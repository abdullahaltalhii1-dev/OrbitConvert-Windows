using System.Buffers.Binary;
using System.IO;
using System.Text;
using ImageMagick;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

// Reads container headers before Q8 can quantize source pixels. Native decoding still validates data/CRCs.
internal static class ImageInputGuard
{
    internal const int MaximumInputBytes = 25 * 1024 * 1024;
    internal const int MaximumOutputBytes = 96 * 1024 * 1024;
    internal const uint MaximumSide = ImageConversionOptions.MaximumSide;
    internal const ulong MaximumPixels = ImageConversionOptions.MaximumPixels;

    internal static byte[] ReadBounded(Stream input, int maximumBytes)
    {
        if (!input.CanRead)
        {
            throw new IOException();
        }

        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            if (remaining > maximumBytes)
            {
                throw new ImageInputException(ImageInspectionError.TooLarge);
            }
        }

        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int)copy.Length))) > 0)
        {
            copy.Write(buffer, 0, count);
            if (copy.Length > maximumBytes)
            {
                throw new ImageInputException(ImageInspectionError.TooLarge);
            }
        }

        if (copy.Length == 0)
        {
            throw new ImageInputException(ImageInspectionError.EmptyInput);
        }

        return copy.ToArray();
    }

    internal static MagickFormat Check(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            CheckPng(data);
            return MagickFormat.Png;
        }

        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255)
        {
            CheckJpeg(data);
            return MagickFormat.Jpeg;
        }

        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            CheckWebP(data);
            return MagickFormat.WebP;
        }
        if (data.AsSpan().StartsWith("BM"u8))
        {
            CheckBmp(data);
            return MagickFormat.Bmp;
        }

        throw new ImageInputException(data.AsSpan().StartsWith("GIF8"u8) || data.AsSpan().StartsWith("RIFF"u8) ||
            data.AsSpan().StartsWith("BM"u8) || data.AsSpan().StartsWith("%PDF"u8)
            ? ImageInspectionError.UnsupportedFormat : ImageInspectionError.InvalidOrCorruptImage);
    }

    private static void CheckWebP(byte[] data)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != data.Length - 8) { Invalid(); }
        var offset = 12;
        var frames = 0;
        uint canvasWidth = 0, canvasHeight = 0, width = 0, height = 0;
        while (offset <= data.Length - 8)
        {
            var type = Encoding.ASCII.GetString(data, offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (length > data.Length - offset - 8) { Invalid(); }
            var chunk = data.AsSpan(offset + 8, (int)length);
            if (type is "ANIM" or "ANMF") { throw new ImageInputException(ImageInspectionError.MultipleFrames); }
            if (type == "ICCP") { throw new ImageInputException(ImageInspectionError.UnsupportedColor); }
            if (type == "VP8X")
            {
                if (offset != 12 || length != 10 || (chunk[0] & 0xC1) != 0 || chunk[1] != 0 || chunk[2] != 0 || chunk[3] != 0) { Invalid(); }
                if ((chunk[0] & 2) != 0) { throw new ImageInputException(ImageInspectionError.MultipleFrames); }
                if ((chunk[0] & 0x20) != 0) { throw new ImageInputException(ImageInspectionError.UnsupportedColor); }
                canvasWidth = Read24(chunk[4..]) + 1;
                canvasHeight = Read24(chunk[7..]) + 1;
                CheckDimensions(canvasWidth, canvasHeight);
            }
            if (type is "VP8 " or "VP8L")
            {
                if (++frames != 1) { throw new ImageInputException(ImageInspectionError.MultipleFrames); }
                if (type == "VP8 ")
                {
                    if (length < 10 || (chunk[0] & 1) != 0 || !chunk.Slice(3, 3).SequenceEqual(new byte[] { 0x9D, 1, 0x2A })) { Invalid(); }
                    width = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(chunk[6..]) & 0x3FFF);
                    height = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]) & 0x3FFF);
                }
                else
                {
                    if (length < 5 || chunk[0] != 0x2F) { Invalid(); }
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(chunk[1..]);
                    if ((bits >> 29) != 0) { Invalid(); }
                    width = (bits & 0x3FFF) + 1;
                    height = ((bits >> 14) & 0x3FFF) + 1;
                }
                CheckDimensions(width, height);
            }
            offset += 8 + (int)length;
            if ((length & 1) != 0)
            {
                if (offset >= data.Length || data[offset++] != 0) { Invalid(); }
            }
        }
        if (offset != data.Length || frames != 1 || (canvasWidth != 0 && (canvasWidth != width || canvasHeight != height))) { Invalid(); }
    }

    private static void CheckBmp(byte[] data)
    {
        if (data.Length < 26 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(2)) != data.Length) { Invalid(); }
        var header = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14));
        if (header is not (12 or 40 or 108 or 124)) { throw new ImageInputException(ImageInspectionError.UnsupportedFormat); }
        if (data.Length < 14 + header) { Invalid(); }
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
        uint width, height;
        ushort planes, depth;
        if (header == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20));
            planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(22));
            depth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(24));
        }
        else
        {
            var signedWidth = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
            var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
            if (signedWidth <= 0 || signedHeight == int.MinValue) { Invalid(); }
            width = (uint)signedWidth;
            height = (uint)Math.Abs(signedHeight);
            planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26));
            depth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30));
            if (compression is not (0 or 3 or 6)) { throw new ImageInputException(ImageInspectionError.UnsupportedFormat); }
            if (compression is 3 or 6)
            {
                var maskBytes = compression == 6 || header >= 108 ? 16 : 12;
                if (depth is not (16 or 32) || pixelOffset < 54 + maskBytes || data.Length < 54 + maskBytes) { Invalid(); }
                uint used = 0;
                for (var i = 0; i < maskBytes; i += 4)
                {
                    var mask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(54 + i));
                    if ((mask & used) != 0 || (depth == 16 && mask > ushort.MaxValue)) { Invalid(); }
                    if (System.Numerics.BitOperations.PopCount(mask) > 8) { throw new ImageInputException(ImageInspectionError.UnsupportedBitDepth); }
                    used |= mask;
                }
            }
            if (header >= 108)
            {
                var color = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(70));
                if (color is not (0x73524742 or 0x57696E20)) { throw new ImageInputException(ImageInspectionError.UnsupportedColor); }
            }
            if (header == 124 && (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(126)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(130)) != 0)) { throw new ImageInputException(ImageInspectionError.UnsupportedColor); }
        }
        CheckDimensions(width, height);
        if (planes != 1 || depth is not (1 or 4 or 8 or 16 or 24 or 32)) { Invalid(); }
        var pixelBytes = (((ulong)width * depth + 31) / 32) * 4 * height;
        if (pixelOffset < 14 + header || pixelOffset > data.Length || pixelBytes > (ulong)data.Length - pixelOffset) { Invalid(); }
    }

    private static uint Read24(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);
    private static void Invalid() => throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);

    internal static void CheckDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
        }

        if (width > MaximumSide || height > MaximumSide || (ulong)width * height > MaximumPixels)
        {
            throw new ImageInputException(ImageInspectionError.TooLarge);
        }
    }

    private static void CheckPng(byte[] data)
    {
        var offset = 8;
        var first = true;
        while (offset <= data.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (length > data.Length - offset - 12)
            {
                throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
            }

            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var chunk = data.AsSpan(offset + 8, (int)length);
            if (first)
            {
                if (type != "IHDR" || length != 13)
                {
                    throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
                }

                CheckDimensions(BinaryPrimitives.ReadUInt32BigEndian(chunk), BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]));
                if (chunk[8] > 8)
                {
                    throw new ImageInputException(ImageInspectionError.UnsupportedBitDepth);
                }
                first = false;
            }
            else if (type == "IHDR")
            {
                throw new ImageInputException(ImageInspectionError.MultipleFrames);
            }

            if (type == "acTL")
            {
                throw new ImageInputException(ImageInspectionError.MultipleFrames);
            }

            if (type is "iCCP" or "cICP" ||
                (type == "gAMA" && (chunk.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(chunk) != 45455)) ||
                (type == "cHRM" && !chunk.SequenceEqual(new byte[] { 0, 0, 122, 38, 0, 0, 128, 132, 0, 0, 250, 0, 0, 0, 128, 232, 0, 0, 117, 48, 0, 0, 234, 96, 0, 0, 58, 152, 0, 0, 23, 112 })))
            {
                throw new ImageInputException(ImageInspectionError.UnsupportedColor);
            }

            offset += (int)length + 12;
            if (type == "IEND")
            {
                if (length != 0 || offset != data.Length)
                {
                    throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
                }
                return;
            }
        }

        throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
    }

    private static void CheckJpeg(byte[] data)
    {
        var offset = 2;
        var hasFrame = false;
        var hasScan = false;
        while (offset < data.Length)
        {
            if (data[offset++] != 255)
            {
                throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
            }
            while (offset < data.Length && data[offset] == 255) { offset++; }
            if (offset >= data.Length) { break; }
            var marker = data[offset++];
            if (marker == 0xD9 && hasFrame && hasScan)
            {
                if (offset != data.Length) { throw new ImageInputException(ImageInspectionError.MultipleFrames); }
                return;
            }
            if (offset > data.Length - 2) { break; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            if (length < 2 || length > data.Length - offset) { break; }
            var segment = data.AsSpan(offset + 2, length - 2);
            if (marker == 0xE2 && segment.StartsWith("MPF\0"u8))
            {
                throw new ImageInputException(ImageInspectionError.MultipleFrames);
            }
            if (marker == 0xE2 && segment.StartsWith("ICC_PROFILE\0"u8))
            {
                throw new ImageInputException(ImageInspectionError.UnsupportedColor);
            }
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (segment.Length < 6) { break; }
                if (segment[0] != 8) { throw new ImageInputException(ImageInspectionError.UnsupportedBitDepth); }
                if (segment[5] is not (1 or 3)) { throw new ImageInputException(ImageInspectionError.UnsupportedColor); }
                CheckDimensions(BinaryPrimitives.ReadUInt16BigEndian(segment[3..]), BinaryPrimitives.ReadUInt16BigEndian(segment[1..]));
                hasFrame = true;
            }
            offset += length;
            if (marker == 0xDA && hasFrame)
            {
                hasScan = true;
                // Skip entropy-coded data, respecting escaped FF bytes and restart markers.
                while (offset < data.Length)
                {
                    if (data[offset++] != 255) { continue; }
                    var start = offset - 1;
                    while (offset < data.Length && data[offset] == 255) { offset++; }
                    if (offset >= data.Length) { break; }
                    var code = data[offset++];
                    if (code == 0 || code is >= 0xD0 and <= 0xD7) { continue; }
                    offset = start;
                    break;
                }
            }
        }
        throw new ImageInputException(ImageInspectionError.InvalidOrCorruptImage);
    }
}

internal sealed class ImageInputException(ImageInspectionError error) : Exception
{
    public ImageInspectionError Error { get; } = error;
}
