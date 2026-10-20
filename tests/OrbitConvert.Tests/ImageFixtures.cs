using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitConvert.Tests;

internal static class ImageFixtures
{
    // Owned synthetic fixtures: fully valid PNG chunks, zlib scanlines, and CRCs; no Q8 encoder.
    internal static byte[] Png(byte depth = 8, uint width = 2, uint height = 3,
        byte alpha = 255, string? extraChunk = null)
    {
        using var file = new MemoryStream();
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = depth;
        header[9] = 6;
        Chunk(file, "IHDR", header);
        if (extraChunk is not null) { Chunk(file, extraChunk, new byte[8]); }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            // Deliberately oversized headers are rejected before scanline decoding in boundary tests.
            for (var y = 0; y < Math.Min(height, 3); y++)
            {
                zlib.WriteByte(0);
                for (var x = 0; x < Math.Min(width, 8192); x++)
                {
                    foreach (var value in new byte[] { 255, 0, 0, alpha })
                    {
                        zlib.WriteByte(value);
                        if (depth == 16) { zlib.WriteByte(value); }
                    }
                }
            }
        }
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    internal static byte[] Jpeg(bool orientation = false) => OnSta(() => JpegCore(orientation));

    internal static byte[] PatternPng() => OnSta(() =>
    {
        var pixels = new byte[96 * 64 * 3];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 96; x++)
            {
                var offset = (y * 96 + x) * 3;
                pixels[offset] = (byte)((x * 11 + y * 3) % 256);
                pixels[offset + 1] = (byte)((x * 5 + y * 13) % 256);
                pixels[offset + 2] = (byte)((x * 17 + y * 7) % 256);
            }
        }
        var bitmap = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Rgb24, null, pixels, 96 * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new MemoryStream();
        encoder.Save(file);
        return file.ToArray();
    });

    internal static byte[] WithoutJpegScanData(byte[] jpeg)
    {
        // Keep the real encoder's headers, including SOS, but replace all compressed pixels with EOI.
        var offset = 2;
        while (offset <= jpeg.Length - 4 && jpeg[offset] == 0xFF)
        {
            var marker = jpeg[offset + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 2, 2));
            offset += 2 + length;
            if (length < 2 || offset > jpeg.Length) { break; }
            if (marker == 0xDA) { return jpeg[..offset].Concat(new byte[] { 0xFF, 0xD9 }).ToArray(); }
        }
        throw new InvalidOperationException("JPEG fixture has no complete scan header.");
    }

    private static byte[] JpegCore(bool orientation)
    {
        var bitmap = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, 18).ToArray(), 6);
        var metadata = new BitmapMetadata("jpg");
        if (orientation) { metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6); }
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static (int Width, int Height, byte[] Pixels, Guid Container, int BitsPerPixel) Decode(byte[] data) => OnSta(() => DecodeCore(data));

    private static (int Width, int Height, byte[] Pixels, Guid Container, int BitsPerPixel) DecodeCore(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[rgb.PixelWidth * rgb.PixelHeight * 4];
        rgb.CopyPixels(pixels, rgb.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, pixels, decoder.CodecInfo.ContainerFormat, frame.Format.BitsPerPixel);
    }

    private static void Chunk(Stream target, string type, byte[] data)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
        target.Write(size);
        var content = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        target.Write(content);
        uint crc = uint.MaxValue;
        foreach (var value in content)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc);
        target.Write(size);
    }

    private static T OnSta<T>(Func<T> action)
    {
        T value = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { value = action(); }
            catch (Exception exception) { error = exception; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) { throw new TimeoutException("WPF imaging fixture timed out."); }
        if (error is not null) { throw new InvalidOperationException("WPF imaging fixture failed.", error); }
        return value;
    }
}
