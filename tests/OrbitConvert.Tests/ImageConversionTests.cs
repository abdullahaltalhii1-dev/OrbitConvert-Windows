using System.Windows.Media.Imaging;
using ImageMagick;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.Tests;

public sealed class ImageConversionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", Guid.NewGuid().ToString("N"));
    private readonly MagickImageEngine _engine = new();
    public ImageConversionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Convert_BothDirections_IndependentlyDecodeAndPreserveOriginal(bool pngSource)
    {
        var bytes = pngSource ? ImageFixtures.Png() : ImageFixtures.Jpeg();
        var source = FilePath("صورة تجريبية 你好.bin");
        var destination = FilePath(pngSource ? "ناتج.jpg" : "ناتج.png");
        await File.WriteAllBytesAsync(source, bytes);

        var result = await new ImageConversionService(_engine).ConvertAsync(source, destination, pngSource ? "JPEG" : "PNG");

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        var decoded = ImageFixtures.Decode(await File.ReadAllBytesAsync(destination));
        Assert.Equal(2, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal(pngSource ? new JpegBitmapEncoder().CodecInfo.ContainerFormat : new PngBitmapEncoder().CodecInfo.ContainerFormat, decoded.Container);
        if (!pngSource) { Assert.Equal(8, File.ReadAllBytes(destination)[24]); }
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Theory]
    [InlineData((byte)0, 255, 255, 255)]
    [InlineData((byte)128, 255, 127, 127)]
    public void Convert_AlphaIsCompositedOnWhite(byte alpha, int red, int green, int blue)
    {
        using var input = new MemoryStream(ImageFixtures.Png(alpha: alpha));
        using var output = new MemoryStream();
        Assert.True(_engine.Convert(input, output, "JPEG").IsSuccess);
        var pixel = ImageFixtures.Decode(output.ToArray()).Pixels;
        Assert.InRange((int)pixel[0], blue - 5, blue + 5);
        Assert.InRange((int)pixel[1], green - 5, green + 5);
        Assert.InRange((int)pixel[2], red - 5, red + 5);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public void Inspect_Real16BitPng_IsRejectedBeforeQ8Decoding()
    {
        var fixture = ImageFixtures.Png(depth: 16);
        Assert.Equal(64, ImageFixtures.Decode(fixture).BitsPerPixel);
        using var stream = new MemoryStream(fixture);
        Assert.Equal(ImageInspectionError.UnsupportedBitDepth, _engine.Inspect(stream).Error);
    }

    [Theory]
    [InlineData(8193u, 1u)]
    [InlineData(4001u, 4000u)]
    public void Inspect_RejectsDimensionsBeforePixelAllocation(uint width, uint height)
    {
        using var stream = new MemoryStream(ImageFixtures.Png(width: width, height: height));
        Assert.Equal(ImageInspectionError.TooLarge, _engine.Inspect(stream).Error);
    }

    [Fact]
    public void Inspect_RejectsInputLargerThan25MiB()
    {
        using var stream = new MemoryStream(new byte[25 * 1024 * 1024 + 1]);
        Assert.Equal(ImageInspectionError.TooLarge, _engine.Inspect(stream).Error);
    }

    [Fact]
    public void Inspect_AcceptsMaximumSideAndRejectsConcatenatedJpegs()
    {
        using var wide = new MemoryStream(ImageFixtures.Png(width: 8192, height: 1));
        Assert.True(_engine.Inspect(wide).IsSuccess);
        var jpeg = ImageFixtures.Jpeg();
        using var concatenated = new MemoryStream(jpeg.Concat(jpeg).ToArray());
        Assert.Equal(ImageInspectionError.MultipleFrames, _engine.Inspect(concatenated).Error);
    }

    [Theory]
    [InlineData("acTL", ImageInspectionError.MultipleFrames)]
    [InlineData("iCCP", ImageInspectionError.UnsupportedColor)]
    public void Inspect_RejectsAnimationAndUnsupportedProfile(string chunk, ImageInspectionError error)
    {
        using var stream = new MemoryStream(ImageFixtures.Png(extraChunk: chunk));
        Assert.Equal(error, _engine.Inspect(stream).Error);
    }

    [Fact]
    public void Inspect_UnsupportedAndTruncatedInputsFail()
    {
        using var gif = new MemoryStream("GIF89a"u8.ToArray());
        using var corrupt = new MemoryStream(ImageFixtures.Png()[..40]);
        Assert.Equal(ImageInspectionError.UnsupportedFormat, _engine.Inspect(gif).Error);
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, _engine.Inspect(corrupt).Error);
    }

    [Fact]
    public void JpegWithoutScanData_ActuallyRaisesNativeCorruptionWarning()
    {
        var valid = ImageFixtures.Jpeg();
        using var source = new MemoryStream(valid);
        Assert.True(_engine.Inspect(source).IsSuccess); // Initialize the production policy first.
        using var image = new MagickImage();
        var warnings = new List<MagickWarningException>();
        image.Warning += (_, warning) => warnings.Add(warning.Exception);
        var settings = new MagickReadSettings { Format = MagickFormat.Jpeg };
        image.Read(valid, settings);
        Assert.Empty(warnings);

        var damaged = ImageFixtures.WithoutJpegScanData(valid);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, damaged[^2..]);
        image.Read(damaged, settings); // Decoder recovers without throwing, despite missing all pixels.
        Assert.Contains(warnings, warning => warning is MagickCorruptImageWarningException);
    }

    [Theory]
    [InlineData("inspect", ImageInspectionError.InvalidOrCorruptImage)]
    [InlineData("convert", ImageInspectionError.InvalidOrCorruptImage)]
    [InlineData("validate", ImageInspectionError.OutputValidationFailed)]
    public void DecoderWarning_RejectsDamagedJpeg(string operation, ImageInspectionError expected)
    {
        using var input = new MemoryStream(ImageFixtures.WithoutJpegScanData(ImageFixtures.Jpeg()));
        using var output = new MemoryStream();
        var result = operation switch
        {
            "inspect" => _engine.Inspect(input),
            "convert" => _engine.Convert(input, output, "PNG"),
            _ => _engine.ValidateOutput(input, new(2, 3, 8, "JPEG", false)),
        };
        Assert.Equal(expected, result.Error);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, output.Length);

        using var valid = new MemoryStream(ImageFixtures.Jpeg());
        Assert.True(_engine.Inspect(valid).IsSuccess); // Warning state must not leak to later operations.
    }

    [Fact]
    public async Task DecoderWarning_DamagedSourceNeverCreatesDestinationOrChangesOriginal()
    {
        var bytes = ImageFixtures.WithoutJpegScanData(ImageFixtures.Jpeg());
        var source = FilePath("صورة ناقصة.jpg");
        var destination = FilePath("output.png");
        await File.WriteAllBytesAsync(source, bytes);

        var result = await new ImageConversionService(_engine).ConvertAsync(source, destination, "PNG");

        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, result.Error);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Fact]
    public void Convert_NormalizesExifOrientation()
    {
        using var source = new MemoryStream(ImageFixtures.Jpeg(orientation: true));
        using var output = new MemoryStream();
        Assert.True(_engine.Convert(source, output, "PNG").IsSuccess);
        var decoded = ImageFixtures.Decode(output.ToArray());
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }

    [Fact]
    public async Task Convert_SamePathAndExistingDestinationNeverChangeBytes()
    {
        var source = FilePath("original.png");
        var destination = FilePath("existing.jpg");
        var original = ImageFixtures.Png();
        byte[] sentinel = [9, 8, 7];
        await File.WriteAllBytesAsync(source, original);
        await File.WriteAllBytesAsync(destination, sentinel);
        var service = new ImageConversionService(_engine);
        Assert.Equal(ImageInspectionError.SamePath, (await service.ConvertAsync(source, source.ToUpperInvariant(), "JPEG")).Error);
        Assert.Equal(ImageInspectionError.DestinationExists, (await service.ConvertAsync(source, destination, "JPEG")).Error);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(destination));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData("race", ImageInspectionError.DestinationExists)]
    [InlineData("corrupt-output", ImageInspectionError.OutputValidationFailed)]
    [InlineData("partial-failure", ImageInspectionError.OutputFailure)]
    [InlineData("jpeg-warning-output", ImageInspectionError.OutputValidationFailed)]
    public async Task Convert_FailuresAndFinalizationRaceLeaveNoOwnedTemporaryFiles(string mode, ImageInspectionError expected)
    {
        var source = FilePath("source.png");
        var destination = FilePath("output.jpg");
        await File.WriteAllBytesAsync(source, ImageFixtures.Png());
        var service = new ImageConversionService(new FaultEngine(_engine, mode, destination));
        var result = await service.ConvertAsync(source, destination, "JPEG");
        Assert.Equal(expected, result.Error);
        Assert.False(result.IsSuccess);
        Assert.Equal(ImageFixtures.Png(), await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
        if (mode == "race") { Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(destination)); }
        else { Assert.False(File.Exists(destination)); }
    }

    [Fact]
    public async Task Convert_InaccessibleSourceAndDirectoryFailWithoutOutput()
    {
        var source = FilePath("source.png");
        await File.WriteAllBytesAsync(source, ImageFixtures.Png());
        var service = new ImageConversionService(_engine);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False((await service.ConvertAsync(source, FilePath("locked.jpg"), "JPEG")).IsSuccess);
        }
        Assert.False((await service.ConvertAsync(source, FilePath("missing/output.jpg"), "JPEG")).IsSuccess);
        Assert.False((await service.ConvertAsync(FilePath("missing.png"), FilePath("output.jpg"), "JPEG")).IsSuccess);
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Fact]
    public void NativePolicy_BlocksOtherCodersAndEnforcesConfiguredLimits()
    {
        using var input = new MemoryStream(ImageFixtures.Png());
        Assert.True(_engine.Inspect(input).IsSuccess);
        Assert.Equal(0ul, ResourceLimits.Disk);
        Assert.Equal(256ul * 1024 * 1024, ResourceLimits.Memory);
        Assert.Equal(8192ul, ResourceLimits.Width);
        Assert.Equal(8192ul, ResourceLimits.Height);
        Assert.Equal(8ul, ResourceLimits.ListLength);
        Assert.InRange(ResourceLimits.Thread, 1ul, 2ul);
        Assert.Equal(1024ul * 1024, ResourceLimits.MaxProfileSize);
        using var image = new MagickImage(ImageFixtures.Png());
        Assert.Throws<MagickPolicyErrorException>(() => image.ToByteArray(MagickFormat.Gif));
    }

    [Fact]
    public void ValidateOutput_RejectsWrongFormatOrDimensions()
    {
        using var input = new MemoryStream(ImageFixtures.Png());
        var wrongFormat = _engine.ValidateOutput(input, new(2, 3, 8, "JPEG", true));
        Assert.Equal(ImageInspectionError.OutputValidationFailed, wrongFormat.Error);
        input.Position = 0;
        Assert.Equal(ImageInspectionError.OutputValidationFailed, _engine.ValidateOutput(input, new(4, 3, 8, "PNG", true)).Error);
    }

    [Fact]
    public async Task CleanupFailure_IsReportedAndDoesNotDeleteUnrelatedTemporaryFile()
    {
        var source = FilePath("source.png");
        var destination = FilePath("output.jpg");
        await File.WriteAllBytesAsync(source, ImageFixtures.Png());
        var unrelated = FilePath(".orbitconvert-unrelated.tmp");
        await File.WriteAllBytesAsync(unrelated, [77]);
        using var lockedEngine = new CleanupBlockingEngine(_engine);
        var result = await new ImageConversionService(lockedEngine).ConvertAsync(source, destination, "JPEG");
        Assert.False(result.IsSuccess);
        Assert.True(result.CleanupFailed);
        Assert.False(File.Exists(destination));
        Assert.Equal(new byte[] { 77 }, File.ReadAllBytes(unrelated));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private string FilePath(string name) => Path.Combine(_directory, name);

    private sealed class FaultEngine(IImageEngine inner, string mode, string destination) : IImageEngine
    {
        public ImageInspectionResult Inspect(Stream input) => inner.Inspect(input);
        public ImageInspectionResult Convert(Stream input, Stream output, string format)
        {
            if (mode == "jpeg-warning-output")
            {
                using var encoded = new MemoryStream();
                var converted = inner.Convert(input, encoded, format);
                Assert.True(converted.IsSuccess);
                output.Write(ImageFixtures.WithoutJpegScanData(encoded.ToArray()));
                return converted; // The real validator must reject the warning-producing temporary file.
            }
            if (mode == "partial-failure")
            {
                output.WriteByte(1);
                return ImageInspectionResult.Failure(ImageInspectionError.OutputFailure);
            }
            var result = inner.Convert(input, output, format);
            if (mode == "corrupt-output") { output.SetLength(1); }
            return result;
        }
        public ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected)
        {
            var result = inner.ValidateOutput(output, expected);
            if (mode == "race") { File.WriteAllBytes(destination, [42]); }
            return result;
        }
    }

    private sealed class CleanupBlockingEngine(IImageEngine inner) : IImageEngine, IDisposable
    {
        private FileStream? _lock;
        public ImageInspectionResult Inspect(Stream input) => inner.Inspect(input);
        public ImageInspectionResult Convert(Stream input, Stream output, string format) => inner.Convert(input, output, format);
        public ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected)
        {
            _lock = new FileStream(((FileStream)output).Name, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ImageInspectionResult.Failure(ImageInspectionError.OutputValidationFailed);
        }
        public void Dispose() => _lock?.Dispose();
    }
}
