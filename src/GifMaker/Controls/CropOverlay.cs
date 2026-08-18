using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GifMaker.Models;

namespace GifMaker.Controls;

/// <summary>
/// 画面裁剪覆盖层：半透明遮罩 + 裁剪框（8 向拖拽手柄 + 拖动移动），
/// Crop 为归一化坐标，TwoWay 绑定；LockedRatio 锁定像素宽高比。
/// </summary>
public sealed class CropOverlay : FrameworkElement
{
    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop), typeof(CropRect), typeof(CropOverlay),
        new FrameworkPropertyMetadata(CropRect.Full, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCropChanged));

    public static readonly DependencyProperty LockedRatioProperty = DependencyProperty.Register(
        nameof(LockedRatio), typeof(double?), typeof(CropOverlay),
        new FrameworkPropertyMetadata(null, OnCropChanged));

    public CropRect Crop
    {
        get => (CropRect)GetValue(CropProperty);
        set { if ((CropRect)GetValue(CropProperty) != value) { SetValue(CropProperty, value); } else InvalidateVisual(); }
    }

    public double? LockedRatio
    {
        get => (double?)GetValue(LockedRatioProperty);
        set => SetValue(LockedRatioProperty, value);
    }

    private static void OnCropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CropOverlay)d).InvalidateVisual();

    private enum Handle { None, Move, Left, Right, Top, Bottom, TL, TR, BL, BR }

    private const double MinNorm = 0.05;
    private const double SlopPx = 14;

    private Handle _active = Handle.None;

    private double _dragOffX, _dragOffY;

    public CropOverlay()
    {
        IsHitTestVisible = true;
        Cursor = Cursors.Cross;
        Focusable = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        double px = Crop.Left * w, py = Crop.Top * h;
        double pw = Crop.Width * w, ph = Crop.Height * h;
        double cx = px + pw / 2, cy = py + ph / 2;

        // 全区域透明填充：保证挖孔区域（裁剪框内部）也参与命中测试
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // 遮罩（全屏挖孔）
        var geo = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, w, h)),
            new RectangleGeometry(new Rect(px, py, pw, ph)));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, geo);

        // 裁剪框边框 + 三分线
        var borderPen = new Pen(Brushes.White, 2);
        dc.DrawRectangle(null, borderPen, new Rect(px, py, pw, ph));
        var thirdPen = new Pen(Brushes.White, 0.6) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        thirdPen.Freeze();
        for (int i = 1; i <= 2; i++)
        {
            dc.DrawLine(thirdPen, new Point(px + pw * i / 3, py), new Point(px + pw * i / 3, py + ph));
            dc.DrawLine(thirdPen, new Point(px, py + ph * i / 3), new Point(px + pw, py + ph * i / 3));
        }

        // 4 角手柄 + 4 边中点手柄
        double r = 7;
        var pts = new (Handle handle, Point p)[]
        {
            (Handle.TL, new Point(px, py)), (Handle.TR, new Point(px + pw, py)),
            (Handle.BL, new Point(px, py + ph)), (Handle.BR, new Point(px + pw, py + ph)),
            (Handle.Left, new Point(px, cy)), (Handle.Right, new Point(px + pw, cy)),
            (Handle.Top, new Point(cx, py)), (Handle.Bottom, new Point(cx, py + ph))
        };
        var fill = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        foreach (var (_, p) in pts)
        {
            dc.DrawEllipse(Brushes.White, null, p, r, r);
            dc.DrawEllipse(fill, null, p, r * 0.62, r * 0.62);
        }

        dc.DrawEllipse(Brushes.Transparent, null, new Point(cx, cy), 0, 0);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        _active = HitTest(pos.X, pos.Y);
        if (_active == Handle.Move)
        {
            double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
            _dragOffX = pos.X / w - Crop.Left;
            _dragOffY = pos.Y / h - Crop.Top;
        }
        if (_active != Handle.None)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_active == Handle.None) return;
        var pos = e.GetPosition(this);
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        double nx = Math.Clamp(pos.X / w, 0, 1);
        double ny = Math.Clamp(pos.Y / h, 0, 1);

        double aspect = LockedRatio is double ratio && ratio > 0 ? ratio / (w / h) : double.NaN;

        var r = _active switch
        {
            Handle.Move => MoveRect(Crop, nx, ny),
            _ => ResizeWithAnchor(Crop, _active, nx, ny, aspect)
        };

        var clamped = r.Clamped();
        if (clamped != Crop)
        {
            Crop = clamped;
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_active == Handle.None) return;
        _active = Handle.None;
        ReleaseMouseCapture();
        e.Handled = true;
    }

