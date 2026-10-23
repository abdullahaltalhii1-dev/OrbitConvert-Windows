using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Archives;

public sealed class ArchiveService(IProcessRunner runner) : IArchiveService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public ArchiveService() : this(new ProcessRunner()) { }
    public Task<ArchiveResult> InspectAsync(string path, CancellationToken token = default) =>
        RunAsync(null, [path], "", null, null, token);
    public Task<ArchiveResult> ExecuteAsync(ArchiveRequest request, IProgress<string>? progress = null, CancellationToken token = default) =>
        RunAsync(request.Operation, request.Sources, request.Destination, request.FolderSource, progress, token);

    private Task<ArchiveResult> RunAsync(ArchiveOperation? operation, string[] sources, string destination, string? folder, IProgress<string>? progress, CancellationToken token) => Task.Run(async () =>
    {
        using var originals = new ArchiveFileSystem();
        using var work = new ArchiveFileSystem();
        using var stagingFiles = new ArchiveFileSystem();
        var entered = false; string? workspace = null, staging = null;
        FileStream? stagingFile = null;
        var result = new ArchiveResult(ArchiveError.InvalidSelection);
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            if (operation is not null && !Enum.IsDefined(operation.Value) || sources.Length > ArchiveLimits.Entries) { throw new ArchiveRejected(ArchiveError.InvalidSelection); }
            var create = operation is ArchiveOperation.CreateZip or ArchiveOperation.CreateTar;
            if (!create && (sources.Length != 1 || folder is not null)) { throw new ArchiveRejected(ArchiveError.InvalidSelection); }
            if (operation is not null)
            {
                destination = LocalPath(destination);
                var parent = Path.GetDirectoryName(destination)!;
                stagingFiles.PinDirectory(parent);
                if (File.Exists(destination) || Directory.Exists(destination)) { throw new ArchiveRejected(ArchiveError.DestinationExists); }
                var extension = operation == ArchiveOperation.CreateTar ? ".tar" : ".zip";
                if (operation != ArchiveOperation.Extract && !Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase))
                { throw new ArchiveRejected(ArchiveError.InvalidDestination); }
            }
            workspace = Path.Combine(Path.GetTempPath(), ".orbitconvert-archive-" + Guid.NewGuid().ToString("N"));
            work.MakeDirectory(workspace, rename: true);
            progress?.Report("Inspecting");
            var inputs = new List<ArchiveCreateEntry>();
            var paths = new ArchivePaths(); long total = 0;
            async Task AddFile(string path, string name)
            {
                token.ThrowIfCancellationRequested();
                path = LocalPath(path);
                if (operation is not null && path.Equals(destination, StringComparison.OrdinalIgnoreCase)) { throw new ArchiveRejected(ArchiveError.InvalidDestination); }
                FileStream input;
                try { input = originals.ReadFile(path); } // Retained read-only until publication/cleanup completes.
                catch (IOException) { throw new ArchiveRejected(ArchiveError.UnreadableInput); }
                if (input.Length > (create ? ArchiveLimits.EntryBytes : ArchiveLimits.InputBytes)) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                total = checked(total + input.Length);
                if (total > ArchiveLimits.ExpandedBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                var id = create ? "input-" + inputs.Count.ToString("D4") : "input";
                string hash;
                using (var snapshot = work.NewFile(Path.Combine(workspace, id)))
                { hash = await CopyAsync(input, snapshot, input.Length, token).ConfigureAwait(false); snapshot.Flush(true); }
                _ = work.ReadFile(Path.Combine(workspace, id)); // Keep immutable while the child parses it.
                inputs.Add(new(paths.Add(name, false), id, false, input.Length, hash));
            }
            foreach (var source in sources) { await AddFile(source, Path.GetFileName(source)).ConfigureAwait(false); }
            if (create && folder is not null)
            {
                folder = LocalPath(folder); originals.PinDirectory(folder);
                var prefix = ArchivePaths.Validate(Path.GetFileName(folder));
                var pending = new Stack<(string Full, string Name)>(); pending.Push((folder, prefix));
                while (pending.TryPop(out var next))
                {
                    token.ThrowIfCancellationRequested(); originals.PinDirectory(next.Full);
                    inputs.Add(new(paths.Add(next.Name, true), "", true, 0, ""));
                    if (inputs.Count > ArchiveLimits.Entries) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(next.Full))
                    {
                        token.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) { throw new ArchiveRejected(ArchiveError.LinksNotSupported); }
                        var name = next.Name + "/" + Path.GetFileName(entry);
                        _ = ArchivePaths.Validate(name);
                        if (Directory.Exists(entry)) { pending.Push((entry, name)); }
                        else { await AddFile(entry, name).ConfigureAwait(false); }
                        if (inputs.Count + pending.Count > ArchiveLimits.Entries) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                    }
                }
            }
            var action = operation switch { ArchiveOperation.CreateZip => "zip", ArchiveOperation.CreateTar => "tar", ArchiveOperation.Extract => "extract", ArchiveOperation.RepackZip => "repack", _ => "inspect" };
            progress?.Report(create ? "Creating" : operation == ArchiveOperation.Extract ? "Extracting" : operation == ArchiveOperation.RepackZip ? "Repacking" : "Inspecting");
            var response = await WorkerAsync(workspace, new(action, "response.json", create ? inputs.ToArray() : null), work, token).ConfigureAwait(false);
            if (response.Error != ArchiveError.None) { throw new ArchiveRejected(response.Error); }
            var inspection = response.Inspection ?? throw new ArchiveRejected(ArchiveError.OutputValidationFailed);
            ValidateManifest(inspection);
            if (operation is null) { result = new(ArchiveError.None, inspection); }
            else
            {
                progress?.Report("Verifying");
                var parentPath = Path.GetDirectoryName(destination)!;
                staging = Path.Combine(parentPath, ".orbitconvert-archive-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (operation == ArchiveOperation.Extract)
                {
                    stagingFiles.MakeDirectory(staging, rename: true);
                    foreach (var entry in inspection.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.IsDirectory) { stagingFiles.EnsureParents(staging, entry.Path, includeLeaf: true); continue; }
                        stagingFiles.EnsureParents(staging, entry.Path);
                        using var candidate = work.ReadFile(ArchivePaths.Under(Path.Combine(workspace, "expanded"), entry.Path));
                        var file = stagingFiles.NewFile(ArchivePaths.Under(staging, entry.Path));
                        var hash = await CopyAsync(candidate, file, entry.Size, token).ConfigureAwait(false); file.Flush(true);
                        file.Position = 0;
                        if (hash != entry.Sha256 || Convert.ToHexString(SHA256.HashData(file)) != entry.Sha256) { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
                    }
                    token.ThrowIfCancellationRequested();
                    stagingFiles.PublishDirectory(staging, stagingFiles.PinDirectory(parentPath)!, Path.GetFileName(destination));
                }
                else
                {
                    using var candidate = work.ReadFile(Path.Combine(workspace, "candidate"));
                    if (candidate.Length > ArchiveLimits.InputBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
                    var file = stagingFile = stagingFiles.NewFile(staging, rename: true);
                    var hash = await CopyAsync(candidate, file, candidate.Length, token).ConfigureAwait(false); file.Flush(true); file.Position = 0;
                    if (hash != response.Hash || Convert.ToHexString(SHA256.HashData(file)) != response.Hash) { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
                    token.ThrowIfCancellationRequested();
                    ArchiveFileSystem.Publish(file.SafeFileHandle, stagingFiles.PinDirectory(parentPath)!, Path.GetFileName(destination));
                }
                staging = null; result = new(ArchiveError.None, inspection, destination); progress?.Report("Completed");
            }
        }
        catch (OperationCanceledException) { result = new(ArchiveError.Cancelled); }
        catch (ArchiveRejected error) { result = new(error.Error); }
        catch (IOException) when (operation is not null && (File.Exists(destination) || Directory.Exists(destination))) { result = new(ArchiveError.DestinationExists); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException or JsonException)
        { result = new(operation is null ? ArchiveError.UnreadableInput : ArchiveError.OutputFailure); }
        finally
        {
            if (staging is not null && result.Error != ArchiveError.ProcessTerminationFailed)
            {
                if (Directory.Exists(staging)) { if (!stagingFiles.CleanupOwnedDirectory(staging)) { result = result with { CleanupFailed = true }; } }
                else if (stagingFile is not null && !ArchiveFileSystem.CleanupOwnedFile(stagingFile)) { result = result with { CleanupFailed = true }; }
            }
            if (workspace is not null && result.Error != ArchiveError.ProcessTerminationFailed && !work.CleanupOwnedDirectory(workspace)) { result = result with { CleanupFailed = true }; }
            if (entered) { Gate.Release(); }
        }
        return result.Error == ArchiveError.ProcessTerminationFailed ? result with { CleanupFailed = true } : result;
    }, CancellationToken.None);

    private async Task<ArchiveWorkerResponse> WorkerAsync(string root, ArchiveWorkerRequest request, ArchiveFileSystem files, CancellationToken token)
    {
        var path = Path.Combine(root, "request.json");
        using (var output = files.NewFile(path)) { JsonSerializer.Serialize(output, request); output.Flush(true); }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var overBudget = false;
        var monitor = Task.Run(async () =>
        {
            using var pins = new ArchiveFileSystem(root);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    long total = 0; var count = 0;
                    // The child cannot create links; never traverse attacker-inserted reparse points.
                    var pending = new Stack<string>(); pending.Push(root);
                    while (pending.TryPop(out var directory))
                    {
                        pins.PinDirectory(directory);
                        foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                        {
                            if (++count > 3 * ArchiveLimits.Entries + 20 || (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                            { overBudget = true; stop.Cancel(); return; }
                            if (Directory.Exists(item)) { pending.Push(item); }
                            else if ((total = checked(total + new FileInfo(item).Length)) > ArchiveLimits.TemporaryBytes) { overBudget = true; stop.Cancel(); return; }
                        }
                    }
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or ArchiveRejected) { overBudget = true; stop.Cancel(); }
        }, CancellationToken.None);
        ProcessRunResult run;
        try
        {
            run = await runner.RunAsync(new(Path.Combine(AppContext.BaseDirectory, "OrbitConvert.App.exe"), ["--archive-worker", path],
                ArchiveLimits.Timeout, MaximumJobMemoryBytes: ArchiveLimits.JobMemoryBytes), stop.Token).ConfigureAwait(false);
        }
        finally { stop.Cancel(); await monitor.ConfigureAwait(false); }
        if (!run.TerminationConfirmed) { throw new ArchiveRejected(ArchiveError.ProcessTerminationFailed); }
        token.ThrowIfCancellationRequested();
        if (overBudget) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        if (run.Outcome == ProcessOutcome.TimedOut) { throw new ArchiveRejected(ArchiveError.TimedOut); }
        var responsePath = Path.Combine(root, request.Response);
        using var response = files.ReadFile(responsePath);
        if (response.Length > 1024 * 1024) { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
        var value = JsonSerializer.Deserialize<ArchiveWorkerResponse>(response) ?? throw new ArchiveRejected(ArchiveError.OutputValidationFailed);
        if (run.Outcome != ProcessOutcome.Exited || run.ExitCode != 0 && value.Error == ArchiveError.None) { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
        return value;
    }
    private static string LocalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith("\\\\", StringComparison.Ordinal) || full.Length > ArchiveLimits.FullPath)
        { throw new ArchiveRejected(ArchiveError.UnsafePath); }
        ArchivePaths.Validate(Path.GetFileName(full));
        return full;
    }
    private static async Task<string> CopyAsync(Stream input, Stream output, long size, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[ArchiveLimits.BufferSize]; long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        { total = checked(total + read); if (total > size || total > ArchiveLimits.ExpandedBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); } hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); }
        if (total != size) { throw new ArchiveRejected(ArchiveError.UnreadableInput); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void ValidateManifest(ArchiveInspection inspection)
    {
        var paths = new ArchivePaths(); long total = 0;
        if (inspection.Entries.Length > ArchiveLimits.Entries || inspection.InputBytes > ArchiveLimits.InputBytes) { throw new ArchiveRejected(ArchiveError.TooLarge); }
        foreach (var entry in inspection.Entries)
        {
            paths.Add(entry.Path, entry.IsDirectory); total = checked(total + entry.Size);
            if (entry.Size < 0 || entry.Size > ArchiveLimits.EntryBytes || total > ArchiveLimits.ExpandedBytes ||
                entry.IsDirectory && (entry.Size != 0 || entry.Sha256.Length != 0) || !entry.IsDirectory && (entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit)))
            { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
        }
        if (total != inspection.ExpandedBytes) { throw new ArchiveRejected(ArchiveError.OutputValidationFailed); }
    }
}
