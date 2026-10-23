using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OrbitConvert.App.Services.Archives;

internal sealed record ArchiveWorkerRequest(string Action, string Response, ArchiveCreateEntry[]? Files = null);
internal sealed record ArchiveWorkerResponse(ArchiveError Error, ArchiveInspection? Inspection = null, string Hash = "");

/// <summary>Archive parsing/decompression stays outside WPF in the existing contained child-process model.</summary>
internal static class ArchiveWorker
{
    public static int Run(string requestPath)
    {
        // The parent closes redirected stdin only after successful Windows job assignment.
        // Do not open even request.json before containment; discard input without buffering it.
        if (!Console.IsInputRedirected) { return 2; }
        try { while (Console.In.Read() != -1) { } }
        catch (IOException) { return 2; }
        string? response = null;
        ArchiveWorkerResponse result;
        var root = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
        if (!Path.GetFileName(root).StartsWith(".orbitconvert-archive-", StringComparison.Ordinal) ||
            !Path.GetDirectoryName(root)!.Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { return 2; }
        using var files = new ArchiveFileSystem(root);
        try
        {
            using var requestFile = files.ReadFile(requestPath);
            if (requestFile.Length > 512 * 1024) { return 2; }
            var request = JsonSerializer.Deserialize<ArchiveWorkerRequest>(requestFile) ?? throw new ArchiveRejected(ArchiveError.InvalidSelection);
            response = ArchivePaths.Under(root, request.Response);
            if (request.Action is not ("inspect" or "extract" or "zip" or "tar" or "repack")) { throw new ArchiveRejected(ArchiveError.InvalidSelection); }
            ArchiveInspection inspection;
            Console.WriteLine("archive-worker-ready"); Console.Out.Flush();
            if (request.Action is "zip" or "tar")
            {
                inspection = ArchiveCodec.Create(Path.Combine(root, "candidate"), request.Files ?? [], root, files, request.Action == "tar");
            }
            else
            {
                var expanded = request.Action == "inspect" ? null : Path.Combine(root, "expanded");
                if (expanded is not null) { files.MakeDirectory(expanded); }
                inspection = ArchiveCodec.Read(Path.Combine(root, "input"), expanded, files);
                if (request.Action == "repack")
                {
                    var inputs = inspection.Entries.Select(x => new ArchiveCreateEntry(x.Path, x.Path, x.IsDirectory, x.Size, x.Sha256)).ToArray();
                    inspection = ArchiveCodec.Create(Path.Combine(root, "candidate"), inputs, expanded!, files);
                }
            }
            var hash = "";
            if (request.Action is "zip" or "tar" or "repack") { using var candidate = files.ReadFile(Path.Combine(root, "candidate")); hash = Convert.ToHexString(SHA256.HashData(candidate)); }
            result = new(ArchiveError.None, inspection, hash);
        }
        catch (ArchiveRejected error) { result = new(error.Error); }
        // InsufficientMemoryException derives from OutOfMemoryException. A fatal OS/runtime
        // failure may still prevent a response; the parent must not infer a memory cause then.
        catch (OutOfMemoryException) { result = new(ArchiveError.TooLarge); }
        catch (CryptographicException) { result = new(ArchiveError.Encrypted); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException or CryptographicException or OverflowException or SharpCompress.Common.SharpCompressException)
        { result = new(ArchiveError.InvalidArchive); }
        if (response is null) { return 2; }
        using (var output = files.NewFile(response)) { JsonSerializer.Serialize(output, result); output.Flush(true); }
        return result.Error == ArchiveError.None ? 0 : 1;
    }
}
