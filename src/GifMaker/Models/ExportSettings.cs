namespace GifMaker.Models;

public enum ResolutionMode
{
    Original,
    W480,
    W640,
    W720,
    Custom
}

public sealed record ExportSettings(
    int MaxWidth,
    int Fps,
    int Colors,
    bool Dithering,
    bool InfiniteLoop)
{
    public static ExportSettings Default => new(0, 15, 256, true, true);

    public static int WidthFor(ResolutionMode mode, int customWidth) => mode switch
    {
        ResolutionMode.W480 => 480,
        ResolutionMode.W640 => 640,
        ResolutionMode.W720 => 720,
        ResolutionMode.Custom => Math.Clamp(customWidth, 96, 1920),
        _ => 0
    };
}