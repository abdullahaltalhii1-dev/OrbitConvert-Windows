using System.IO;
using System.Security;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public sealed record ImageConversionResult(ImageInspectionError Error, string? OutputPath = null, bool CleanupFailed = false)
{
    public bool IsSuccess => Error == ImageInspectionError.None && OutputPath is not null;
}

public interface IImageConversionService
{
    Task<ImageInspectionResult> InspectAsync(string sourcePath);
    Task<ImageConversionResult> ConvertAsync(string sourcePath, string destinationPath, string outputFormat, ImageConversionOptions? options = null);
}

public sealed class ImageConversionService(IImageEngine engine) : IImageConversionService
{
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    public async Task<ImageInspectionResult> InspectAsync(string sourcePath)
    {
        await OperationGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var source = OpenSource(sourcePath);
                    return engine.Inspect(source);
                }
                catch (Exception error) when (IsFileError(error))
                {
                    return ImageInspectionResult.Failure(ImageInspectionError.UnreadableInput);
                }
            });
        }
        finally { OperationGate.Release(); }
    }

    public async Task<ImageConversionResult> ConvertAsync(string sourcePath, string destinationPath, string outputFormat, ImageConversionOptions? options = null)
    {
        await OperationGate.WaitAsync();
        try { return await Task.Run(() => ConvertFile(sourcePath, destinationPath, outputFormat, options ?? new())); }
        finally { OperationGate.Release(); }
    }

    private ImageConversionResult ConvertFile(string sourcePath, string destinationPath, string outputFormat, ImageConversionOptions options)
    {
        string? ownedTemporaryPath = null;
        var result = new ImageConversionResult(ImageInspectionError.OutputFailure);
        try
        {
            if (!options.IsValid) { return new(ImageInspectionError.InvalidSettings); }
            var source = Path.GetFullPath(sourcePath);
            var destination = Path.GetFullPath(destinationPath);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) { return new(ImageInspectionError.SamePath); }
            if (File.Exists(destination) || Directory.Exists(destination)) { return new(ImageInspectionError.DestinationExists); }
            var extension = Path.GetExtension(destination);
            if (!ImageConversionOptions.MatchesExtension(outputFormat, extension)) { return new(ImageInspectionError.InvalidDestination); }

            // Windows read sharing prohibits replacement/modification while the source is open.
            using var input = OpenSource(source);
            var inspection = engine.Inspect(input);
            if (!inspection.IsSuccess) { return new(inspection.Error); }
            input.Position = 0;
            var candidate = Path.Combine(Path.GetDirectoryName(destination)!, $".orbitconvert-{Guid.NewGuid():N}.tmp");
            ImageInspectionResult conversion;
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownedTemporaryPath = candidate;
                conversion = engine.Convert(input, output, outputFormat, options);
                output.Flush(flushToDisk: true);
            }
            if (!conversion.IsSuccess) { result = new(conversion.Error); }
            else
            {
                using (var verification = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var verified = engine.ValidateOutput(verification, conversion.Inspection!);
                    result = verified.IsSuccess ? new(ImageInspectionError.None, destination) : new(ImageInspectionError.OutputValidationFailed);
                }
                if (result.IsSuccess)
                {
                    File.Move(candidate, destination, overwrite: false);
                    ownedTemporaryPath = null;
                }
            }
        }
        catch (IOException) when (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            result = new(ImageInspectionError.DestinationExists);
        }
        catch (Exception error) when (IsFileError(error))
        {
            result = new(ImageInspectionError.OutputFailure);
        }
        finally
        {
            if (ownedTemporaryPath is not null)
            {
                try { File.Delete(ownedTemporaryPath); }
                catch (Exception error) when (IsFileError(error)) { result = result with { CleanupFailed = true }; }
            }
        }
        return result;
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
