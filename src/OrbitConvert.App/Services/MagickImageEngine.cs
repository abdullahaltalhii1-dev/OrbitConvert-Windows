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

    public ImageInspectionResult Convert(Stream input, Stream output, string outputFormat)
    {
        ArgumentNullException.ThrowIfNull(output);
        return outputFormat is "PNG" or "JPEG"
            ? Process(input, output, outputFormat, false)
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

    private static ImageInspectionResult Process(Stream input, Stream? output, string? outputFormat, bool validation)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            // Bounded snapshot prevents a changing stream between header inspection and decoding.
            var data = ImageInputGuard.ReadBounded(input, validation ? ImageInputGuard.MaximumOutputBytes : ImageInputGuard.MaximumInputBytes);
            var format = ImageInputGuard.Check(data);
            if (outputFormat == format.ToString().ToUpperInvariant())
            {
                return ImageInspectionResult.Failure(ImageInspectionError.UnsupportedFormat);
            }
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
                var settings = new MagickReadSettings { Format = format };
                using var headers = new MagickImageCollection();
                headers.Ping(data, settings);
                if (headers.Count != 1) { return ImageInspectionResult.Failure(ImageInspectionError.MultipleFrames); }
                ImageInputGuard.CheckDimensions(headers[0].Width, headers[0].Height);
                using var image = new MagickImage(data, settings);
                if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Gray))
                {
                    return ImageInspectionResult.Failure(ImageInspectionError.UnsupportedColor);
                }
                // Normalize into pixels and remove EXIF to avoid applying orientation twice.
                image.AutoOrient();
                image.RemoveProfile("exif");
                if (output is not null)
                {
                    if (outputFormat == "JPEG")
                    {
                        image.BackgroundColor = MagickColors.White;
                        image.Alpha(AlphaAction.Remove);
                        image.Quality = 90;
                    }
                    image.Depth = 8;
                    if (outputFormat == "PNG") { image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8"); }
                    image.Format = outputFormat == "PNG" ? MagickFormat.Png : MagickFormat.Jpeg;
                    image.Write(output);
                }
                return ImageInspectionResult.Success(new ImageInspection(image.Width, image.Height, image.Depth,
                    image.Format == MagickFormat.Png ? "PNG" : "JPEG", image.HasAlpha));
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
