using System.Buffers.Binary;
using ImageMagick;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.Tests;

public sealed class ExtendedImageConversionTests : IDisposable
{
    private readonly MagickImageEngine _engine = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", Guid.NewGuid().ToString("N"));
    public ExtendedImageConversionTests() => Directory.CreateDirectory(_directory);

    public static IEnumerable<object[]> FormatPairs => ImageConversionOptions.Formats.SelectMany(source =>
        ImageConversionOptions.Formats.Select(target => new object[] { source, target }));

    [Theory]
    [MemberData(nameof(FormatPairs))]
    public async Task AllSupportedInputsAndTargets_ProduceVerifiedNewFiles(string sourceFormat, string target)
    {
        var sourceBytes = Encode(ImageFixtures.PatternPng(), sourceFormat);
        var source = Path.Combine(_directory, "صورة mixed 你好" + ImageConversionOptions.Extension(sourceFormat));
        var destination = Path.Combine(_directory, "نسخة جديدة" + ImageConversionOptions.Extension(target));
        await File.WriteAllBytesAsync(source, sourceBytes);
        var result = await new ImageConversionService(_engine).ConvertAsync(source, destination, target);
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        var output = await File.ReadAllBytesAsync(destination);
        using var stream = new MemoryStream(output);
        var inspection = _engine.Inspect(stream);
        Assert.True(inspection.IsSuccess, inspection.Error.ToString());
        Assert.Equal(target, inspection.Inspection!.Format);
        Assert.Equal(96u, inspection.Inspection.Width);
        Assert.Equal(64u, inspection.Inspection.Height);
        if (target != "WebP")
        {
            var decoded = ImageFixtures.Decode(output); // Independent Windows WIC decoder.
            Assert.Equal(96, decoded.Width);
            Assert.Equal(64, decoded.Height);
        }
        else
        {
            Assert.Equal("RIFF"u8.ToArray(), output[..4]);
            Assert.Equal("WEBP"u8.ToArray(), output[8..12]);
            Assert.Equal((uint)output.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4)));
        }
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Theory]
    [InlineData("JPEG")]
    [InlineData("WebP")]
    public void Quality_ChangesActualCompressedPixelsAndEncodedBytes(string target)
    {
        var pattern = ImageFixtures.PatternPng();
        var low = Encode(pattern, target, new(Quality: 10));
        var high = Encode(pattern, target, new(Quality: 95));
        Assert.False(low.SequenceEqual(high));
        Assert.True(high.Length > low.Length);
        using var lowImage = new MagickImage(low);
        using var highImage = new MagickImage(high);
        using var original = new MagickImage(pattern);
        Assert.True(highImage.Compare(original, ErrorMetric.RootMeanSquared) < lowImage.Compare(original, ErrorMetric.RootMeanSquared));
    }

    [Theory]
    [InlineData(false, true, 48, 48, 96, 64)]
    [InlineData(true, true, 48, 48, 48, 32)]
    [InlineData(true, false, 48, 48, 48, 48)]
    [InlineData(true, true, 1, 1, 1, 1)]
    public void Resize_UsesOrientedDimensionsAndExplicitAspectChoice(bool enabled, bool keepRatio, int width, int height, int expectedWidth, int expectedHeight)
    {
        var output = Encode(ImageFixtures.PatternPng(), "PNG", new(ResizeEnabled: enabled, Width: width, Height: height, PreserveAspectRatio: keepRatio));
        var decoded = ImageFixtures.Decode(output);
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);
    }

    [Theory]
    [InlineData("PNG")]
    [InlineData("WebP")]
    [InlineData("JPEG")]
    [InlineData("BMP")]
    public void Transparency_IsPreservedOrCompositedOnWhite(string target)
    {
        var output = Encode(ImageFixtures.Png(alpha: 128), target);
        using var image = new MagickImage(output);
        using var pixels = image.GetPixels();
        var color = pixels.GetPixel(0, 0).ToColor()!;
        Assert.InRange((int)color.R, 250, 255);
        if (target is "PNG" or "WebP")
        {
            Assert.True(image.HasAlpha);
            Assert.InRange((int)color.A, 127, 129);
        }
        else
        {
            Assert.False(image.HasAlpha);
            Assert.InRange((int)color.G, 122, 132);
            Assert.InRange((int)color.B, 122, 132);
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task OpaqueAlphaChannelSource_ConvertsToEveryTarget(string target)
    {
        // RGBA screenshots are often fully opaque; encoders may drop the unused alpha channel.
        var source = Path.Combine(_directory, "opaque.png");
        var destination = Path.Combine(_directory, "opaque-out" + ImageConversionOptions.Extension(target));
        await File.WriteAllBytesAsync(source, ImageFixtures.Png(alpha: 255));
        var result = await new ImageConversionService(_engine).ConvertAsync(source, destination, target);
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.True(File.Exists(destination));
    }

    public static IEnumerable<object[]> Targets => ImageConversionOptions.Formats.Select(target => new object[] { target });

    [Fact]
    public void Orientation_IsAppliedBeforeResizeAndRemovedFromOutput()
    {
        var output = Encode(ImageFixtures.Jpeg(orientation: true), "WebP", new(ResizeEnabled: true, Width: 6, Height: 6));
        using var stream = new MemoryStream(output);
        var inspection = _engine.Inspect(stream);
        Assert.True(inspection.IsSuccess);
        Assert.Equal(6u, inspection.Inspection!.Width);
        Assert.Equal(4u, inspection.Inspection.Height);
        using var image = new MagickImage(output);
        Assert.Null(image.GetProfile("exif"));
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(101, 10, 10)]
    [InlineData(90, 0, 10)]
    [InlineData(90, -1, 10)]
    [InlineData(90, int.MaxValue, int.MaxValue)]
    [InlineData(90, 8193, 1)]
    [InlineData(90, 4001, 4000)]
    public async Task InvalidSettings_CreateNoOutput(int quality, int width, int height)
    {
        var source = Path.Combine(_directory, "source.png");
        var destination = Path.Combine(_directory, "output.webp");
        await File.WriteAllBytesAsync(source, ImageFixtures.Png());
        var options = new ImageConversionOptions(quality, true, width, height);
        var result = await new ImageConversionService(_engine).ConvertAsync(source, destination, "WebP", options);
        Assert.Equal(ImageInspectionError.InvalidSettings, result.Error);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExtendedContainers_RejectMalformedAnimatedColorAndOversizedHeaders()
    {
        var webp = Encode(ImageFixtures.PatternPng(), "WebP");
        AssertRejected(webp[..^2], ImageInspectionError.InvalidOrCorruptImage);
        AssertRejected(AddWebPChunk(webp, "ANIM", new byte[6]), ImageInspectionError.MultipleFrames);
        AssertRejected(AddWebPChunk(webp, "ICCP", new byte[10]), ImageInspectionError.UnsupportedColor);
        var extended = new byte[10];
        extended[0] = 2;
        AssertRejected(AddWebPChunk(webp, "VP8X", extended, prepend: true), ImageInspectionError.MultipleFrames);
        extended[0] = 0;
        extended[4] = 0; extended[5] = 32; // Width = 8193.
        AssertRejected(AddWebPChunk(webp, "VP8X", extended, prepend: true), ImageInspectionError.TooLarge);
        var bmp = Encode(ImageFixtures.PatternPng(), "BMP");
        var oversized = (byte[])bmp.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(18), 8193);
        AssertRejected(oversized, ImageInspectionError.TooLarge);
        var compression = (byte[])bmp.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(compression.AsSpan(30), 5); // Embedded PNG is not accepted as BMP.
        AssertRejected(compression, ImageInspectionError.UnsupportedFormat);
        AssertRejected(bmp[..^1], ImageInspectionError.InvalidOrCorruptImage);
        var invalidPixels = AddWebPChunk(webp[..12], "VP8 ", new byte[] { 0, 0, 0, 0x9D, 1, 0x2A, 2, 0, 3, 0 });
        using var damaged = new MemoryStream(invalidPixels);
        Assert.False(_engine.Inspect(damaged).IsSuccess);
    }

    [Fact]
    public void RealAnimatedWebP_AndHighPrecisionBmpMasksAreRejected()
    {
        using var seed = new MemoryStream(ImageFixtures.Png());
        Assert.True(_engine.Inspect(seed).IsSuccess);
        using var frames = new MagickImageCollection();
        frames.Add(new MagickImage(ImageFixtures.Png()));
        frames.Add(new MagickImage(ImageFixtures.Png(alpha: 128)));
        foreach (var frame in frames) { frame.AnimationDelay = 10; }
        using var animated = new MemoryStream();
        frames.Write(animated, MagickFormat.WebP);
        AssertRejected(animated.ToArray(), ImageInspectionError.MultipleFrames);

        var bmp = Encode(ImageFixtures.PatternPng(), "BMP");
        // Construct a V4 header with a 10-bit red mask; Q8 must not silently quantize it.
        var highDepth = new byte[bmp.Length + 68];
        bmp[..54].CopyTo(highDepth, 0);
        bmp[54..].CopyTo(highDepth, 122);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(2), (uint)highDepth.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(10), 122);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(14), 108);
        BinaryPrimitives.WriteUInt16LittleEndian(highDepth.AsSpan(28), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(30), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(54), 0x3FF00000);
        AssertRejected(highDepth, ImageInspectionError.UnsupportedBitDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(54), 0xFF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(highDepth.AsSpan(70), 0x4D424544); // Embedded profile color space.
        AssertRejected(highDepth, ImageInspectionError.UnsupportedColor);
    }

    [Theory]
    [InlineData("WebP")]
    [InlineData("BMP")]
    public async Task NewTargets_PreserveExistingDestinationsAndRejectWrongExtensions(string target)
    {
        var source = Path.Combine(_directory, "source.png");
        var output = Path.Combine(_directory, "existing" + ImageConversionOptions.Extension(target));
        await File.WriteAllBytesAsync(source, ImageFixtures.Png());
        await File.WriteAllBytesAsync(output, [1, 2, 3]);
        var service = new ImageConversionService(_engine);
        Assert.Equal(ImageInspectionError.DestinationExists, (await service.ConvertAsync(source, output, target)).Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(ImageInspectionError.InvalidDestination, (await service.ConvertAsync(source, output + ".png", target)).Error);
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    private byte[] Encode(byte[] source, string format, ImageConversionOptions? options = null)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        var result = _engine.Convert(input, output, format, options);
        Assert.True(result.IsSuccess, result.Error.ToString());
        output.Position = 0;
        Assert.True(_engine.ValidateOutput(output, result.Inspection!).IsSuccess, "Encoded output validation failed: " + format);
        return output.ToArray();
    }

    private void AssertRejected(byte[] bytes, ImageInspectionError expected)
    {
        using var input = new MemoryStream(bytes);
        Assert.Equal(expected, _engine.Inspect(input).Error);
    }

    private static byte[] AddWebPChunk(byte[] webp, string type, byte[] payload, bool prepend = false)
    {
        using var stream = new MemoryStream();
        stream.Write(webp[..12]);
        if (!prepend) { stream.Write(webp[12..]); }
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        stream.Write(length);
        stream.Write(payload);
        if (payload.Length % 2 != 0) { stream.WriteByte(0); }
        if (prepend) { stream.Write(webp[12..]); }
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length - 8);
        return result;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
