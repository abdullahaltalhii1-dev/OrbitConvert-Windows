using System.Text;
using OrbitConvert.App.Services;

namespace OrbitConvert.Tests;

public sealed class MagickImageEngineTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Inspect_ValidPng_ReturnsDecodedImageInformation()
    {
        var engine = new MagickImageEngine();
        using var input = new MemoryStream(OnePixelPng, writable: false);

        var result = engine.Inspect(input);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Inspection);
        Assert.Equal((uint)1, result.Inspection.Width);
        Assert.Equal((uint)1, result.Inspection.Height);
        Assert.Equal("PNG", result.Inspection.Format);
        Assert.True(result.Inspection.BitDepth > 0);
    }

    [Fact]
    public void Inspect_EmptyInput_ReturnsSafeFailure()
    {
        var engine = new MagickImageEngine();
        using var input = new MemoryStream();

        var result = engine.Inspect(input);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Inspection);
        Assert.Equal(ImageInspectionError.EmptyInput, result.Error);
    }

    [Fact]
    public void Inspect_CorruptInput_ReturnsSafeFailure()
    {
        var engine = new MagickImageEngine();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("not an image"));

        var result = engine.Inspect(input);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Inspection);
        Assert.Equal(ImageInspectionError.InvalidOrCorruptImage, result.Error);
    }

    [Fact]
    public void Inspect_UnreadableInput_ReturnsSafeFailure()
    {
        var engine = new MagickImageEngine();
        using var input = new UnreadableStream();

        var result = engine.Inspect(input);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Inspection);
        Assert.Equal(ImageInspectionError.UnreadableInput, result.Error);
    }

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
