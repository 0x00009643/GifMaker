using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace GifMaker.Services;

/// <summary>
/// 自动下载并解压 FFmpeg（gyan.dev essentials 构建）到应用目录 ffmpeg\bin。
/// </summary>
public sealed class FFmpegDownloader
{
    private const string DownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private static readonly string DestRoot = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    private static readonly string BinDir = Path.Combine(DestRoot, "bin");
    private static readonly string TempZip = Path.Combine(DestRoot, "download.zip");

    public bool AlreadyInstalled => File.Exists(Path.Combine(BinDir, "ffmpeg.exe")) &&
                                    File.Exists(Path.Combine(BinDir, "ffprobe.exe"));

    public async Task<bool> DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(DestRoot);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GifMaker/1.0");

            using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(TempZip);
                var buf = new byte[256 * 1024];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report(Math.Clamp(done / (double)total, 0, 1));
                }
                await dst.FlushAsync(ct);
            }

            ExtractBinaries();
            File.Delete(TempZip);
            return AlreadyInstalled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try { File.Delete(TempZip); } catch { }
            return false;
        }
    }

    private static void ExtractBinaries()
    {
        Directory.CreateDirectory(BinDir);
        using var zip = ZipFile.OpenRead(TempZip);
        foreach (var entry in zip.Entries)
        {
            string name = Path.GetFileName(entry.FullName);
            if (name is "ffmpeg.exe" or "ffprobe.exe" or "ffplay.exe")
            {
                string dest = Path.Combine(BinDir, name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
    }
}