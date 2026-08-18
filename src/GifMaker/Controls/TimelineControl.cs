using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GifMaker.Controls;

/// <summary>
/// 时长裁剪时间轴：缩略图条 + 起点/终点手柄 + 播放头。
/// 拖动端点裁剪（按帧），点击/拖动条内区域跳帧（松开时通知）。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    public static readonly DependencyProperty FrameCountProperty = DependencyProperty.Register(
        nameof(FrameCount), typeof(int), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0, OnStateChanged));

    public static readonly DependencyProperty StartFrameProperty = DependencyProperty.Register(
        nameof(StartFrame), typeof(int), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0, OnStateChanged));

    public static readonly DependencyProperty EndFrameProperty = DependencyProperty.Register(
        nameof(EndFrame), typeof(int), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0, OnStateChanged));

    public static readonly DependencyProperty CurrentFrameProperty = DependencyProperty.Register(
        nameof(CurrentFrame), typeof(int), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0, OnStateChanged));

    public int FrameCount { get => (int)GetValue(FrameCountProperty); set => SetValue(FrameCountProperty, value); }
    public int StartFrame { get => (int)GetValue(StartFrameProperty); set => SetValue(StartFrameProperty, value); }
    public int EndFrame { get => (int)GetValue(EndFrameProperty); set => SetValue(EndFrameProperty, value); }
    public int CurrentFrame { get => (int)GetValue(CurrentFrameProperty); set => SetValue(CurrentFrameProperty, value); }

    public IReadOnlyList<BitmapSource> Thumbs { get; private set; } = Array.Empty<BitmapSource>();
    public int ThumbStride { get; private set; } = 1;

    public event Action<int>? SeekRequested;
    public event Action<int>? TrimStartChanged;
    public event Action<int>? TrimEndChanged;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimelineControl)d).InvalidateVisual();

    private const double HandleW = 12;
    private const double SlopPx = 12;
    private const double StripH = 56;

    public void SetThumbs(IReadOnlyList<BitmapSource> thumbs, int stride)
    {
        Thumbs = thumbs;
        ThumbStride = stride;
        InvalidateVisual();
    }

    private enum DragMode { None, Start, End, Seek }
    private DragMode _drag = DragMode.None;
    private int _dragSeekFrame;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = Math.Min(ActualHeight, ActualHeight > 0 ? ActualHeight : StripH);
        if (w < 4 || h < 4) return;
        int n = Math.Max(FrameCount, 2);
        double stripH = Math.Min(h, StripH);

        // 缩略图条
        if (Thumbs.Count > 0)
        {
            for (int i = 0; i < Thumbs.Count; i++)
            {
                int frame = i * ThumbStride;
                double x0 = X(frame, n, w);
                double x1 = X(Math.Min(frame + ThumbStride, n - 1), n, w);
                double cw = Math.Max(x1 - x0, 2.0);
                if (x0 > w) break;
                dc.DrawImage(Thumbs[i], new Rect(x0, 0, cw, stripH));
            }
        }
        else
        {
            dc.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, w, stripH));
        }

        double sx = X(StartFrame, n, w), ex = X(EndFrame, n, w);

        // 选中区间高亮
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(70, 79, 70, 229)),
            null, new Rect(sx, 0, Math.Max(ex - sx, 0), stripH));

        // 裁剪区间外变暗
        var dim = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        dc.DrawRectangle(dim, null, new Rect(0, 0, Math.Max(sx, 0), stripH));
        dc.DrawRectangle(dim, null, new Rect(ex, 0, Math.Max(w - ex, 0), stripH));

        // 起点 / 终点手柄
        var startBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        var endBrush = new SolidColorBrush(Color.FromRgb(0x0E, 0x9F, 0x6E));
        dc.DrawRectangle(startBrush, null, new Rect(sx - HandleW / 2, 0, HandleW, stripH));
        dc.DrawRectangle(endBrush, null, new Rect(ex - HandleW / 2, 0, HandleW, stripH));

        // 播放头
        double cx = X(CurrentFrame, n, w);
        dc.DrawRectangle(Brushes.White, null, new Rect(cx - 1, 0, 2.5, stripH));

        // 底部刻度
        double tickH = h - stripH;
        if (tickH > 6)
        {
            var tickPen = new Pen(Brushes.Gray, 1);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, stripH, w, tickH));
            int ticks = Math.Min(10, n);
            for (int i = 0; i <= ticks; i++)
            {
                double x = X((int)Math.Round((n - 1) * i / (double)ticks), n, w);
                dc.DrawLine(tickPen, new Point(x, stripH), new Point(x, stripH + Math.Min(tickH, 8)));
            }
        }
    }

    private static double X(int frame, int n, double w) => frame * (w - 2) / (n - 1) + 1;

    private int FrameAtX(double x, int n, double w) =>
        Math.Clamp((int)Math.Round((x - 1) / Math.Max(w - 2, 1) * (n - 1)), 0, n - 1);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || FrameCount < 1) return;
        int n = Math.Max(FrameCount, 2);
        double w = Math.Max(ActualWidth, 1);
        double x = e.GetPosition(this).X;
        double sx = X(StartFrame, n, w), ex = X(EndFrame, n, w);

        if (Math.Abs(x - sx) <= SlopPx) _drag = DragMode.Start;
        else if (Math.Abs(x - ex) <= SlopPx) _drag = DragMode.End;
        else _drag = DragMode.Seek;

        if (_drag != DragMode.None)
        {
            CaptureMouse();
            e.Handled = true;
            if (_drag == DragMode.Seek)
            {
                _dragSeekFrame = FrameAtX(x, n, w);
                CurrentFrame = _dragSeekFrame;
            }
            else
            {
                ApplyTrim(x, n, w);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        int n = Math.Max(FrameCount, 2);
        double w = Math.Max(ActualWidth, 1);
        double x = e.GetPosition(this).X;
        ApplyTrim(x, n, w);
        if (_drag == DragMode.Seek)
        {
            int f = FrameAtX(x, n, w);
            if (f != _dragSeekFrame)
            {
                _dragSeekFrame = f;
                CurrentFrame = f;
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        DragMode mode = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        e.Handled = true;
        if (mode == DragMode.Seek && FrameCount > 0)
            SeekRequested?.Invoke(Math.Clamp(_dragSeekFrame, 0, FrameCount - 1));
    }

    private void ApplyTrim(double x, int n, double w)
    {
        int f = FrameAtX(x, n, w);
        if (_drag == DragMode.Start)
        {
            f = Math.Clamp(f, 0, EndFrame);
            if (f != StartFrame) StartFrame = f;
            TrimStartChanged?.Invoke(f);
        }
        else if (_drag == DragMode.End)
        {
            f = Math.Clamp(f, StartFrame, FrameCount - 1);
            if (f != EndFrame) EndFrame = f;
            TrimEndChanged?.Invoke(f);
        }
    }
}