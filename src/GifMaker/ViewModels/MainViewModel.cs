using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GifMaker.Models;
using GifMaker.Services;

namespace GifMaker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private FfTools? _tools;
    private MediaProbeService? _probe;
    private FrameExtractor? _frames;
    private GifExporter? _exporter;
    private readonly FFmpegDownloader _downloader = new();

    private readonly FrameCache _cache = new(48);
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _downloadCts;
    private int _navSeq;

    public MainViewModel()
    {
        _settings = AppSettings.Instance;
        _outputFolder = _settings.OutputFolder;
        _customWidth = 480;
        _fps = _settings.LastFps;
        _colors = _settings.LastColors;
        _dithering = _settings.LastDithering;
        _infiniteLoop = _settings.LastInfiniteLoop;
        _loopPlayback = _settings.LastLoopPlayback;
    }

    // ---------- FFmpeg 状态 ----------

    [ObservableProperty] private bool _ffmpegReady;
    [ObservableProperty] private string _ffmpegStatus = "正在检测 FFmpeg…";
    [ObservableProperty] private string _ffmpegPathDisplay = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;

    public async Task InitializeAsync()
    {
        var tools = await Task.Run(() => FFmpegLocator.Locate(_settings));
        if (tools == null)
        {
            FfmpegStatus = "未找到 ffmpeg / ffprobe";
            FfmpegPathDisplay = "未找到";
            return;
        }
        _tools = tools;
        _probe = new MediaProbeService(tools.Ffmpeg, tools.Ffprobe);
        _frames = new FrameExtractor(tools.Ffmpeg);
        _exporter = new GifExporter(tools.Ffmpeg);
        FfmpegReady = true;
        FfmpegStatus = tools.Version;
        FfmpegPathDisplay = tools.Ffmpeg;
    }

    public async Task DownloadFfmpegAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        DownloadProgress = 0;
        Status = "正在下载 FFmpeg…";
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                Status = $"正在下载 FFmpeg {(p * 100):0}%";
            });
            bool ok = await _downloader.DownloadAsync(progress, _downloadCts.Token);
            if (ok)
            {
                Status = "FFmpeg 下载完成，正在检测…";
                await InitializeAsync();
            }
            else
            {
                Status = "FFmpeg 下载失败，请检查网络或手动安装";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已取消下载";
        }
        catch (Exception ex)
        {
            Status = "FFmpeg 下载失败: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
        }
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    public bool CanClickDownload => !FfmpegReady;

    public void SetFfmpegPaths(string ffmpeg, string ffprobe)
    {
        _settings.FfmpegPath = ffmpeg;
        _settings.FfprobePath = ffprobe;
        _ = InitializeAsync();
    }

    public bool IsFfmpegMissing => !FfmpegReady;

    // ---------- 视频状态 ----------

    [ObservableProperty] private VideoInfo? _info;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private bool _probeIndeterminate = true;
    [ObservableProperty] private double _probeProgress;
    [ObservableProperty] private string _probeStatusText = "正在读取视频信息…";

    [ObservableProperty] private int _startFrame;
    [ObservableProperty] private int _endFrame;
    [ObservableProperty] private int _currentFrame;

    [ObservableProperty] private CropRect _crop = CropRect.Full;

    [ObservableProperty] private bool _isStepMode;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loopPlayback;
    [ObservableProperty] private BitmapSource? _currentPreview;

    [ObservableProperty] private string _status = "就绪";

    public int FrameCount => Info?.FrameCount ?? 0;
    public double SourceFps => Info?.FrameRate ?? 30.0;

    public string FileName => Info?.FileName ?? "";
    public string InfoText => Info == null
        ? "未加载视频"
        : $"{Info.DisplayWidth}×{Info.DisplayHeight} · {Info.FrameCount} 帧 · {Info.FrameRate:0.##} fps · {Info.DurationMs / 1000.0:0.0}s";

    public long CurrentTimeMs => Info == null ? 0 : (long)(Info.FramePts[CurrentFrame] * 1000);
    public long StartTimeMs => Info == null ? 0 : (long)(Info.FramePts[StartFrame] * 1000);
    public long EndTimeMs => Info == null ? 0 : (long)(Info.FramePts[EndFrame] * 1000);

    /// <summary>区间播放起点（毫秒）。</summary>
    public long PlaybackStartMs => StartTimeMs;

    /// <summary>区间播放终点（毫秒，含终点帧）。</summary>
    public long PlaybackEndMs => Info == null ? 0
        : (long)(Info.FramePts[Math.Min(EndFrame + 1, Info.FrameCount - 1)] * 1000);

    public string CurrentTimeText => TimeStr(CurrentTimeMs);
    public string StartTimeText => TimeStr(StartTimeMs);
    public string EndTimeText => TimeStr(EndTimeMs);

    public string FrameLabel => $"第 {StartFrame + 1} / {EndFrame + 1} 帧";

    // ---------- 裁剪（像素数值输入） ----------

    public int CropX => Info == null ? 0 : (int)Math.Round(Crop.Left * DisplayWidth);
    public int CropY => Info == null ? 0 : (int)Math.Round(Crop.Top * DisplayHeight);
    public int CropW => Info == null ? 0 : (int)Math.Round(Crop.Width * DisplayWidth);
    public int CropH => Info == null ? 0 : (int)Math.Round(Crop.Height * DisplayHeight);

    public int DisplayWidth => Info?.DisplayWidth ?? 0;
    public int DisplayHeight => Info?.DisplayHeight ?? 0;
    public double ViewAspect => DisplayHeight > 0 ? DisplayWidth / (double)DisplayHeight : 1.0;

    [ObservableProperty] private string _cropRatioChoice = "Free";

    public string[] CropRatioChoices { get; } = { "Free", "1:1", "4:3", "16:9", "Source" };

    public bool IsCustomResolution => ResolutionMode == ResolutionMode.Custom;

    public double? LockedCropRatio => CropRatioChoice switch
    {
        "1:1" => 1.0,
        "4:3" => 4.0 / 3.0,
        "16:9" => 16.0 / 9.0,
        "Source" => ViewAspect,
        _ => null
    };

    // ---------- 导出设置 ----------

    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private ResolutionMode _resolutionMode;
    [ObservableProperty] private int _customWidth;
    [ObservableProperty] private int _fps;
    [ObservableProperty] private int _colors;
    [ObservableProperty] private bool _dithering;
    [ObservableProperty] private bool _infiniteLoop;

    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string? _exportResultPath;

    public string[] FpsChoices { get; } = { "0", "8", "10", "12", "15", "20", "24", "30" };
    public string[] ColorChoices { get; } = { "64", "128", "192", "256" };

    public int EffectiveFps => Fps > 0 ? Fps : (int)Math.Max(1, Math.Round(SourceFps));

    public string ExportEstimateText
    {
        get
        {
            if (Info == null) return "";
            int maxW = ExportSettings.WidthFor(ResolutionMode, CustomWidth);
            var px = Crop.ToPixels(DisplayWidth, DisplayHeight);
            int outW = maxW > 0 ? maxW : px.Width;
            int outH = maxW > 0 ? Math.Max(2, (int)Math.Round(px.Height * maxW / (double)px.Width)) : px.Height;
            int frames = GifExporter.EstimateFrameCount(Info, StartFrame, EndFrame, EffectiveFps);
            double dur = (EndFrame - StartFrame + 1) / SourceFps;
            return $"输出 {outW}×{outH} · {frames} 帧 · {EffectiveFps} fps · 时长 {dur:0.00}s";
        }
    }

    public bool CanExport => IsLoaded && FfmpegReady && !IsExporting;

    // ---------- 缩略图 ----------

    private List<BitmapSource> _thumbs = new();
    private int _thumbStride = 1;

    public IReadOnlyList<BitmapSource> Thumbs => _thumbs;
    public int ThumbStride => _thumbStride;

    // ---------- 加载视频 ----------

    public async Task OpenAsync(string path)
    {
        if (!FfmpegReady || _probe == null || _frames == null) return;

        _probeCts?.Cancel();
        _exportCts?.Cancel();
        _cache.Clear();
        _thumbs = new List<BitmapSource>();
        _thumbStride = 1;
        OnPropertyChanged(nameof(Thumbs));

        StartFrame = 0;
        EndFrame = 0;
        CurrentFrame = 0;
        Info = null;
        IsLoaded = false;
        IsStepMode = false;
        IsPlaying = false;
        CurrentPreview = null;
        ExportResultPath = null;

        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        IsProbing = true;
        ProbeIndeterminate = true;
        ProbeProgress = 0;
        ProbeStatusText = "正在读取视频信息…";
        Status = "正在扫描视频帧…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProbeIndeterminate = false;
                ProbeProgress = p;
                ProbeStatusText = $"正在扫描视频帧 {p * 100:0}%";
            });
            var info = await _probe.ProbeAsync(path, progress, ct);
            Info = info;
            StartFrame = 0;
            EndFrame = Math.Max(0, info.FrameCount - 1);
            CurrentFrame = 0;
            Crop = CropRect.Full;
            IsLoaded = true;
            Status = $"已加载 {info.FileName}";
            NotifyDerived();

            _ = LoadThumbnailsAsync(info, ct);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消加载";
        }
        catch (Exception ex)
        {
            Status = "加载失败: " + ex.Message;
        }
        finally
        {
            IsProbing = false;
        }
    }

    public void CancelProbe() => _probeCts?.Cancel();

    private async Task LoadThumbnailsAsync(VideoInfo info, CancellationToken ct)
    {
        try
        {
            Status = "正在生成缩略图…";
            var progress = new Progress<double>(p => Status = $"正在生成缩略图 {(p * 100):0}%");
            var (stride, thumbs) = await _frames!.ExtractThumbnailsAsync(info, 200, 160, progress, ct);
            _thumbStride = stride;
            _thumbs = thumbs;
            OnPropertyChanged(nameof(Thumbs));
            OnPropertyChanged(nameof(ThumbStride));
            if (!ct.IsCancellationRequested) Status = "就绪";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "缩略图生成失败: " + ex.Message;
        }
    }

    // ---------- 帧导航 ----------

    public async Task StepAsync(int delta)
    {
        if (!IsLoaded || IsProbing) return;
        int seq = ++_navSeq;
        SetCurrentFrame(CurrentFrame + delta, stepMode: true);
        await ShowPreviewFrameAsync(CurrentFrame, seq);
    }

    public async Task SeekToAsync(int frame)
    {
        if (!IsLoaded) return;
        int seq = ++_navSeq;
        SetCurrentFrame(frame, stepMode: true);
        await ShowPreviewFrameAsync(CurrentFrame, seq);
    }

    public async Task ShowPreviewFrameAsync(int frame, int seq)
    {
        if (!IsLoaded || _frames == null || frame < 0 || frame >= FrameCount) return;
        var cached = _cache.Get(frame);
        if (cached != null)
        {
            CurrentPreview = cached;
        }
        else
        {
            var b = await _frames.ExtractFrameAsync(Info!, frame, 1280, CancellationToken.None);
            if (b == null) return;
            if (seq != _navSeq) return;
            _cache.Put(frame, b);
            CurrentPreview = b;
        }
        _ = PrefetchAsync(frame + 1);
    }

    private async Task PrefetchAsync(int frame)
    {
        if (!IsLoaded || _frames == null || frame < 0 || frame >= FrameCount) return;
        if (_cache.Get(frame) != null) return;
        try
        {
            var b = await _frames.ExtractFrameAsync(Info!, frame, 1280, CancellationToken.None);
            if (b != null) _cache.Put(frame, b);
        }
        catch { }
    }

    public void SetCurrentFrame(int frame, bool stepMode = false)
    {
        if (!IsLoaded) return;
        CurrentFrame = Math.Clamp(frame, 0, FrameCount - 1);
        IsStepMode = stepMode;
    }

    /// <summary>播放中同步播放头（时间 → 最近帧），不切步进模式。</summary>
    public void SetPlayhead(double timeMs)
    {
        if (!IsLoaded) return;
        int f = Info!.FrameIndexAtTime(timeMs);
        if (f != CurrentFrame)
        {
            CurrentFrame = f;
            OnPropertyChanged(nameof(CurrentTimeText));
            OnPropertyChanged(nameof(FrameLabel));
        }
    }

    public void SetPlaying(bool playing) => IsPlaying = playing;

    public void ExitStepMode() => IsStepMode = false;

    // ---------- 时长裁剪 ----------

    public void SetTrimStart() { if (CurrentFrame <= EndFrame) StartFrame = CurrentFrame; }
    public void SetTrimEnd() { if (CurrentFrame >= StartFrame) EndFrame = CurrentFrame; }

    public void SetTrimStart(int frame) => StartFrame = Math.Clamp(frame, 0, EndFrame);
    public void SetTrimEnd(int frame) => EndFrame = Math.Clamp(frame, StartFrame, FrameCount - 1);

    // ---------- 画面裁剪 ----------

    public void SetCrop(CropRect rect)
    {
        if (rect != Crop) Crop = rect.Clamped();
    }

    public void ResetCrop()
    {
        Crop = CropRect.Full;
        CropRatioChoice = "Free";
    }

    public void ApplyCropRatio(string choice)
    {
        CropRatioChoice = choice;
        Crop = Crop.WithRatio(LockedCropRatio, ViewAspect);
    }

    public void SetCropPixels(int x, int y, int w, int h)
    {
        if (Info == null) return;
        int dw = DisplayWidth, dh = DisplayHeight;
        if (dw <= 0 || dh <= 0) return;
        int rx = Math.Clamp(x, 0, dw - 2);
        int ry = Math.Clamp(y, 0, dh - 2);
        int rw = Math.Clamp(w, 2, dw - rx);
        int rh = Math.Clamp(h, 2, dh - ry);
        var r = new CropRect(rx / (double)dw, ry / (double)dh, (rx + rw) / (double)dw, (ry + rh) / (double)dh);
        if (r != Crop) Crop = r;
    }

    // ---------- 导出 ----------

    public async Task ExportAsync()
    {
        if (!CanExport || _exporter == null || Info == null) return;

        int maxW = ExportSettings.WidthFor(ResolutionMode, CustomWidth);
        var settings = new ExportSettings(maxW, Fps, Colors, Dithering, InfiniteLoop);
        string fileName = $"GIF_{DateTime.Now:yyyyMMdd_HHmmss}.gif";
        string dir = OutputFolder;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { Status = "无法创建输出目录: " + ex.Message; return; }
        string outPath = Path.Combine(dir, fileName);

        IsExporting = true;
        ExportProgress = 0;
        ExportResultPath = null;
        Status = "正在导出 GIF…";

        _exportCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => ExportProgress = p);
            var result = await _exporter.ExportAsync(
                Info, StartFrame, EndFrame, Crop, settings, outPath, progress, _exportCts.Token);
            ExportProgress = 1;
            ExportResultPath = result.OutputPath;
            Status = $"导出完成: {result.OutputPath}";
        }
        catch (OperationCanceledException)
        {
            Status = "导出已取消";
        }
        catch (Exception ex)
        {
            Status = "导出失败: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    public void CancelExport() => _exportCts?.Cancel();

    // ---------- 设置 ----------

    public void SaveSettings()
    {
        _settings.OutputFolder = OutputFolder;
        _settings.LastMaxWidth = ExportSettings.WidthFor(ResolutionMode, CustomWidth);
        _settings.LastFps = Fps;
        _settings.LastColors = Colors;
        _settings.LastDithering = Dithering;
        _settings.LastInfiniteLoop = InfiniteLoop;
        _settings.LastLoopPlayback = LoopPlayback;
        _settings.Save();
    }

    // ---------- 派生通知 ----------

    partial void OnInfoChanged(VideoInfo? value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(ViewAspect));
        OnPropertyChanged(nameof(LockedCropRatio));
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnStartFrameChanged(int value)
    {
        OnPropertyChanged(nameof(StartTimeText));
        OnPropertyChanged(nameof(PlaybackStartMs));
        OnPropertyChanged(nameof(PlaybackEndMs));
        OnPropertyChanged(nameof(ExportEstimateText));
        OnPropertyChanged(nameof(FrameLabel));
    }

    partial void OnEndFrameChanged(int value)
    {
        OnPropertyChanged(nameof(EndTimeText));
        OnPropertyChanged(nameof(PlaybackEndMs));
        OnPropertyChanged(nameof(ExportEstimateText));
        OnPropertyChanged(nameof(FrameLabel));
    }

    partial void OnCurrentFrameChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(ExportEstimateText));
    }

    partial void OnCropChanged(CropRect value)
    {
        OnPropertyChanged(nameof(CropX));
        OnPropertyChanged(nameof(CropY));
        OnPropertyChanged(nameof(CropW));
        OnPropertyChanged(nameof(CropH));
        OnPropertyChanged(nameof(ExportEstimateText));
    }

    partial void OnCropRatioChoiceChanged(string value) { OnPropertyChanged(nameof(LockedCropRatio)); }

    partial void OnResolutionModeChanged(ResolutionMode value)
    {
        OnPropertyChanged(nameof(ExportEstimateText));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(IsCustomResolution));
    }

    partial void OnFpsChanged(int value)
    {
        OnPropertyChanged(nameof(EffectiveFps));
        OnPropertyChanged(nameof(ExportEstimateText));
    }

    partial void OnCustomWidthChanged(int value)
    {
        OnPropertyChanged(nameof(ExportEstimateText));
    }

    partial void OnOutputFolderChanged(string value) { }

    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(InfoText));
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClickDownload));
        OnPropertyChanged(nameof(DownloadButtonText));
    }

    public string DownloadButtonText => IsDownloading ? "取消下载" : "自动下载 ffmpeg…";

    partial void OnFfmpegReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFfmpegMissing));
        OnPropertyChanged(nameof(CanExport));
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(ViewAspect));
        OnPropertyChanged(nameof(StartTimeText));
        OnPropertyChanged(nameof(EndTimeText));
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(FrameLabel));
        OnPropertyChanged(nameof(CropX));
        OnPropertyChanged(nameof(CropY));
        OnPropertyChanged(nameof(CropW));
        OnPropertyChanged(nameof(CropH));
        OnPropertyChanged(nameof(ExportEstimateText));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(LockedCropRatio));
        OnPropertyChanged(nameof(FrameCount));
    }

    private static string TimeStr(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100:0}";
    }

    private sealed class FrameCache
    {
        private readonly int _capacity;
        private readonly Dictionary<int, BitmapSource> _map = new();
        private readonly LinkedList<int> _order = new();

        public FrameCache(int capacity) => _capacity = capacity;

        public BitmapSource? Get(int i)
        {
            if (_map.TryGetValue(i, out var b))
            {
                _order.Remove(i);
                _order.AddFirst(i);
                return b;
            }
            return null;
        }

        public void Put(int i, BitmapSource b)
        {
            if (_map.ContainsKey(i)) return;
            _map[i] = b;
            _order.AddFirst(i);
            while (_order.Count > _capacity)
            {
                int last = _order.Last!.Value;
                _order.RemoveLast();
                _map.Remove(last);
            }
        }

        public void Clear() { _map.Clear(); _order.Clear(); }
    }
}