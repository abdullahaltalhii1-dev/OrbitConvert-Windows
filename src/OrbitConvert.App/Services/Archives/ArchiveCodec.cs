using System.IO;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace OrbitConvert.App.Services.Archives;

internal static class ArchiveCodec
{
    public static ArchiveInspection Read(string source, string? extraction, ArchiveFileSystem files)
    {
        using var input = files.ReadFile(source);
        if (input.Length > ArchiveLimits.InputBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        var (type, records) = ArchiveContainerGuard.Read(input);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var archive = ArchiveFactory.OpenArchive(input, new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false, ArchiveEncoding = new ArchiveEncoding { Default = type == ArchiveType.Zip ? Encoding.GetEncoding(437) : new UTF8Encoding(false, true) } });
        if (archive.Type != type) { throw new ArchiveRejected(ArchiveError.UnsupportedFormat); }
        var entries = new List<ArchiveEntryInfo>(); var paths = new ArchivePaths(); long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entries.Count >= ArchiveLimits.Entries || records is not null && entries.Count >= records.Length || !entry.IsComplete || entry.IsSplitAfter) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            if (entry.IsEncrypted) { throw new ArchiveRejected(ArchiveError.Encrypted); }
            if (entry is SevenZipArchiveEntry seven && (seven.IsAnti || seven.ExtendedAttrib is { } unix && (unix & 0xf000) is not (0 or 0x8000 or 0x4000)))
            { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
            // TAR's Attrib getter is not implemented in this release; strict raw type flags above
            // already reject links/devices/sparse/unknown extensions before library parsing.
            var entryAttributes = type == ArchiveType.Tar ? null : entry.Attrib;
            if (entry.LinkTarget is not null || entryAttributes is { } attributes && ((attributes & 0x400) != 0 || ((attributes >> 16) & 0xf000) is not (0 or 0x8000 or 0x4000)))
            { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
            var path = paths.Add(entry.Key ?? "", entry.IsDirectory);
            var record = records is null ? new(path, entry.IsDirectory, entry.Size, entry.CompressedSize, (uint)entry.Crc) : records[entries.Count];
            if (record.Path != path || record.Directory != entry.IsDirectory || record.Size != entry.Size || record.Compressed != entry.CompressedSize || record.Crc is { } expectedCrc && expectedCrc != (uint)entry.Crc)
            { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
            if (entry.IsDirectory)
            {
                if (extraction is not null) { files.EnsureParents(extraction, path, includeLeaf: true); }
                entries.Add(new(path, true, 0, 0, "")); continue;
            }
            if (entry.Size < 0 || entry.Size > ArchiveLimits.EntryBytes || checked(total + entry.Size) > ArchiveLimits.ExpandedBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
            using var content = entry.OpenEntryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var crc = new ArchiveCrc(); long size = 0;
            Stream output = Stream.Null;
            if (extraction is not null) { files.EnsureParents(extraction, path); output = files.NewFile(ArchivePaths.Under(extraction, path)); }
            try
            {
                var buffer = new byte[ArchiveLimits.BufferSize];
                int read;
                while ((read = content.Read(buffer)) != 0)
                {
                    size = checked(size + read); total = checked(total + read);
                    if (size > ArchiveLimits.EntryBytes || total > ArchiveLimits.ExpandedBytes || size > entry.Size ||
                        total > Math.Max(1024 * 1024, checked(input.Length * ArchiveLimits.Ratio)) ||
                        type == ArchiveType.Zip && size > Math.Max(1024 * 1024, checked(entry.CompressedSize * ArchiveLimits.Ratio))) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                    hash.AppendData(buffer, 0, read); crc.Append(buffer.AsSpan(0, read)); output.Write(buffer, 0, read);
                }
                if (size != entry.Size || record.Crc is { } checksum && crc.Value != checksum) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
                if (output is FileStream file) { file.Flush(true); }
            }
            finally { if (extraction is not null) { output.Dispose(); } }
            entries.Add(new(path, false, size, entry.CompressedSize, Convert.ToHexString(hash.GetHashAndReset())));
        }
        if (records is not null && entries.Count != records.Length) { throw new ArchiveRejected(ArchiveError.InvalidArchive); }
        return new(type == ArchiveType.Zip ? "ZIP" : type == ArchiveType.Tar ? "TAR" : "7z", input.Length, total, entries.ToArray());
    }

    public static ArchiveInspection Create(string candidate, ArchiveCreateEntry[] entries, string root, ArchiveFileSystem files, bool tar = false)
    {
        using (var output = files.NewFile(candidate))
        using (var bounded = new ArchiveBoundedOutput(output, ArchiveLimits.InputBytes))
        using (IWriter writer = tar
            ? WriterFactory.OpenWriter(bounded, ArchiveType.Tar, new WriterOptions(CompressionType.None) { LeaveStreamOpen = true, ArchiveEncoding = new ArchiveEncoding { Default = Encoding.UTF8 } })
            : new ZipWriter(bounded, new ZipWriterOptions(CompressionType.None) { LeaveStreamOpen = true, ArchiveEncoding = new ArchiveEncoding { Default = Encoding.UTF8 } }))
        {
            var paths = new ArchivePaths(); long total = 0;
            foreach (var entry in entries)
            {
                var path = paths.Add(entry.Path, entry.Directory);
                if (entry.Directory) { writer.WriteDirectory(path, null); continue; }
                using var source = files.ReadFile(ArchivePaths.Under(root, entry.Source));
                total = checked(total + source.Length);
                if (source.Length != entry.Size || total > ArchiveLimits.ExpandedBytes || source.Length > ArchiveLimits.EntryBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                if (Convert.ToHexString(SHA256.HashData(source)) != entry.Hash) { throw new ArchiveRejected(ArchiveError.UnreadableInput); }
                source.Position = 0;
                if (writer is ZipWriter zip) { zip.Write(path, source, new ZipWriterEntryOptions { CompressionType = CompressionType.Deflate, CompressionLevel = 5 }); }
                else { writer.Write(path, source, null); }
            }
        }
        var verified = Read(candidate, null, files);
        if (verified.Entries.Length != entries.Length || !verified.Entries.Zip(entries).All(pair => pair.First.Path == pair.Second.Path && pair.First.IsDirectory == pair.Second.Directory && pair.First.Size == pair.Second.Size && pair.First.Sha256 == pair.Second.Hash))
        { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
        return verified;
    }
}

internal sealed record ArchiveCreateEntry(string Path, string Source, bool Directory, long Size, string Hash);

internal sealed class ArchiveBoundedOutput(Stream inner, long maximum) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { if (value < 0 || value > maximum) { throw new ArchiveRejected(ArchiveError.TooLarge); } inner.Position = value; } }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { if (Position > maximum - buffer.Length) { throw new ArchiveRejected(ArchiveError.TooLarge); } inner.Write(buffer); }
    public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(Position + offset), _ => checked(Length + offset) }; return Position; }
    public override void SetLength(long value) { if (value < 0 || value > maximum) { throw new ArchiveRejected(ArchiveError.TooLarge); } inner.SetLength(value); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
