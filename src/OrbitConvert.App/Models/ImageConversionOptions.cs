namespace OrbitConvert.App.Models;

public sealed record ImageConversionOptions(int Quality = 90, bool ResizeEnabled = false,
    int Width = 0, int Height = 0, bool PreserveAspectRatio = true)
{
    public const int MaximumSide = 8192;
    public const long MaximumPixels = 16_000_000;
    public static IReadOnlyList<string> Formats { get; } = Array.AsReadOnly(new[] { "PNG", "JPEG", "WebP", "BMP" });

    public bool IsValid => Quality is >= 1 and <= 100 && (!ResizeEnabled ||
        (Width is > 0 and <= MaximumSide && Height is > 0 and <= MaximumSide && (long)Width * Height <= MaximumPixels));

    public (uint Width, uint Height) OutputSize(uint sourceWidth, uint sourceHeight)
    {
        if (!IsValid || sourceWidth == 0 || sourceHeight == 0) { throw new ArgumentOutOfRangeException(nameof(Width)); }
        if (!ResizeEnabled) { return (sourceWidth, sourceHeight); }
        if (!PreserveAspectRatio) { return ((uint)Width, (uint)Height); }
        var scale = Math.Min((double)Width / sourceWidth, (double)Height / sourceHeight);
        return ((uint)Math.Clamp(Math.Round(sourceWidth * scale), 1, Width),
            (uint)Math.Clamp(Math.Round(sourceHeight * scale), 1, Height));
    }

    public static string Extension(string format) => format switch
    {
        "PNG" => ".png",
        "JPEG" => ".jpg",
        "WebP" => ".webp",
        "BMP" => ".bmp",
        _ => string.Empty,
    };

    public static bool MatchesExtension(string format, string extension) =>
        Extension(format) is { Length: > 0 } expected && (extension.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            (format == "JPEG" && extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)));
}
