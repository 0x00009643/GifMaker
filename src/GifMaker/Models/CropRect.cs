namespace GifMaker.Models;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => new(0, 0, 0, 0);
}

/// <summary>归一化裁剪矩形（0..1 坐标）。</summary>
public struct CropRect : IEquatable<CropRect>
{
    public double Left;
    public double Top;
    public double Right;
    public double Bottom;

    public CropRect(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public static CropRect Full => new(0, 0, 1, 1);

    public double Width => Math.Max(Right - Left, 0.02);
    public double Height => Math.Max(Bottom - Top, 0.02);

    public CropRect Clamped()
    {
        const double minS = 0.02;
        double l = Math.Clamp(Left, 0, 1 - minS);
        double t = Math.Clamp(Top, 0, 1 - minS);
        double r = Math.Clamp(Right, l + minS, 1);
        double b = Math.Clamp(Bottom, t + minS, 1);
        return new CropRect(l, t, r, b);
    }

    public PixelRect ToPixels(int displayWidth, int displayHeight)
    {
        double x = Left * displayWidth;
        double y = Top * displayHeight;
        double w = Width * displayWidth;
        double h = Height * displayHeight;
        int X = Math.Max(0, (int)Math.Round(x));
        int Y = Math.Max(0, (int)Math.Round(y));
        int W = Math.Min(displayWidth - X, Math.Max(2, (int)Math.Round(w)));
        int H = Math.Min(displayHeight - Y, Math.Max(2, (int)Math.Round(h)));
        return new PixelRect(X, Y, W, H);
    }

    /// <summary>以中心为基准调整为指定像素宽高比（ratio = w/h，null 表示自由）。</summary>
    public CropRect WithRatio(double? pixelRatio, double viewAspect)
    {
        if (pixelRatio is not double ratio) return this;
        double normalized = ratio / viewAspect;
        double cx = (Left + Right) / 2;
        double cy = (Top + Bottom) / 2;
        double w = Width, h = Height;
        if (h * normalized >= w) w = h * normalized;
        else h = w / normalized;
        if (w > 1 || h > 1)
        {
            double s = 1.0 / Math.Max(w, h);
            w *= s;
            h *= s;
        }
        double l = cx - w / 2, t = cy - h / 2, r = cx + w / 2, b = cy + h / 2;
        if (l < 0) { r -= l; l = 0; }
        if (t < 0) { b -= t; t = 0; }
        if (r > 1) { l -= r - 1; r = 1; }
        if (b > 1) { t -= b - 1; b = 1; }
        return new CropRect(l, t, r, b).Clamped();
    }

    public bool Equals(CropRect other) =>
        Left.Equals(other.Left) && Top.Equals(other.Top) &&
        Right.Equals(other.Right) && Bottom.Equals(other.Bottom);

    public override bool Equals(object? obj) => obj is CropRect other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Left, Top, Right, Bottom);

    public static bool operator ==(CropRect a, CropRect b) => a.Equals(b);
    public static bool operator !=(CropRect a, CropRect b) => !a.Equals(b);
}