using System.Diagnostics;
using System.IO;

namespace GifMaker.Services;

public sealed record FfTools(string Ffmpeg, string Ffprobe, string Version);

/// <summary>
/// 定位系统/手动安装的 ffmpeg 与 ffprobe：
/// 1) 设置中保存的路径  2) 应用当前目录（含 bin 子目录）  3) PATH  4) 常见安装目录。
/// </summary>
public static class FFmpegLocator
{
    private static readonly string[] CommonDirs =
    {
        @"C:\ffmpeg\bin",
        @"C:\Program Files\ffmpeg\bin",
        @"C:\Program Files (x86)\ffmpeg\bin",
        @"C:\tools\ffmpeg\bin",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ffmpeg\bin"),
    };

    public static FfTools? Locate(AppSettings settings)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
            candidates.Add(Path.GetDirectoryName(settings.FfmpegPath) ?? "");

        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "bin"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"));

        candidates.AddRange(Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());

        candidates.AddRange(CommonDirs);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || !seen.Add(dir)) continue;
            if (!Directory.Exists(dir)) continue;

            string ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            string ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) continue;

            string? version = GetVersion(ffmpeg);
            if (version == null) continue;

            return new FfTools(ffmpeg, ffprobe, version);
        }
        return null;
    }

    private static string? GetVersion(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string line = p.StandardOutput.ReadLine() ?? p.StandardError.ReadLine() ?? "";
            p.WaitForExit(3000);
            return line.Trim();
        }
        catch
        {
            return null;
        }
    }
}