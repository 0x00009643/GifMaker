using System.Globalization;
using System.IO;
using System.Text.Json;
using GifMaker.Models;

namespace GifMaker.Services;

/// <summary>
/// ffprobe 两阶段探测：
///  1) 快速读取元数据（时长/尺寸/帧率/旋转）
///  2) 流式全量扫描逐帧（key_frame + pts），实时报告百分比进度，取消时杀进程
/// 全量解码保证 VFR 视频也帧级精确。
/// </summary>
public sealed class MediaProbeService
{
    private readonly string _ffprobe;

    public MediaProbeService(string ffmpeg, string ffprobe)
    {
        _ffprobe = ffprobe;
    }

    public async Task<VideoInfo> ProbeAsync(
        string path, IProgress<double>? progress, CancellationToken ct)
    {
        var meta = await ReadMetaAsync(path, ct);

        var scanArgs = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_frames",
            "-show_entries", "frame=key_frame,best_effort_timestamp_time",
            "-of", "csv",
            path
        };

        var pts = new List<double>(8192);
        var keys = new List<int>(64);

        var res = await ProcessRunner.RunAsync(
            _ffprobe, scanArgs, null, ct,
            onStdoutLine: line =>
            {
                if (line == null || !line.StartsWith("frame,", StringComparison.Ordinal)) return;
                var parts = line.Split(',');
                if (parts.Length < 3) return;
                if (parts[1] == "1") keys.Add(pts.Count);
                double t;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                    t = pts.Count > 0 ? pts[^1] + 1.0 / meta.FrameRate : 0;
                pts.Add(t);
                if (meta.DurationUs > 0)
                    progress?.Report(Math.Clamp(t * 1_000_000.0 / meta.DurationUs, 0, 1));
            });

        if (res.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 失败: {res.ErrorTail}");

        if (pts.Count == 0)
            throw new InvalidOperationException("未找到视频帧");

        if (keys.Count == 0) keys.Add(0);
        progress?.Report(1);

        return new VideoInfo
        {
            FilePath = path,
            Width = meta.Width,
            Height = meta.Height,
            DisplayWidth = meta.Rotate % 180 == 0 ? meta.Width : meta.Height,
            DisplayHeight = meta.Rotate % 180 == 0 ? meta.Height : meta.Width,
            FrameRate = meta.FrameRate,
            DurationUs = meta.DurationUs,
            FramePts = pts.ToArray(),
            KeyFrames = keys.ToArray()
        };
    }

    private sealed record Meta(int Width, int Height, double FrameRate, long DurationUs, int Rotate);

    private async Task<Meta> ReadMetaAsync(string path, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate",
            "-show_entries", "stream_tags=rotate",
            "-show_entries", "format=duration",
            "-of", "json",
            path
        };

        var ms = new MemoryStream();
        var res = await ProcessRunner.RunAsync(_ffprobe, args, null, ct, stdoutSink: ms);
        if (res.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 失败: {res.ErrorTail}");

        int width = 0, height = 0, rotate = 0;
        double fps = 0, duration = 0;
        try
        {
            var root = JsonDocument.Parse(ms.ToArray()).RootElement;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var st in streams.EnumerateArray())
                {
                    if (st.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (st.TryGetProperty("height", out var h)) height = h.GetInt32();
                    fps = ParseRatio(st, "avg_frame_rate") ?? ParseRatio(st, "r_frame_rate") ?? 0;
                    if (st.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("rotate", out var rot) &&
                        rot.ValueKind == JsonValueKind.String &&
                        int.TryParse(rot.GetString(), out var r))
                        rotate = r;
                    break;
                }
            }
            if (root.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var fd) && fd.ValueKind == JsonValueKind.String &&
                double.TryParse(fd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                duration = d;
        }
        catch
        {
            // 元数据解析失败按默认值处理
        }

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("无法读取视频尺寸");
        if (fps <= 0) fps = 30.0;
        if (duration <= 0) duration = 1.0;
        return new Meta(width, height, fps, (long)(duration * 1_000_000), rotate);
    }

    private static double? ParseRatio(JsonElement st, string key)
    {
        if (!st.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return num / den;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2) ? v2 : null;
    }
}