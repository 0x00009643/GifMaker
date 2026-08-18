using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GifMaker.Models;
using GifMaker.ViewModels;
using Microsoft.Win32;

namespace GifMaker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _playheadTimer;
    private double? _pendingPosition;
    private double _zoom = 1.0;
    private double _zoomX, _zoomY;

    private static readonly int[] FpsValues = { 0, 8, 10, 12, 15, 20, 24, 30 };
    private static readonly int[] ColorValues = { 64, 128, 192, 256 };

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _ = _vm.InitializeAsync();

        Player.MediaOpened += OnMediaOpened;
        Player.MediaEnded += (_, _) =>
        {
            if (_vm.LoopPlayback && _vm.IsLoaded)
            {
                Player.Position = TimeSpan.FromMilliseconds(_vm.PlaybackStartMs);
                Player.Play();
                _vm.SetPlaying(true);
            }
            else
            {
                _vm.SetPlaying(false);
                UpdatePlayGlyphs();
            }
        };

        _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _playheadTimer.Tick += (_, _) =>
        {
            if (Player.NaturalDuration.HasTimeSpan && _vm.IsPlaying)
            {
                double endMs = _vm.PlaybackEndMs;
                if (Player.Position.TotalMilliseconds >= endMs)
                {
                    if (_vm.LoopPlayback)
                    {
                        Player.Position = TimeSpan.FromMilliseconds(_vm.PlaybackStartMs);
                    }
                    else
                    {
                        Player.Pause();
                        _vm.SetPlaying(false);
                    }
                }
                else
                {
                    _vm.SetPlayhead(Player.Position.TotalMilliseconds);
                }
            }
        };

        PreviewHost.SizeChanged += (_, _) => FitPreview();

        PreviewHost.MouseWheel += OnPreviewMouseWheel;
        PreviewBox.MouseLeftButtonDown += OnPreviewMouseDown;
        HScroll.Scroll += OnHScroll;
        VScroll.Scroll += OnVScroll;

        Timeline.SeekRequested += f => _ = _vm.SeekToAsync(f);
        Timeline.TrimStartChanged += f => _vm.SetTrimStart(f);
        Timeline.TrimEndChanged += f => _vm.SetTrimEnd(f);

        RatioCombo.ItemsSource = _vm.CropRatioChoices;
        ResCombo.ItemsSource = new[] { "原尺寸", "480", "640", "720", "自定义" };
        FpsCombo.ItemsSource = new[] { "原帧率", "8 fps", "10 fps", "12 fps", "15 fps", "20 fps", "24 fps", "30 fps" };
        ColorCombo.ItemsSource = ColorValues.Select(c => c + " 色").ToArray();

        Loaded += (_, _) => SyncCombos();
        UpdatePlayGlyphs();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.SaveSettings();
        _playheadTimer.Stop();
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsLoaded):
                if (_vm.IsLoaded)
                {
                    SetupPlayer();
                    SyncFrameBoxes(force: true);
                    SyncCropBoxes(force: true);
                }
                else
                {
                    ResetZoom();
                    _playheadTimer.Stop();
                    Player.Pause();
                    Player.Stop();
                    Player.Source = null;
                }
                UpdatePlayGlyphs();
                FitPreview();
                break;
            case nameof(MainViewModel.IsStepMode):
                bool step = _vm.IsStepMode;
                StepImage.Visibility = step ? Visibility.Visible : Visibility.Collapsed;
                Player.Visibility = step ? Visibility.Hidden : Visibility.Visible;
                break;
            case nameof(MainViewModel.IsPlaying):
                UpdatePlayGlyphs();
                if (_vm.IsPlaying) _playheadTimer.Start();
                else _playheadTimer.Stop();
                break;
            case nameof(MainViewModel.Thumbs):
                Timeline.SetThumbs(_vm.Thumbs, _vm.ThumbStride);
                break;
            case nameof(MainViewModel.FrameCount):
                SyncFrameBoxes();
                break;
            case nameof(MainViewModel.StartFrame):
                SyncStartBox();
                break;
            case nameof(MainViewModel.EndFrame):
                SyncEndBox();
                break;
            case nameof(MainViewModel.CropX):
            case nameof(MainViewModel.CropY):
            case nameof(MainViewModel.CropW):
            case nameof(MainViewModel.CropH):
                SyncCropBoxes();
                break;
            case nameof(MainViewModel.CropRatioChoice):
                if (RatioCombo != null)
                    RatioCombo.SelectedIndex = Math.Max(0, Array.IndexOf(_vm.CropRatioChoices, _vm.CropRatioChoice));
                break;
            case nameof(MainViewModel.IsExporting):
                ExportProgress.Visibility = _vm.IsExporting ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(MainViewModel.ExportResultPath):
                ResultOpenButton.Visibility = _vm.ExportResultPath != null ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void SetupPlayer()
    {
        Player.Source = new Uri(_vm.Info!.FilePath);
        Player.Stop();
        _vm.ExitStepMode();
    }

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_pendingPosition.HasValue)
            {
                Player.Position = TimeSpan.FromMilliseconds(_pendingPosition.Value);
                _pendingPosition = null;
            }
            else
            {
                Player.Position = TimeSpan.FromMilliseconds(_vm.CurrentTimeMs);
            }
        }
        catch { }
    }

    private void FitPreview()
    {
        if (!_vm.IsLoaded || PreviewHost.ActualWidth < 10 || PreviewHost.ActualHeight < 10) return;
        double a = _vm.DisplayWidth / (double)_vm.DisplayHeight;
        double aw = PreviewHost.ActualWidth
                    - (VScroll.Visibility == Visibility.Visible ? VScroll.Width : 0);
        double ah = PreviewHost.ActualHeight
                    - (HScroll.Visibility == Visibility.Visible ? HScroll.Height : 0);
        double pw = aw, ph = aw / a;
        if (ph > ah) { ph = ah; pw = ah * a; }
        PreviewBox.Width = pw;
        PreviewBox.Height = ph;
        CropOverlay.Width = pw;
        CropOverlay.Height = ph;
        CropOverlay.RenderTransform = PreviewBox.RenderTransform;
    }

    // ---------- 画面缩放（滚轮） ----------

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_vm.IsLoaded) return;
        var pos = e.GetPosition(PreviewBox);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double ns = Math.Clamp(_zoom * factor, 1.0, 8.0);
        if (Math.Abs(ns - _zoom) < 1e-9) return;
        _zoomX = pos.X * (_zoom - ns) + _zoomX;
        _zoomY = pos.Y * (_zoom - ns) + _zoomY;
        _zoom = ns;
        ApplyZoom();
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsLoaded) return;
        if (e.ClickCount == 2 && _zoom > 1.001)
        {
            ResetZoom();
            e.Handled = true;
        }
    }

    private void ApplyZoom()
    {
        ZoomScale.ScaleX = _zoom;
        ZoomScale.ScaleY = _zoom;
        ZoomLabel.Visibility = _zoom > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        if (_zoom > 1.001) ZoomLabelText.Text = $"缩放 {_zoom * 100:0}%";
        SyncScrollBars();
    }

    private void SyncScrollBars()
    {
        double pw = PreviewBox.Width, ph = PreviewBox.Height;
        double vw = PreviewHost.ActualWidth
                    - (VScroll.Visibility == Visibility.Visible ? VScroll.Width : 0);
        double vh = PreviewHost.ActualHeight
                    - (HScroll.Visibility == Visibility.Visible ? HScroll.Height : 0);
        double lx = (vw - pw) * 0.5, ly = (vh - ph) * 0.5;
        double cx = pw * _zoom, cy = ph * _zoom;
        double maxX = Math.Max(0, cx - vw), maxY = Math.Max(0, cy - vh);
        bool h = maxX > 2.5, v = maxY > 2.5;
        double tMinX, tMaxX, tMinY, tMaxY;
        if (cx <= vw + 2.5)
        {
            tMinX = tMaxX = (vw - cx) * 0.5 - lx;
        }
        else
        {
            tMinX = -lx - maxX + 1;
            tMaxX = -lx - 1;
        }
        if (cy <= vh + 2.5)
        {
            tMinY = tMaxY = (vh - cy) * 0.5 - ly;
        }
        else
        {
            tMinY = -ly - maxY + 1;
            tMaxY = -ly - 1;
        }
        _zoomX = Math.Clamp(_zoomX, Math.Min(tMinX, tMaxX), Math.Max(tMinX, tMaxX));
        _zoomY = Math.Clamp(_zoomY, Math.Min(tMinY, tMaxY), Math.Max(tMinY, tMaxY));
        ZoomTranslate.X = _zoomX;
        ZoomTranslate.Y = _zoomY;
        if (HScroll.Visibility != (h ? Visibility.Visible : Visibility.Collapsed) ||
            VScroll.Visibility != (v ? Visibility.Visible : Visibility.Collapsed))
        {
            HScroll.Visibility = h ? Visibility.Visible : Visibility.Collapsed;
            VScroll.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            FitPreview();
        }
        if (h)
        {
            double denom = 2 - maxX;
            HScroll.Maximum = maxX;
            HScroll.ViewportSize = vw;
            HScroll.Value = (_zoomX + lx + 1) * maxX / denom;
        }
        if (v)
        {
            double denom = 2 - maxY;
            VScroll.Maximum = maxY;
            VScroll.ViewportSize = vh;
            VScroll.Value = (_zoomY + ly + 1) * maxY / denom;
        }
    }

    private void OnHScroll(object sender, ScrollEventArgs e)
    {
        double vw = PreviewHost.ActualWidth
                    - (VScroll.Visibility == Visibility.Visible ? VScroll.Width : 0);
        double pw = PreviewBox.Width;
        if (vw < 10) return;
        double maxX = Math.Max(0, pw * _zoom - vw);
        if (maxX <= 2.5) return;
        _zoomX = -lx() - 1 + e.NewValue * (2 - maxX) / maxX;
        ApplyZoom();
    }

    private void OnVScroll(object sender, ScrollEventArgs e)
    {
        double vh = PreviewHost.ActualHeight
                    - (HScroll.Visibility == Visibility.Visible ? HScroll.Height : 0);
        double ph = PreviewBox.Height;
        if (vh < 10) return;
        double maxY = Math.Max(0, ph * _zoom - vh);
        if (maxY <= 2.5) return;
        _zoomY = -ly() - 1 + e.NewValue * (2 - maxY) / maxY;
        ApplyZoom();
    }

    private double lx() => (PreviewHost.ActualWidth
        - (VScroll.Visibility == Visibility.Visible ? VScroll.Width : 0) - PreviewBox.Width) * 0.5;

    private double ly() => (PreviewHost.ActualHeight
        - (HScroll.Visibility == Visibility.Visible ? HScroll.Height : 0) - PreviewBox.Height) * 0.5;

    private void ResetZoom()
    {
        _zoom = 1.0;
        _zoomX = 0;
        _zoomY = 0;
        ApplyZoom();
    }

    // ---------- 播放控制 ----------

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsLoaded) return;
        if (_vm.IsPlaying)
        {
            Player.Pause();
            _vm.SetPlaying(false);
        }
        else
        {
            _vm.ExitStepMode();
            double startMs = _vm.PlaybackStartMs;
            double pos = _vm.CurrentTimeMs;
            if (pos < startMs || pos >= _vm.PlaybackEndMs) pos = startMs;
            if (Player.NaturalDuration.HasTimeSpan)
            {
                Player.Position = TimeSpan.FromMilliseconds(pos);
                _pendingPosition = null;
            }
            else
            {
                _pendingPosition = pos;
            }
            Player.Play();
            _vm.SetPlaying(true);
        }
    }

    private void OnPrevFrame(object sender, RoutedEventArgs e) => _ = _vm.StepAsync(-1);
    private void OnNextFrame(object sender, RoutedEventArgs e) => _ = _vm.StepAsync(1);

    private void UpdatePlayGlyphs()
    {
        PlayGlyph2.Text = _vm.IsPlaying ? "\uE769" : "\uE768";
    }

    // ---------- 时长裁剪 ----------

    private void OnSetTrimStart(object sender, RoutedEventArgs e) => _vm.SetTrimStart();
    private void OnSetTrimEnd(object sender, RoutedEventArgs e) => _vm.SetTrimEnd();

    private void OnStartFrameLostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseInt(StartFrameBox.Text, out int v)) _vm.SetTrimStart(Math.Max(0, v - 1));
        SyncStartBox();
    }

    private void OnEndFrameLostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseInt(EndFrameBox.Text, out int v)) _vm.SetTrimEnd(Math.Max(0, v - 1));
        SyncEndBox();
    }

    private void SyncStartBox(bool force = false)
    {
        if (StartFrameBox != null && (force || !StartFrameBox.IsKeyboardFocusWithin))
            StartFrameBox.Text = (_vm.StartFrame + 1).ToString();
    }

    private void SyncEndBox(bool force = false)
    {
        if (EndFrameBox != null && (force || !EndFrameBox.IsKeyboardFocusWithin))
            EndFrameBox.Text = (_vm.EndFrame + 1).ToString();
    }

    private void SyncFrameBoxes(bool force = false)
    {
        SyncStartBox(force);
        SyncEndBox(force);
    }

    // ---------- 画面裁剪 ----------

    private void OnRatioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RatioCombo.SelectedItem is string choice && _vm.IsLoaded)
            _vm.ApplyCropRatio(choice);
    }

    private void OnCropPxLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsLoaded) return;
        int x = ParseOr(CropXBox.Text, _vm.CropX);
        int y = ParseOr(CropYBox.Text, _vm.CropY);
        int w = ParseOr(CropWBox.Text, _vm.CropW);
        int h = ParseOr(CropHBox.Text, _vm.CropH);
        _vm.SetCropPixels(x, y, w, h);
        SyncCropBoxes();
    }

    private void SyncCropBoxes(bool force = false)
    {
        if (CropXBox == null || !_vm.IsLoaded) return;
        if (force || !CropXBox.IsKeyboardFocusWithin) CropXBox.Text = _vm.CropX.ToString();
        if (force || !CropYBox.IsKeyboardFocusWithin) CropYBox.Text = _vm.CropY.ToString();
        if (force || !CropWBox.IsKeyboardFocusWithin) CropWBox.Text = _vm.CropW.ToString();
        if (force || !CropHBox.IsKeyboardFocusWithin) CropHBox.Text = _vm.CropH.ToString();
    }

    private void OnResetCrop(object sender, RoutedEventArgs e) => _vm.ResetCrop();

    // ---------- 导出设置 ----------

    private void OnResChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.ResolutionMode = ResCombo.SelectedIndex switch
        {
            1 => ResolutionMode.W480,
            2 => ResolutionMode.W640,
            3 => ResolutionMode.W720,
            4 => ResolutionMode.Custom,
            _ => ResolutionMode.Original
        };
        SyncResCombo();
    }

    private void SyncResCombo()
    {
        ResCombo.SelectedIndex = _vm.ResolutionMode switch
        {
            ResolutionMode.W480 => 1,
            ResolutionMode.W640 => 2,
            ResolutionMode.W720 => 3,
            ResolutionMode.Custom => 4,
            _ => 0
        };
    }

    private void OnCustomWidthLostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseInt(CustomWidthBox.Text, out int v))
            _vm.CustomWidth = Math.Clamp(v, 96, 1920);
        CustomWidthBox.Text = _vm.CustomWidth.ToString();
    }

    private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FpsCombo.SelectedIndex >= 0 && FpsCombo.SelectedIndex < FpsValues.Length)
            _vm.Fps = FpsValues[FpsCombo.SelectedIndex];
    }

    private void OnColorsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorCombo.SelectedIndex >= 0 && ColorCombo.SelectedIndex < ColorValues.Length)
            _vm.Colors = ColorValues[ColorCombo.SelectedIndex];
    }

    private void SyncCombos()
    {
        int fpsIdx = Array.IndexOf(FpsValues, _vm.Fps);
        FpsCombo.SelectedIndex = fpsIdx >= 0 ? fpsIdx : Array.IndexOf(FpsValues, 15);
        int colorIdx = Array.IndexOf(ColorValues, _vm.Colors);
        ColorCombo.SelectedIndex = colorIdx >= 0 ? colorIdx : 3;
        ResCombo.SelectedIndex = _vm.ResolutionMode switch
        {
            ResolutionMode.W480 => 1,
            ResolutionMode.W640 => 2,
            ResolutionMode.W720 => 3,
            ResolutionMode.Custom => 4,
            _ => 0
        };
        RatioCombo.SelectedIndex = Math.Max(0, Array.IndexOf(_vm.CropRatioChoices, _vm.CropRatioChoice));
        CustomWidthBox.Text = _vm.CustomWidth.ToString();
        SyncFrameBoxes();
        SyncCropBoxes();
    }

    // ---------- 文件操作 ----------

    private void OnOpenVideo(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择视频",
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.ts|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            _ = _vm.OpenAsync(dlg.FileName);
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_vm.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vm.OutputFolder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenResultFolder(object sender, RoutedEventArgs e)
    {
        var path = _vm.ExportResultPath;
        if (path == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnBrowseOutputFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择 GIF 输出目录" };
        if (dlg.ShowDialog(this) == true)
            _vm.OutputFolder = dlg.FolderName;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|可执行文件|*.exe"
        };
        if (dlg.ShowDialog(this) != true) return;
        string ffmpeg = dlg.FileName;
        string? dir = Path.GetDirectoryName(ffmpeg);
        string ffprobe = dir == null ? "" : Path.Combine(dir, "ffprobe.exe");
        if (!File.Exists(ffprobe))
        {
            var dlg2 = new OpenFileDialog
            {
                Title = "未在 ffmpeg 同目录找到 ffprobe.exe，请手动选择",
                Filter = "ffprobe.exe|ffprobe.exe|可执行文件|*.exe"
            };
            if (dlg2.ShowDialog(this) != true)
            {
                MessageBox.Show(this, "缺少 ffprobe.exe，无法继续。请安装完整 FFmpeg 后重试。", "GifMaker",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ffprobe = dlg2.FileName;
        }
        _vm.SetFfmpegPaths(ffmpeg, ffprobe);
    }

    private void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        if (_vm.IsDownloading) _vm.CancelDownload();
        else _ = _vm.DownloadFfmpegAsync();
    }

    // ---------- 导出 ----------

    private void OnExport(object sender, RoutedEventArgs e) => _ = _vm.ExportAsync();

    private void OnCancelExport(object sender, RoutedEventArgs e) => _vm.CancelExport();

    private void OnCancelProbe(object sender, RoutedEventArgs e) => _vm.CancelProbe();

    private static bool TryParseInt(string s, out int v) =>
        int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out v);

    private static int ParseOr(string s, int fallback) =>
        TryParseInt(s, out int v) ? v : fallback;
}