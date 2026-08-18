using System.Globalization;
using System.IO;
using GifMaker.Models;

namespace GifMaker.Services;

/// <summary>
/// GIF 导出：单条 filter_complex 完成帧号裁剪 → 定帧率 → 裁剪 → 缩放 → palette 两遍编码。
/// 进度从 ffmpeg -progress 的 out_time_us 换算。
/// </summary>
public sealed class GifExporter
{
    private readonly string _ffmpeg;

    public GifExporter(string ffmpeg) => _ffmpeg = ffmpeg;

    public sealed record ExportResult(string OutputPath, int FrameCount, double DurationSec, long SizeBytes);

    /// <summary>导出帧数预估（fps 重采样后）。</summary>
    public static int EstimateFrameCount(VideoInfo info, int startFrame, int endFrame, int targetFps)
    {
        int srcFps = Math.Max(1, (int)Math.Round(info.FrameRate));
        int fps = targetFps > 0 ? targetFps : srcFps;
        int frames = endFrame - startFrame + 1;
        return Math.Max(1, (int)Math.Round(frames * fps / (double)srcFps));
    }

    public async Task<ExportResult> ExportAsync(
        VideoInfo info,
        int startFrame,
        int endFrame,
        CropRect crop,
        ExportSettings s,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        int srcFps = Math.Max(1, (int)Math.Round(info.FrameRate));
        int fps = s.Fps > 0 ? s.Fps : srcFps;
        int S = Math.Clamp(startFrame, 0, info.FrameCount - 1);
        int E = Math.Clamp(endFrame, S, info.FrameCount - 1);
        int frames = E - S + 1;

        var px = crop.ToPixels(info.DisplayWidth, info.DisplayHeight);

        int K = info.NearestKeyFrame(S);
        string kPts = F(info.FramePts[K]);

        string scale = s.MaxWidth > 0 && px.Width != s.MaxWidth
            ? $",scale={s.MaxWidth}:-2:flags=lanczos"
            : "";
        string dither = s.Dithering ? "bayer:bayer_scale=5" : "none";

        string fc =
            $"[0:v]trim=start_frame={S - K}:end_frame={E - K + 1},setpts=PTS-STARTPTS," +
            $"fps={fps},crop={px.Width}:{px.Height}:{px.X}:{px.Y}{scale}," +
            $"split[a][b];[a]palettegen=max_colors={s.Colors}[p];" +
            $"[b][p]paletteuse=dither={dither}";

        var args = new List<string>
        {
            "-v", "error",
            "-ss", kPts,
            "-i", info.FilePath,
            "-filter_complex", fc,
            "-an",
            "-f", "gif",
            "-loop", s.InfiniteLoop ? "0" : "1",
            "-progress", "pipe:2",
            "-y",
            outputPath
        };

        double totalFrames = EstimateFrameCount(info, S, E, fps);
        var stderrLines = new List<string>();

        RunResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                _ffmpeg, args, null, ct,
                onStderrLine: l =>
                {
                    if (l == null) return;
                    stderrLines.Add(l);
                    if (l.StartsWith("frame=", StringComparison.Ordinal) &&
                        int.TryParse(l.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                    {
                        progress?.Report(Math.Clamp(frame / totalFrames, 0, 1));
                    }
                });
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw;
        }

        if (result.ExitCode != 0)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw new InvalidOperationException(
                $"FFmpeg 导出失败:{Environment.NewLine}{result.ErrorTail}");
        }

        var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        return new ExportResult(
            outputPath,
            EstimateFrameCount(info, S, E, fps),
            frames / (double)srcFps,
            size);
    }

    private static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
}