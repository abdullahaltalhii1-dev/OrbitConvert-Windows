using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.Tests;

public sealed class ImageHardeningTests : IDisposable
{
    private static readonly MagickImageEngine Engine = new();
    private const int MaximumInputBytes = 25 * 1024 * 1024;
    private const int MaximumOutputBytes = 96 * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", Guid.NewGuid().ToString("N"));

    public ImageHardeningTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var path in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)) { File.SetAttributes(path, FileAttributes.Normal); }
        Directory.Delete(@"\\?\" + _directory, recursive: true);
    }

    public static IEnumerable<object[]> WicVariants =>
        new (string Container, string Format)[]
        {
            ("png", "BlackWhite"), ("png", "Gray2"), ("png", "Gray4"), ("png", "Gray8"), ("png", "Indexed1"),
            ("png", "Indexed2"), ("png", "Indexed4"), ("png", "Indexed8"), ("png", "Bgr24"), ("png", "Bgra32"),
            ("bmp", "Indexed1"), ("bmp", "Indexed4"), ("bmp", "Indexed8"), ("bmp", "Bgr24"), ("bmp", "Bgr32"),
            ("bmp", "Bgra32"), ("jpeg", "Gray8"), ("jpeg", "Bgr24"),
        }.SelectMany(variant => ImageConversionOptions.Formats.Select(target => new object[] { variant.Container, variant.Format, target }));

    // Regression: palette and grayscale sources were written as RLE8 BMP, which the input guard rejects.
    [Theory]
    [MemberData(nameof(WicVariants))]
    public void ValidWindowsEncodedVariants_ConvertToEveryTargetAndValidate(string container, string pixelFormat, string target)
    {
        var format = (PixelFormat)typeof(PixelFormats).GetProperty(pixelFormat)!.GetValue(null)!;
        var source = ImageFixtures.Wic(container, format);
        using var input = new MemoryStream(source);
        var inspected = Engine.Inspect(input);
        Assert.True(inspected.IsSuccess, $"inspect {container}/{pixelFormat}: {inspected.Error}");
        using var again = new MemoryStream(source);
        using var output = new MemoryStream();
        var converted = Engine.Convert(again, output, target);
        Assert.True(converted.IsSuccess, $"convert {container}/{pixelFormat} -> {target}: {converted.Error}");
        output.Position = 0;
        Assert.True(Engine.ValidateOutput(output, converted.Inspection!).IsSuccess, $"validate {container}/{pixelFormat} -> {target}");
        Assert.Equal(37u, converted.Inspection!.Width);
        Assert.Equal(23u, converted.Inspection.Height);
        if (target == "BMP") { Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(output.ToArray().AsSpan(30))); } // BI_RGB
    }

    [Theory]
    [InlineData(4000u, 4000u, ImageInspectionError.None)] // Exactly 16,000,000 pixels.
    [InlineData(8000u, 2000u, ImageInspectionError.None)]
    [InlineData(4000u, 4001u, ImageInspectionError.TooLarge)]
    [InlineData(8192u, 1954u, ImageInspectionError.TooLarge)]
    public void PixelLimit_IsExactAtTheBoundary(uint width, uint height, ImageInspectionError expected)
    {
        // The fixture holds only three scanlines: an accepted header then fails pixel decoding instead,
        // so TooLarge proves the guard rejected the header before any pixel allocation.
        using var input = new MemoryStream(ImageFixtures.Png(width: width, height: height));
        var error = Engine.Inspect(input).Error;
        if (expected == ImageInspectionError.TooLarge) { Assert.Equal(expected, error); }
        else { Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, error); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByteLimit_AcceptsExactlyTwentyFiveMiBAndRejectsOneMore(bool seekable)
    {
        var exact = new byte[MaximumInputBytes];
        ImageFixtures.Png().CopyTo(exact, 0);
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, Engine.Inspect(Stream(exact, seekable)).Error);
        Assert.Equal(ImageInspectionError.TooLarge, Engine.Inspect(Stream(new byte[exact.Length + 1], seekable)).Error);
    }

    [Fact]
    public void SeekableSnapshot_AssemblesShortReads_AndRejectsAStreamThatGrows()
    {
        var png = ImageFixtures.PatternPng();
        Assert.True(Engine.Inspect(new TrickleStream(png, reportedLength: png.Length)).IsSuccess);
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, Engine.Inspect(new TrickleStream(png, reportedLength: png.Length - 10)).Error);
    }

    [Fact]
    public void SeekablePositionBeyondLength_ReturnsEmptyInputWithoutThrowing()
    {
        var png = ImageFixtures.PatternPng();
        using var input = new MemoryStream(png) { Position = png.Length + 10 };
        Assert.Equal(ImageInspectionError.EmptyInput, Engine.Inspect(input).Error);
    }

    [Fact]
    public void SeekableStreamEndingBeforeItsDeclaredLength_IsRejected()
    {
        // A complete, valid image whose stream claims more bytes than it delivers must not yield a shortened snapshot.
        var png = ImageFixtures.PatternPng();
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, Engine.Inspect(new TrickleStream(png, reportedLength: png.Length + 10)).Error);
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, Engine.Inspect(new TrickleStream([], reportedLength: 100)).Error);
    }

    [Fact]
    public void JpegSideLimit_IsCheckedInFrameHeader()
    {
        var jpeg = ImageFixtures.Jpeg();
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), 8193); // Width field.
        using var input = new MemoryStream(jpeg);
        Assert.Equal(ImageInspectionError.TooLarge, Engine.Inspect(input).Error);
    }

    [Fact]
    public async Task MaximumPixelImage_DecodesResizesEncodesAndValidatesForEveryTarget()
    {
        var source = Path.Combine(_directory, "max-16mp.png");
        await File.WriteAllBytesAsync(source, ImageFixtures.GradientPng(4000, 4000));
        var tiny = Path.Combine(_directory, "tiny.png");
        await File.WriteAllBytesAsync(tiny, ImageFixtures.PatternPng());
        var service = new ImageConversionService(Engine);
        foreach (var target in ImageConversionOptions.Formats)
        {
            var extension = ImageConversionOptions.Extension(target);
            var decoded = await service.ConvertAsync(source, Path.Combine(_directory, "from-max" + extension), target);
            Assert.True(decoded.IsSuccess, $"decode at limit -> {target}: {decoded.Error}");
            var upscaled = await service.ConvertAsync(tiny, Path.Combine(_directory, "upscaled" + extension), target,
                new ImageConversionOptions(ResizeEnabled: true, Width: 4000, Height: 4000, PreserveAspectRatio: false));
            Assert.True(upscaled.IsSuccess, $"resize to limit -> {target}: {upscaled.Error}");
            // The service already fully decoded and validated both outputs; they stay within the 96 MiB output bound.
            Assert.InRange(new FileInfo(upscaled.OutputPath!).Length, 1, MaximumOutputBytes);
        }
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Fact]
    public async Task UnicodeNestedAndLongPaths_AreHandledExactly()
    {
        var folder = Path.Combine(_directory, "صور العميل", "mixed English & عربي (1)", "emoji 📷 — test", new string('d', 120), new string('e', 60));
        Assert.True(folder.Length > 260);
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "صورة #1, نهائي.png");
        var destination = Path.Combine(folder, "نسخة؛ 'final' [v2].webp");
        await File.WriteAllBytesAsync(source, ImageFixtures.PatternPng());
        var original = await File.ReadAllBytesAsync(source);
        var result = await new ImageConversionService(Engine).ConvertAsync(source, destination, "WebP");
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(destination, result.OutputPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(new[] { "نسخة؛ 'final' [v2].webp", "صورة #1, نهائي.png" }.Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task DiskFullDuringWrite_LeavesNoOutputOrTemporaryFile()
    {
        var source = Path.Combine(_directory, "source.png");
        var destination = Path.Combine(_directory, "output.bmp");
        await File.WriteAllBytesAsync(source, ImageFixtures.PatternPng());
        var result = await new ImageConversionService(new DiskFullEngine(Engine)).ConvertAsync(source, destination, "BMP");
        Assert.Equal(ImageInspectionError.OutputFailure, result.Error);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    [Fact]
    public async Task ReadOnlySource_ConvertsWithoutChangingIt_AndDeniedDestinationFolderFailsCleanly()
    {
        var source = Path.Combine(_directory, "readonly.png");
        await File.WriteAllBytesAsync(source, ImageFixtures.PatternPng());
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var service = new ImageConversionService(Engine);
        Assert.True((await service.ConvertAsync(source, Path.Combine(_directory, "ok.jpg"), "JPEG")).IsSuccess);
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(source) & FileAttributes.ReadOnly);
        Assert.Equal(ImageFixtures.PatternPng(), await File.ReadAllBytesAsync(source));

        var denied = Directory.CreateDirectory(Path.Combine(_directory, "denied"));
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);
        var security = denied.GetAccessControl();
        security.AddAccessRule(rule);
        denied.SetAccessControl(security);
        try
        {
            var result = await service.ConvertAsync(source, Path.Combine(denied.FullName, "out.jpg"), "JPEG");
            Assert.Equal(ImageInspectionError.OutputFailure, result.Error);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            denied.SetAccessControl(security);
        }
        Assert.Empty(denied.GetFiles("*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SimultaneousConversionsToOneDestination_ExactlyOneWins()
    {
        var source = Path.Combine(_directory, "source.png");
        var destination = Path.Combine(_directory, "shared.png");
        await File.WriteAllBytesAsync(source, ImageFixtures.PatternPng());
        var service = new ImageConversionService(Engine);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.ConvertAsync(source, destination, "PNG")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(3, results.Count(result => result.Error == ImageInspectionError.DestinationExists));
        using var output = File.OpenRead(destination);
        Assert.True(Engine.Inspect(output).IsSuccess);
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
    }

    private static Stream Stream(byte[] bytes, bool seekable) => seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes);

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    // Seekable stream that returns at most 7 bytes per read and can under-report its length (a growing file).
    private sealed class TrickleStream(byte[] bytes, long reportedLength) : MemoryStream(bytes)
    {
        public override long Length => reportedLength;
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));
    }

    private sealed class DiskFullEngine(IImageEngine inner) : IImageEngine
    {
        public ImageInspectionResult Inspect(Stream input) => inner.Inspect(input);
        public ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected) => inner.ValidateOutput(output, expected);
        public ImageInspectionResult Convert(Stream input, Stream output, string format, ImageConversionOptions? options = null)
        {
            output.Write(new byte[4096]);
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }
    }
}
