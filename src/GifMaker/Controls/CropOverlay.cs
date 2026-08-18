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

    /// <summary>以对侧边缘/角为锚点缩放，支持比例锁定（锚点不动）。</summary>
    private static CropRect ResizeWithAnchor(CropRect r, Handle h, double nx, double ny, double aspect)
    {
        double ax, ay;
        switch (h)
        {
            case Handle.TL: ax = r.Right; ay = r.Bottom; break;
            case Handle.TR: ax = r.Left; ay = r.Bottom; break;
            case Handle.BL: ax = r.Right; ay = r.Top; break;
            case Handle.BR: ax = r.Left; ay = r.Top; break;
            case Handle.Left: ax = r.Right; ay = (r.Top + r.Bottom) / 2; break;
            case Handle.Right: ax = r.Left; ay = (r.Top + r.Bottom) / 2; break;
            case Handle.Top: ax = (r.Left + r.Right) / 2; ay = r.Bottom; break;
            case Handle.Bottom: ax = (r.Left + r.Right) / 2; ay = r.Top; break;
            default: return r;
        }

        double dx = nx - ax, dy = ny - ay;
        double cw = Math.Abs(dx), ch = Math.Abs(dy);

        if (!double.IsNaN(aspect))
        {
            if (ch * aspect >= cw) cw = ch * aspect;
            else ch = cw / aspect;
        }
        cw = Math.Max(cw, MinNorm);
        ch = Math.Max(ch, MinNorm);

        double l = dx >= 0 ? ax : ax - cw;
        double t = dy >= 0 ? ay : ay - ch;
        double r1 = l + cw, b1 = t + ch;

        // 越界收缩回可容纳范围
        if (l < 0) { r1 -= l; l = 0; }
        if (t < 0) { b1 -= t; t = 0; }
        if (r1 > 1) { l -= r1 - 1; r1 = 1; }
        if (b1 > 1) { t -= b1 - 1; b1 = 1; }

        return new CropRect(l, t, r1, b1);
    }

    private static CropRect MoveRect(CropRect r, double nx, double ny)
    {
        double cw = r.Right - r.Left, ch = r.Bottom - r.Top;
        double l = Math.Clamp(nx - cw / 2, 0, 1 - cw);
        double t = Math.Clamp(ny - ch / 2, 0, 1 - ch);
        return new CropRect(l, t, l + cw, t + ch);
    }

    private Handle HitTest(double x, double y)
    {
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        double px = Crop.Left * w, py = Crop.Top * h;
        double pw = Crop.Width * w, ph = Crop.Height * h;
        double cx = px + pw / 2, cy = py + ph / 2;

        bool near(double a, double b) => Math.Abs(a - b) <= SlopPx;

        if (near(x, px) && near(y, py)) return Handle.TL;
        if (near(x, px + pw) && near(y, py)) return Handle.TR;
        if (near(x, px) && near(y, py + ph)) return Handle.BL;
        if (near(x, px + pw) && near(y, py + ph)) return Handle.BR;
        if (near(x, px) && y >= py && y <= py + ph) return Handle.Left;
        if (near(x, px + pw) && y >= py && y <= py + ph) return Handle.Right;
        if (near(y, py) && x >= px && x <= px + pw) return Handle.Top;
        if (near(y, py + ph) && x >= px && x <= px + pw) return Handle.Bottom;
        if (x >= px && x <= px + pw && y >= py && y <= py + ph) return Handle.Move;
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