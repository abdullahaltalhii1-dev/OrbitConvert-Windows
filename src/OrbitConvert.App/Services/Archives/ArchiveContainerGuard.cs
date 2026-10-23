using System.Buffers.Binary;
using System.IO;
using System.Text;
using SharpCompress.Common;

namespace OrbitConvert.App.Services.Archives;

internal static class ArchiveContainerGuard
{
    public static (ArchiveType Type, ArchiveZipRecord[]? Records) Read(Stream input)
    {
        var header = new byte[(int)Math.Min(512, input.Length)]; input.ReadExactly(header); input.Position = 0;
        if (header.Length >= 4 && header[0] == 'P' && header[1] == 'K') { return (ArchiveType.Zip, ArchiveZipGuard.Read(input)); }
        if (header.Length >= 6 && header.AsSpan(0, 6).SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }))
        { CheckSevenZip(input); return (ArchiveType.SevenZip, null); }
        if (header.Length == 512 && (header.AsSpan(257, 5).SequenceEqual("ustar"u8) || header.All(x => x == 0) || LooksLikeV7Tar(header)))
        { var entries = CheckTar(input); return (ArchiveType.Tar, entries); }
        throw new ArchiveRejected(ArchiveError.UnsupportedFormat);
    }
    private static bool LooksLikeV7Tar(byte[] header)
    {
        try
        {
            long checksum = 0;
            for (var i = 0; i < header.Length; i++) { checksum += i is >= 148 and < 156 ? 32 : header[i]; }
            return checksum == Octal(header.AsSpan(148, 8));
        }
        catch (Exception error) when (error is ArchiveRejected or OverflowException) { return false; }
    }
    private static void CheckSevenZip(Stream input)
    {
        var header = new byte[32]; input.ReadExactly(header);
        if (header[6] != 0 || header[7] > 4) { throw new ArchiveRejected(ArchiveError.UnsupportedFormat); }
        var crc = new ArchiveCrc(); crc.Append(header.AsSpan(12));
        if (crc.Value != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12));
        var size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20));
        // Bound the OUTER next-header allocation (GHSA-7jcq-g98g-83v5), independently of
        // upstream's physical-range fix since 0.50.5. Internal encoded-header allocations
        // are not covered here: parsing starts only after the worker's job quota is active.
        if (size > 1024 * 1024 || offset > (ulong)(input.Length - 32) || size > (ulong)(input.Length - 32) - offset)
        { throw new ArchiveRejected(ArchiveError.TooLarge); }
        if (offset + size != (ulong)(input.Length - 32)) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        input.Position = checked(32 + (long)offset); crc = new();
        var buffer = new byte[ArchiveLimits.BufferSize]; var remaining = (long)size;
        while (remaining > 0) { var count = (int)Math.Min(buffer.Length, remaining); input.ReadExactly(buffer.AsSpan(0, count)); crc.Append(buffer.AsSpan(0, count)); remaining -= count; }
        if (crc.Value != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28))) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        input.Position = 0;
    }
    private static ArchiveZipRecord[] CheckTar(Stream input)
    {
        var entries = new List<ArchiveZipRecord>(); var paths = new ArchivePaths(); string? longName = null; var zeros = 0;
        var header = new byte[512];
        while (input.Position < input.Length)
        {
            input.ReadExactly(header);
            if (header.All(x => x == 0)) { zeros++; continue; }
            if (zeros != 0) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            long checksum = 0;
            for (var i = 0; i < header.Length; i++) { checksum += i is >= 148 and < 156 ? 32 : header[i]; }
            if (checksum != Octal(header.AsSpan(148, 8))) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            var size = Octal(header.AsSpan(124, 12)); var type = header[156];
            if (size > ArchiveLimits.EntryBytes || entries.Count >= ArchiveLimits.Entries) { throw new ArchiveRejected(ArchiveError.TooLarge); }
            if (type is (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4' or (byte)'6' or (byte)'K') { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
            if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'L')) { throw new ArchiveRejected(ArchiveError.UnsupportedFormat); }
            var end = checked(input.Position + ((size + 511) / 512) * 512);
            if (end > input.Length) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            if (type == 'L')
            {
                if (longName is not null || size is < 1 or > 2048) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
                var data = new byte[(int)size]; input.ReadExactly(data); longName = Text(data);
            }
            else
            {
                var name = longName ?? Text(header.AsSpan(0, 100)); longName = null;
                if (header.AsSpan(257, 8).SequenceEqual("ustar\000"u8) && Text(header.AsSpan(345, 155)) is { Length: > 0 } prefix) { name = prefix + "/" + name; }
                var directory = type == '5';
                if (directory && size != 0) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
                entries.Add(new(paths.Add(name, directory), directory, size, size, null));
            }
            input.Position = end;
        }
        if (zeros < 2 || longName is not null) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        input.Position = 0; return entries.ToArray();
    }
    private static string Text(ReadOnlySpan<byte> bytes)
    { var end = bytes.IndexOf((byte)0); return new UTF8Encoding(false, true).GetString(end < 0 ? bytes : bytes[..end]); }
    private static long Octal(ReadOnlySpan<byte> field)
    {
        var text = Encoding.ASCII.GetString(field).Trim('\0', ' '); long value = 0;
        foreach (var digit in text) { if (digit is < '0' or > '7') { throw new ArchiveRejected(ArchiveError.InvalidArchive); } value = checked(value * 8 + digit - '0'); }
        return value;
    }
}
