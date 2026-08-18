using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using GifMaker.Models;

namespace GifMaker.Services;

/// <summary>
/// 帧精确取帧：
///  - 单帧：输入定位到最近关键帧 + select 按精确时间戳筛选（帧号精确）
///  - 缩略图：一次解码全程按帧号抽样（not(mod(n,K))，与绝对帧号对齐）
/// </summary>
public sealed class FrameExtractor
{
    private readonly string _ffmpeg;

    public FrameExtractor(string ffmpeg) => _ffmpeg = ffmpeg;

    public async Task<BitmapSource?> ExtractFrameAsync(
        VideoInfo info, int index, int maxDim, CancellationToken ct)
    {
        if (index < 0 || index >= info.FrameCount) return null;

        int key = info.NearestKeyFrame(index);
        string kPts = F(info.FramePts[key]);

        var args = new List<string>
        {
            "-v", "error",
            "-ss", kPts,
            "-i", info.FilePath,
            "-vf", $"select='eq(n,{index - key})',scale='min(iw,{maxDim})':-2",
            "-frames:v", "1",
            "-f", "image2pipe",
            "-vcodec", "png",
            "-"
        };

        var ms = new MemoryStream();
        var res = await ProcessRunner.RunAsync(_ffmpeg, args, null, ct, stdoutSink: ms);
        if (res.ExitCode != 0 || ms.Length < 8) return null;
        return DecodePng(ms.ToArray());
    }

    /// <summary>按帧号抽样缩略图，返回与帧号对齐的 (Stride, 缩略图列表)。</summary>
    public async Task<(int Stride, List<BitmapSource> Thumbs)> ExtractThumbnailsAsync(
        VideoInfo info, int maxThumbs, int thumbWidth,
        IProgress<double>? progress, CancellationToken ct)
    {
        int total = info.FrameCount;
        int stride = Math.Max(1, (int)Math.Ceiling(total / (double)maxThumbs));
        int thumbCount = (total + stride - 1) / stride;

        var args = new List<string>
        {
            "-v", "error",
            "-progress", "pipe:2",
            "-i", info.FilePath,
            "-vf", $"select='not(mod(n,{stride}))',scale={thumbWidth}:-2",
            "-fps_mode", "vfr",
            "-f", "image2pipe",
            "-vcodec", "png",
            "-"
        };

        var stdout = new MemoryStream();
        var stderrLines = new List<string>();
        var res = await ProcessRunner.RunAsync(
            _ffmpeg, args, null, ct,
            stdoutSink: stdout,
            onStderrLine: l => { if (l != null) stderrLines.Add(l); });

        if (res.ExitCode != 0)
            throw new InvalidOperationException($"生成缩略图失败: {res.ErrorTail}");

        ReportProgress(stderrLines, info, progress);

        var chunks = PngStream.Split(stdout.ToArray());
        var thumbs = new List<BitmapSource>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var b = DecodePng(chunk);
            if (b != null) thumbs.Add(b);
        }
        return (stride, thumbs);
    }

    private static void ReportProgress(List<string> lines, VideoInfo info, IProgress<double>? progress)
    {
        if (progress == null || info.DurationUs <= 0) return;
        double last = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
            {
                last = Math.Clamp(us / (double)info.DurationUs, 0, 1);
            }
        }
        progress.Report(last);
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var f = dec.Frames[0];
            f.Freeze();
            return f;
        }
        catch
        {
            return null;
        }
    }

    private static string F(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
}

/// <summary>将 ffmpeg image2pipe 输出的多张拼接 PNG 拆分为独立块。</summary>
internal static class PngStream
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static List<byte[]> Split(byte[] data)
    {
        var result = new List<byte[]>();
        int i = 0;
        while (i + Signature.Length <= data.Length)
        {
            if (MatchesSignature(data, i))
            {
                int pos = i + Signature.Length;
                int end = -1;
                while (pos + 12 <= data.Length)
                {
                    int len = ReadInt32BE(data, pos);
                    string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    int total = 12 + len;
                    if (pos + total > data.Length) break;
                    if (type == "IEND") { end = pos + total; break; }
                    pos += total;
                }
                if (end < 0) break;
                var chunk = new byte[end - i];
                Array.Copy(data, i, chunk, 0, end - i);
                result.Add(chunk);
                i = end;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    private static bool MatchesSignature(byte[] data, int i)
    {
        for (int k = 0; k < Signature.Length; k++)
            if (data[i + k] != Signature[k]) return false;
        return true;
    }

    private static int ReadInt32BE(byte[] d, int o) =>
        (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}