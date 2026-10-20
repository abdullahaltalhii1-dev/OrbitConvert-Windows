using System.Buffers.Binary;
using System.IO;
using System.Text;
using ImageMagick;

namespace OrbitConvert.App.Services;

// Reads container headers before Q8 can quantize source pixels. Native decoding still validates data/CRCs.
internal static class ImageInputGuard
{
    internal const int MaximumInputBytes = 25 * 1024 * 1024;
    internal const int MaximumOutputBytes = 96 * 1024 * 1024;
    internal const uint MaximumSide = 8192;
    internal const ulong MaximumPixels = 16_000_000;

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

        throw new ImageInputException(data.AsSpan().StartsWith("GIF8"u8) || data.AsSpan().StartsWith("RIFF"u8) ||
            data.AsSpan().StartsWith("BM"u8) || data.AsSpan().StartsWith("%PDF"u8)
            ? ImageInspectionError.UnsupportedFormat : ImageInspectionError.InvalidOrCorruptImage);
    }

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
