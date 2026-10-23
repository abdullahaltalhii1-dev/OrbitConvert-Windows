using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace OrbitConvert.App.Services.Archives;

internal sealed record ArchiveZipRecord(string Path, bool Directory, long Size, long Compressed, uint? Crc);

/// <summary>Bounded, non-ZIP64, single-volume Store/Deflate profile. No SFX, alternate paths or hidden local entries.</summary>
internal static class ArchiveZipGuard
{
    public static ArchiveZipRecord[] Read(Stream input)
    {
        if (input.Length < 22) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Position = input.Length - tail.Length; input.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        { if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; } }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
        { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        var count = U16(tail, end + 10);
        var centralSize = U32(tail, end + 12);
        var central = U32(tail, end + 16);
        var endPosition = input.Length - tail.Length + end;
        if (count > ArchiveLimits.Entries || centralSize > 4 * 1024 * 1024) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        if ((long)central + centralSize != endPosition) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        var records = new List<ArchiveZipRecord>(); var paths = new ArchivePaths();
        long localEnd = 0;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        for (var i = 0; i < count; i++)
        {
            input.Position = central;
            var header = new byte[46]; input.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            var flags = U16(header, 8); var method = U16(header, 10);
            if ((flags & 0x41) != 0) { throw new ArchiveRejected(ArchiveError.Encrypted); }
            if (method is not (0 or 8) || U16(header, 34) != 0) { throw new ArchiveRejected(ArchiveError.UnsupportedFormat); }
            var compressed = U32(header, 20); var size = U32(header, 24); var crc = U32(header, 16);
            if (size > ArchiveLimits.EntryBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
            var nameLength = U16(header, 28); var extraLength = U16(header, 30); var commentLength = U16(header, 32);
            if (nameLength is 0 or > 720 || extraLength > 4096 || commentLength > 4096) { throw new ArchiveRejected(ArchiveError.TooLarge); }
            var name = new byte[nameLength]; input.ReadExactly(name);
            var extra = new byte[extraLength]; input.ReadExactly(extra);
            // Reject name overrides, ZIP64, encryption metadata and ambiguous legacy Unicode extras.
            for (var offset = 0; offset < extra.Length;)
            {
                if (offset + 4 > extra.Length || offset + 4 + U16(extra, offset + 2) > extra.Length) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
                if (U16(extra, offset) is 1 or 0x7075 or 0x9901) { throw new ArchiveRejected(ArchiveError.UnsupportedFormat); }
                offset += 4 + U16(extra, offset + 2);
            }
            var decoded = (flags & 0x800) != 0 ? new UTF8Encoding(false, true).GetString(name) : Encoding.GetEncoding(437).GetString(name);
            var attributes = U32(header, 38); var unixType = (attributes >> 16) & 0xf000;
            if ((attributes & 0x400) != 0 || unixType is not (0 or 0x8000 or 0x4000)) { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
            var directory = decoded.EndsWith('/') || decoded.EndsWith('\\');
            if (directory && (size != 0 || compressed != 0 || crc != 0) || !directory && (unixType == 0x4000 || (attributes & 0x10) != 0))
            { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            var relative = paths.Add(decoded, directory);
            central = checked(central + 46u + nameLength + extraLength + commentLength);
            if (central > endPosition || U32(header, 42) != localEnd) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            input.Position = localEnd;
            var local = new byte[30]; input.ReadExactly(local);
            if (U32(local, 0) != 0x04034b50 || U16(local, 6) != flags || U16(local, 8) != method || U16(local, 26) != nameLength || U16(local, 28) > 4096)
            { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            var localName = new byte[nameLength]; input.ReadExactly(localName);
            if (!name.SequenceEqual(localName)) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            localEnd = checked(input.Position + U16(local, 28) + compressed);
            if (localEnd > U32(tail, end + 16)) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            if ((flags & 8) != 0)
            {
                input.Position = localEnd;
                var descriptor = new byte[12]; input.ReadExactly(descriptor);
                if (U32(descriptor, 0) == 0x08074b50) { input.Position = localEnd + 4; input.ReadExactly(descriptor); localEnd += 4; }
                if (U32(descriptor, 0) != crc || U32(descriptor, 4) != compressed || U32(descriptor, 8) != size) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
                localEnd += 12;
            }
            else if (U32(local, 14) != crc || U32(local, 18) != compressed || U32(local, 22) != size) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            records.Add(new(relative, directory, size, compressed, crc));
        }
        if (localEnd != U32(tail, end + 16) || central != endPosition) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        input.Position = 0;
        return records.ToArray();
    }
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}

internal sealed class ArchiveCrc
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(value =>
    { var crc = (uint)value; for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); } return crc; }).ToArray();
    private uint _value = uint.MaxValue;
    public uint Value => ~_value;
    public void Append(ReadOnlySpan<byte> bytes) { foreach (var value in bytes) { _value = Table[(_value ^ value) & 255] ^ (_value >> 8); } }
}