/// <summary>
/// 以对侧边缘/角为锚点缩放，支持比例锁定（锚点不动）。
/// 活动边跟随鼠标（可双向拖动，平滑收缩到最小），越过锚点后贴锚点保持最小尺寸，不翻转。
/// </summary>
private static CropRect ResizeWithAnchor(CropRect r, Handle h, double nx, double ny, double aspect)
{
    (double ax, double ay, int sx, int sy) = h switch
    {
        Handle.TL => (r.Right, r.Bottom, -1, -1),
        Handle.TR => (r.Left, r.Bottom, 1, -1),
        Handle.BL => (r.Right, r.Top, -1, 1),
        Handle.BR => (r.Left, r.Top, 1, 1),
        Handle.Left => (r.Right, (r.Top + r.Bottom) / 2, -1, 0),
        Handle.Right => (r.Left, (r.Top + r.Bottom) / 2, 1, 0),
        Handle.Top => ((r.Left + r.Right) / 2, r.Bottom, 0, -1),
        Handle.Bottom => ((r.Left + r.Right) / 2, r.Top, 0, 1),
        _ => (r.Left, r.Top, 0, 0)
    };

    // 活动边位置（跟随鼠标，夹在锚点与画面边界之间）
    double l, t, r1, b1;
    if (sx > 0) { l = ax; r1 = Math.Clamp(nx, ax + MinNorm, 1); }
    else if (sx < 0) { r1 = ax; l = Math.Clamp(nx, 0, ax - MinNorm); }
    else { l = ax - r.Width / 2; r1 = ax + r.Width / 2; }

    if (sy > 0) { t = ay; b1 = Math.Clamp(ny, ay + MinNorm, 1); }
    else if (sy < 0) { b1 = ay; t = Math.Clamp(ny, 0, ay - MinNorm); }
    else { t = ay - r.Height / 2; b1 = ay + r.Height / 2; }

    if (!double.IsNaN(aspect))
    {
        double maxCw = sx > 0 ? 1 - ax : (sx < 0 ? ax : Math.Min(ax, 1 - ax) * 2);
        double maxCh = sy > 0 ? 1 - ay : (sy < 0 ? ay : Math.Min(ay, 1 - ay) * 2);
        if (sx != 0 && sy != 0)
        {
            double cwM = sx > 0 ? r1 - ax : ax - l;
            double chM = sy > 0 ? b1 - ay : ay - t;
            double cw2 = Math.Max(cwM, chM * aspect);
            double ch2 = cw2 / aspect;
            if (cw2 > maxCw || ch2 > maxCh)
            {
                double s = Math.Min(maxCw / cw2, maxCh / ch2);
                cw2 *= s;
                ch2 *= s;
            }
            cw2 = Math.Max(cw2, MinNorm);
            ch2 = Math.Max(ch2, MinNorm);
            if (sx > 0) r1 = ax + cw2; else l = ax - cw2;
            if (sy > 0) b1 = ay + ch2; else t = ay - ch2;
        }
        else if (sx != 0)
        {
            double cwM = sx > 0 ? r1 - ax : ax - l;
            double chM = Math.Max(r.Height, cwM / aspect);
            if (chM > maxCh)
            {
                chM = maxCh;
                cwM = chM * aspect;
            }
            if (sx > 0) r1 = ax + cwM; else l = ax - cwM;
            t = ay - chM / 2;
            b1 = ay + chM / 2;
        }
        else
        {
            double chM = sy > 0 ? b1 - ay : ay - t;
            double cwM = Math.Max(r.Width, chM * aspect);
            if (cwM > maxCw)
            {
                cwM = maxCw;
                chM = cwM / aspect;
            }
            if (sy > 0) b1 = ay + chM; else t = ay - chM;
            l = ax - cwM / 2;
            r1 = ax + cwM / 2;
        }
    }

    l = Math.Clamp(l, 0, 1);
    t = Math.Clamp(t, 0, 1);
    r1 = Math.Clamp(r1, 0, 1);
    b1 = Math.Clamp(b1, 0, 1);
    return new CropRect(l, t, r1, b1);
}

    private CropRect MoveRect(CropRect r, double nx, double ny)
    {
        double cw = r.Right - r.Left, ch = r.Bottom - r.Top;
        double l = Math.Clamp(nx - _dragOffX, 0, 1 - cw);
        double t = Math.Clamp(ny - _dragOffY, 0, 1 - ch);
        return new CropRect(l, t, l + cw, t + ch);
    }

    private Handle HitTest(double x, double y)
    {
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        double px = Crop.Left * w, py = Crop.Top * h;
        double pw = Crop.Width * w, ph = Crop.Height * h;
        double cx = px + pw / 2, cy = py + ph / 2;

        const double r = 12;
        bool near(double a, double b) => Math.Abs(a - b) <= r;

        if (near(x, px) && near(y, py)) return Handle.TL;
        if (near(x, px + pw) && near(y, py)) return Handle.TR;
        if (near(x, px) && near(y, py + ph)) return Handle.BL;
        if (near(x, px + pw) && near(y, py + ph)) return Handle.BR;
        if (near(x, px) && near(y, cy)) return Handle.Left;
        if (near(x, px + pw) && near(y, cy)) return Handle.Right;
        if (near(x, cx) && near(y, py)) return Handle.Top;
        if (near(x, cx) && near(y, py + ph)) return Handle.Bottom;
        if (x >= px - r && x <= px + pw + r && y >= py - r && y <= py + ph + r) return Handle.Move;
        return Handle.None;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_active == Handle.None)
            Cursor = Cursors.Cross;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        // 命中时切换光标由外部处理，保持简单
    }
}