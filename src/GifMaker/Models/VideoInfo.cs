using System.IO;

namespace GifMaker.Models;

/// <summary>
/// 视频元信息：ffprobe 全量扫描得到的每帧时间戳与关键帧位置，
/// 保证裁剪/取帧精确到帧（含 VFR 视频）。
/// </summary>
public sealed class VideoInfo
{
    public required string FilePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int DisplayWidth { get; init; }
    public required int DisplayHeight { get; init; }
    public required double FrameRate { get; init; }
    public required long DurationUs { get; init; }
    public required double[] FramePts { get; init; }
    public required int[] KeyFrames { get; init; }

    public int FrameCount => FramePts.Length;
    public string FileName => Path.GetFileName(FilePath);
    public long DurationMs => DurationUs / 1000;
    public double DurationSeconds => DurationUs / 1_000_000.0;

    public double FrameTimeSeconds(int index) =>
        index >= 0 && index < FramePts.Length ? FramePts[index] : 0.0;

    /// <summary>小于等于 index 的最近关键帧（用于输入定位 + 帧号补偿）。</summary>
    public int NearestKeyFrame(int index)
    {
        int lo = 0, hi = KeyFrames.Length - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (KeyFrames[mid] <= index) { ans = KeyFrames[mid]; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>时间(ms) → 最近帧索引（二分查找）。</summary>
    public int FrameIndexAtTime(double timeMs)
    {
        double t = timeMs / 1000.0;
        int lo = 0, hi = FramePts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (FramePts[mid] <= t) lo = mid + 1;
            else hi = mid - 1;
        }
        return Math.Clamp(hi, 0, FramePts.Length - 1);
    }
}
