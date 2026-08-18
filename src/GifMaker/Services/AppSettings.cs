using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GifMaker.Services;

/// <summary>应用设置，持久化到应用目录 settings.json（旧版 %APPDATA% 配置会自动迁移）。</summary>
public sealed class AppSettings
{
    private static readonly string Dir = AppContext.BaseDirectory;

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly string LegacyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GifMaker");

    private static readonly string LegacyFilePath = Path.Combine(LegacyDir, "settings.json");

    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GIF");

    public int LastMaxWidth { get; set; }
    public int LastFps { get; set; } = 15;
    public int LastColors { get; set; } = 256;
    public bool LastDithering { get; set; } = true;
    public bool LastInfiniteLoop { get; set; } = true;
    public bool LastLoopPlayback { get; set; } = true;

    [JsonIgnore]
    public static AppSettings Instance { get; } = Load();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略持久化失败
        }
    }

    private static AppSettings Load()
    {
        try
        {
            string source = FilePath;
            if (!File.Exists(source) && File.Exists(LegacyFilePath))
            {
                source = LegacyFilePath;
            }
            if (File.Exists(source))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source)) ?? new AppSettings();
        }
        catch
        {
            // 损坏则重置
        }
        return new AppSettings();
    }
}