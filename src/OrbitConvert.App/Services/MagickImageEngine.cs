using System.IO;
using ImageMagick;
using ImageMagick.Configuration;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public sealed class MagickImageEngine : IImageEngine
{
    private static readonly object NativeLock = new();
    private static bool _initialized;

    public ImageInspectionResult Inspect(Stream input) => Process(input, null, null, false);

    public ImageInspectionResult Convert(Stream input, Stream output, string outputFormat, ImageConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (options is { IsValid: false }) { return ImageInspectionResult.Failure(ImageInspectionError.InvalidSettings); }
        return ImageConversionOptions.Formats.Contains(outputFormat)
            ? Process(input, output, outputFormat, false, options ?? new())
            : ImageInspectionResult.Failure(ImageInspectionError.UnsupportedFormat);
    }

    public ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected)
    {
        var result = Process(output, null, null, true);
        return result.Inspection is { } actual && actual.Format == expected.Format &&
            actual.Width == expected.Width && actual.Height == expected.Height && actual.BitDepth <= 8 &&
            actual.HasAlpha == expected.HasAlpha
            ? result : ImageInspectionResult.Failure(ImageInspectionError.OutputValidationFailed);
    }

    private static ImageInspectionResult Process(Stream input, Stream? output, string? outputFormat, bool validation, ImageConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            // Bounded snapshot prevents a changing stream between header inspection and decoding.
            var data = ImageInputGuard.ReadBounded(input, validation ? ImageInputGuard.MaximumOutputBytes : ImageInputGuard.MaximumInputBytes);
            var format = ImageInputGuard.Check(data);
            lock (NativeLock)
            {
                if (!_initialized)
                {
                    var configuration = ConfigurationFiles.Default;
                    configuration.Policy.Data = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ImageMagick", "policy.xml"));
                    var configurationDirectory = MagickNET.Initialize(configuration);
                    ResourceLimits.MaxProfileSize = 1024 * 1024;
                    ResourceLimits.MaxMemoryRequest = 128ul * 1024 * 1024;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        try { Directory.Delete(configurationDirectory, recursive: true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    };
                    _initialized = true;
                }
                // Native decoders can recover damaged pixels and report only a warning, not an exception.
                var corruptWarning = 0;
                void TrackWarning(object? sender, WarningEventArgs warning)
                {
                    if (warning.Exception is MagickCorruptImageWarningException or MagickCoderWarningException)
                    {
                        Interlocked.Exchange(ref corruptWarning, 1);
                    }
                }
                var settings = new MagickReadSettings { Format = format };
                using var headers = new MagickImageCollection();
                headers.Warning += TrackWarning;
                headers.Ping(data, settings);
                if (Volatile.Read(ref corruptWarning) != 0) { return ImageInspectionResult.Failure(ImageInspectionError.InvalidOrCorruptImage); }
                if (headers.Count != 1) { return ImageInspectionResult.Failure(ImageInspectionError.MultipleFrames); }
                ImageInputGuard.CheckDimensions(headers[0].Width, headers[0].Height);
                using var image = new MagickImage();
                image.Warning += TrackWarning;
                image.Read(data, settings);
                if (Volatile.Read(ref corruptWarning) != 0) { return ImageInspectionResult.Failure(ImageInspectionError.InvalidOrCorruptImage); }
                if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray))
                {
                    return ImageInspectionResult.Failure(ImageInspectionError.UnsupportedColor);
                }
                if (image.GetProfile("icc") is not null || image.GetProfile("icm") is not null)
                {
                    return ImageInspectionResult.Failure(ImageInspectionError.UnsupportedColor);
                }
                // Normalize into pixels and remove EXIF to avoid applying orientation twice.
                image.AutoOrient();
                image.RemoveProfile("exif");
                if (output is not null)
                {
                    var size = options!.OutputSize(image.Width, image.Height);
                    ImageInputGuard.CheckDimensions(size.Width, size.Height);
                    if (options.ResizeEnabled)
                    {
                        image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true });
                    }
                    // Encoders drop an unused alpha channel; normalize first so output validation stays exact.
                    if (image.HasAlpha && image.IsOpaque) { image.Alpha(AlphaAction.Off); }
                    if (outputFormat is "JPEG" or "BMP")
                    {
                        image.BackgroundColor = MagickColors.White;
                        image.Alpha(AlphaAction.Remove);
                    }
                    if (outputFormat is "JPEG" or "WebP") { image.Quality = (uint)options.Quality; }
                    image.Depth = 8;
                    if (outputFormat == "PNG") { image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8"); }
                    if (outputFormat == "WebP")
                    {
                        image.Settings.SetDefine(MagickFormat.WebP, "lossless", "false");
                        image.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", "100");
                    }
                    image.Format = outputFormat switch
                    {
                        "PNG" => MagickFormat.Png,
                        "JPEG" => MagickFormat.Jpeg,
                        "WebP" => MagickFormat.WebP,
                        _ => MagickFormat.Bmp3,
                    };
                    image.Write(output);
                    if (Volatile.Read(ref corruptWarning) != 0) { return ImageInspectionResult.Failure(ImageInspectionError.OutputFailure); }
                }
                return ImageInspectionResult.Success(new ImageInspection(image.Width, image.Height, image.Depth,
                    outputFormat ?? (format switch { MagickFormat.Png => "PNG", MagickFormat.Jpeg => "JPEG", MagickFormat.WebP => "WebP", _ => "BMP" }), image.HasAlpha));
            }
        }
        catch (ImageInputException error)
        {
            return ImageInspectionResult.Failure(error.Error);
        }
        catch (MagickResourceLimitErrorException)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.ResourceLimit);
        }
        catch (MagickException)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.InvalidOrCorruptImage);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return ImageInspectionResult.Failure(output is null ? ImageInspectionError.UnreadableInput : ImageInspectionError.OutputFailure);
        }
    }
}
