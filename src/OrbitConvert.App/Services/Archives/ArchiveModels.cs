namespace OrbitConvert.App.Services.Archives;

public enum ArchiveOperation { Extract, CreateZip, CreateTar, RepackZip }
public enum ArchiveError { None, UnreadableInput, UnsupportedFormat, Encrypted, UnsafePath, LinksNotSupported, DuplicateEntry, TooLarge, InvalidArchive, InvalidSelection, DestinationExists, InvalidDestination, OutputValidationFailed, Cancelled, TimedOut, ProcessTerminationFailed, OutputFailure }
public sealed record ArchiveEntryInfo(string Path, bool IsDirectory, long Size, long CompressedSize, string Sha256);
public sealed record ArchiveInspection(string Format, long InputBytes, long ExpandedBytes, ArchiveEntryInfo[] Entries);
public sealed record ArchiveRequest(ArchiveOperation Operation, string[] Sources, string Destination, string? FolderSource = null);
public sealed record ArchiveResult(ArchiveError Error, ArchiveInspection? Inspection = null, string OutputPath = "", bool CleanupFailed = false)
{ public bool IsSuccess => Error == ArchiveError.None; }
public interface IArchiveService
{
    Task<ArchiveResult> InspectAsync(string path, CancellationToken token = default);
    Task<ArchiveResult> ExecuteAsync(ArchiveRequest request, IProgress<string>? progress = null, CancellationToken token = default);
}
public static class ArchiveLimits
{
    public const int Entries = 1000, Depth = 16, RelativePath = 180, FullPath = 240;
    public const long InputBytes = 64 * 1024 * 1024, EntryBytes = 64 * 1024 * 1024, ExpandedBytes = 256 * 1024 * 1024;
    public const long TemporaryBytes = 768 * 1024 * 1024, JobMemoryBytes = 512 * 1024 * 1024;
    public const int Ratio = 200, BufferSize = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
}
internal sealed class ArchiveRejected(ArchiveError error) : Exception { public ArchiveError Error { get; } = error; }
