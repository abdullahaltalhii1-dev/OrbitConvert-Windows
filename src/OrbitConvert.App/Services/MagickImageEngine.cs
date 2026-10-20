using System.IO;
using ImageMagick;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public sealed class MagickImageEngine : IImageEngine
{
    public ImageInspectionResult Inspect(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.CanRead)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.UnreadableInput);
        }

        if (input.CanSeek && input.Length - input.Position == 0)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.EmptyInput);
        }

        try
        {
            using var image = new MagickImage();
            image.Read(input);

            return ImageInspectionResult.Success(new ImageInspection(
                image.Width,
                image.Height,
                image.Depth,
                image.Format.ToString().ToUpperInvariant(),
                image.HasAlpha));
        }
        catch (MagickException)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.InvalidOrCorruptImage);
        }
        catch (IOException)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.UnreadableInput);
        }
        catch (NotSupportedException)
        {
            return ImageInspectionResult.Failure(ImageInspectionError.UnreadableInput);
        }
    }
}
