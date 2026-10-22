using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ImageMagick;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Pdf;

public interface IPdfService
{
    Task<PdfInspectionResult> InspectAsync(string path, CancellationToken token = default);
    Task<PdfResult> ExecuteAsync(PdfRequest request, IProgress<PdfProgress>? progress = null, CancellationToken token = default);
}

/// <summary>PDF-only orchestration; no WPF types, shell, runtime downloads, or general batch queue.</summary>
public sealed class PdfService(QpdfLocator locator, IProcessRunner runner) : IPdfService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public PdfService() : this(new QpdfLocator(), new ProcessRunner()) { }

    public Task<PdfInspectionResult> InspectAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        string? workspace = null;
        var entered = false;
        var result = new PdfInspectionResult(null, PdfError.InvalidPdf);
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            workspace = NewWorkspace();
            using var original = OpenSource(path);
            var copy = await CopyAsync(original, workspace, 0, token).ConfigureAwait(false);
            var engine = await EngineAsync(workspace, token).ConfigureAwait(false);
            var inspection = await InspectCopyAsync(engine, copy, workspace, token).ConfigureAwait(false);
            result = new(inspection with { FileName = Path.GetFileName(path) }, PdfError.None);
        }
        catch (Exception error) when (Expected(error)) { result = new(null, Error(error)); }
        finally { if (workspace is not null && !Cleanup(workspace, true)) { result = result with { CleanupFailed = true }; } if (entered) { Gate.Release(); } }
        return result.Error == PdfError.ProcessTerminationFailed ? result with { CleanupFailed = true } : result;
    }, CancellationToken.None);

    public Task<PdfResult> ExecuteAsync(PdfRequest request, IProgress<PdfProgress>? progress = null, CancellationToken token = default) =>
        Task.Run(() => ExecuteCoreAsync(request, progress, token), CancellationToken.None);

    private async Task<PdfResult> ExecuteCoreAsync(PdfRequest request, IProgress<PdfProgress>? progress, CancellationToken token)
    {
        string? workspace = null, staging = null;
        var stagingDirectory = false;
        var entered = false;
        var originals = new List<FileStream>();
        var result = new PdfResult(PdfError.InvalidSelection);
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            if (!Enum.IsDefined(request.Operation) || request.Sources.Length is < 1 or > 20 || request.Operation != PdfOperation.Merge && request.Sources.Length != 1 ||
                request.Operation == PdfOperation.Merge && request.Sources.Length < 2) { throw new PdfRejected(PdfError.InvalidSelection); }
            var paths = request.Sources.Select(Path.GetFullPath).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) { throw new PdfRejected(PdfError.DuplicateInput); }
            var destination = Path.GetFullPath(request.Destination);
            if (paths.Contains(destination, StringComparer.OrdinalIgnoreCase)) { throw new PdfRejected(PdfError.InvalidDestination); }
            if (File.Exists(destination) || Directory.Exists(destination)) { throw new PdfRejected(PdfError.DestinationExists); }
            if (!Directory.Exists(Path.GetDirectoryName(destination))) { throw new PdfRejected(PdfError.InvalidDestination); }
            workspace = NewWorkspace();
            var engine = await EngineAsync(workspace, token).ConfigureAwait(false);
            var copies = new List<string>();
            var inputs = new List<PdfInspection>();
            long bytes = 0;
            for (var index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var original = OpenSource(paths[index]); originals.Add(original);
                bytes += original.Length;
                if (bytes > PdfLimits.AggregateInputBytes) { throw new PdfRejected(PdfError.TooLarge); }
                var copy = await CopyAsync(original, workspace, index, token).ConfigureAwait(false); copies.Add(copy);
                inputs.Add(await InspectCopyAsync(engine, copy, workspace, token).ConfigureAwait(false));
                progress?.Report(new(index + 1, paths.Length, "Inspecting"));
            }
            if (inputs.Sum(x => x.Pages.Length) > PdfLimits.Pages) { throw new PdfRejected(PdfError.TooLarge); }
            int[] selected;
            try { selected = PdfPageSelection.Parse(request.Pages, inputs[0].Pages.Length); }
            catch (ArgumentException) { throw new PdfRejected(PdfError.InvalidSelection); }
            var groups = Groups(request.Operation, selected, inputs[0].Pages.Length);
            var multiple = request.Operation is PdfOperation.IndividualPages or PdfOperation.Boundaries || request.Operation == PdfOperation.Render && selected.Length > 1;
            if ((request.Operation is PdfOperation.IndividualPages or PdfOperation.Boundaries or PdfOperation.Render) && groups.Length > PdfLimits.OutputFiles) { throw new PdfRejected(PdfError.TooLarge); }
            var extension = request.Operation switch { PdfOperation.Render => ".png", PdfOperation.Text => ".txt", _ => ".pdf" };
            if (!multiple && !Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase)) { throw new PdfRejected(PdfError.InvalidDestination); }
            var candidates = new List<string>();
            var names = new List<string>();
            var preview = "";
            if (request.Operation is PdfOperation.Render or PdfOperation.Text)
            {
                progress?.Report(new(0, selected.Length, request.Operation == PdfOperation.Render ? "Rendering" : "Extracting"));
                var response = await WorkerAsync(workspace, request.Operation == PdfOperation.Render ? "render" : "text", copies[0], selected, request.Dpi, token).ConfigureAwait(false);
                if (request.Operation == PdfOperation.Text)
                {
                    var candidate = Path.Combine(workspace, "text.txt");
                    var text = await File.ReadAllTextAsync(candidate, Utf8, token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text) || text.Length > PdfLimits.TextCharacters || response.Preview != text[..Math.Min(4000, text.Length)]) { throw new PdfRejected(PdfError.OutputValidationFailed); }
                    preview = response.Preview; candidates.Add(candidate); names.Add("text.txt");
                }
                else
                {
                    long pixels = 0;
                    foreach (var page in selected)
                    {
                        var name = $"page-{page:D4}.png";
                        var candidate = Path.Combine(workspace, name);
                        using var image = new MagickImage(candidate);
                        var info = inputs[0].Pages[page - 1];
                        var width = (uint)Math.Ceiling(info.Width * request.Dpi / 72.0);
                        var height = (uint)Math.Ceiling(info.Height * request.Dpi / 72.0);
                        pixels += (long)width * height;
                        if (image.Format != MagickFormat.Png || image.Width != width || image.Height != height ||
                            (long)width * height > PdfLimits.PagePixels || pixels > PdfLimits.TotalPixels) { throw new PdfRejected(PdfError.OutputValidationFailed); }
                        candidates.Add(candidate); names.Add(name);
                    }
                }
            }
            else
            {
                for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    var candidate = Path.Combine(workspace, $"output-{groupIndex:D4}.pdf");
                    var arguments = new List<string> { "--empty", "--pages" };
                    PdfPageInfo[] expected;
                    if (request.Operation == PdfOperation.Merge)
                    {
                        foreach (var copy in copies) { arguments.Add(copy); arguments.Add("1-z"); }
                        expected = inputs.SelectMany(x => x.Pages).ToArray();
                    }
                    else
                    {
                        arguments.Add(copies[0]); arguments.Add(string.Join(',', groups[groupIndex]));
                        expected = groups[groupIndex].Select(x => inputs[0].Pages[x - 1]).ToArray();
                    }
                    arguments.Add("--"); arguments.Add(candidate);
                    progress?.Report(new(groupIndex, groups.Length, "Processing"));
                    Check(await RunAsync(engine, arguments, workspace, token).ConfigureAwait(false));
                    progress?.Report(new(groupIndex, groups.Length, "Verifying"));
                    var output = await InspectCopyAsync(engine, candidate, workspace, token).ConfigureAwait(false);
                    if (!output.Pages.Select(x => x.Fingerprint).SequenceEqual(expected.Select(x => x.Fingerprint))) { throw new PdfRejected(PdfError.OutputValidationFailed); }
                    candidates.Add(candidate);
                    names.Add(request.Operation == PdfOperation.IndividualPages ? $"page-{groups[groupIndex][0]:D4}.pdf" : $"part-{groupIndex + 1:D4}.pdf");
                }
            }
            token.ThrowIfCancellationRequested();
            if (multiple)
            {
                staging = Path.Combine(Path.GetDirectoryName(destination)!, ".orbitconvert-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
                if (!CreateDirectory(staging, IntPtr.Zero)) { staging = null; throw new IOException("Could not reserve results directory."); }
                stagingDirectory = true;
                for (var i = 0; i < candidates.Count; i++) { await StageAsync(candidates[i], Path.Combine(staging, names[i]), token).ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                Directory.Move(staging, destination); // Same-parent, no-replace directory rename: publish the whole verified set at once.
                staging = null;
                result = new(PdfError.None, names.Select(x => Path.Combine(destination, x)).ToArray(), preview);
            }
            else
            {
                var path = Path.Combine(Path.GetDirectoryName(destination)!, ".orbitconvert-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    staging = path;
                    using var input = new FileStream(candidates.Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
                    await input.CopyToAsync(output, token).ConfigureAwait(false); output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(path, destination, overwrite: false); staging = null;
                result = new(PdfError.None, [destination], preview);
            }
            progress?.Report(new(candidates.Count, candidates.Count, "Completed"));
        }
        catch (IOException) when (File.Exists(request.Destination) || Directory.Exists(request.Destination)) { result = new(PdfError.DestinationExists); }
        catch (Exception error) when (Expected(error)) { result = new(Error(error)); }
        finally
        {
            foreach (var original in originals) { original.Dispose(); }
            if (workspace is not null && !Cleanup(workspace, true)) { result = result with { CleanupFailed = true }; }
            if (staging is not null && !Cleanup(staging, stagingDirectory)) { result = result with { CleanupFailed = true }; }
            if (entered) { Gate.Release(); }
        }
        return result.Error == PdfError.ProcessTerminationFailed ? result with { CleanupFailed = true } : result;
    }

    private async Task<PdfInspection> InspectCopyAsync(string engine, string copy, string workspace, CancellationToken token)
    {
        using (var input = File.OpenRead(copy))
        {
            Span<byte> magic = stackalloc byte[5];
            if (input.Length is < 8 or > PdfLimits.InputBytes || input.Read(magic) != 5 || !magic.SequenceEqual("%PDF-"u8)) { throw new PdfRejected(PdfError.InvalidPdf); }
        }
        var encrypted = await RunAsync(engine, ["--is-encrypted", copy], workspace, token).ConfigureAwait(false);
        CheckOutcome(encrypted);
        if (encrypted.ExitCode == 0) { throw new PdfRejected(PdfError.Encrypted); }
        if (encrypted.ExitCode != 2) { throw new PdfRejected(PdfError.InvalidPdf); }
        Check(await RunAsync(engine, ["--check", copy], workspace, token).ConfigureAwait(false)); // Warnings/recovery are not accepted.
        var json = Path.Combine(workspace, "structure-" + Guid.NewGuid().ToString("N") + ".json");
        Check(await RunAsync(engine, ["--json-output=2", "--json-stream-data=none", copy, json], workspace, token).ConfigureAwait(false));
        if (new FileInfo(json).Length > 32 * 1024 * 1024) { throw new PdfRejected(PdfError.TooLarge); }
        using (var structure = JsonDocument.Parse(await File.ReadAllTextAsync(json, Utf8, token).ConfigureAwait(false), new() { MaxDepth = 128 })) { Vet(structure.RootElement); }
        return (await WorkerAsync(workspace, "inspect", copy, [], 150, token).ConfigureAwait(false)).Inspection ?? throw new PdfRejected(PdfError.InvalidPdf);
    }
    private static void Vet(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in node.EnumerateObject())
            {
                if (member.Name is "/JS" or "/JavaScript" or "/OpenAction" or "/AA" or "/EmbeddedFiles" or "/EF" or "/AcroForm" or "/XFA" or "/ByteRange" ||
                    member.Value.ValueKind == JsonValueKind.String && member.Value.GetString() is "/JavaScript" or "/Launch" or "/GoToR" or "/SubmitForm" or "/ImportData" or "/RichMedia" or "/EmbeddedFile" or "/Filespec" or "/Sig")
                { throw new PdfRejected(PdfError.UnsafeContent); }
                Vet(member.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) { foreach (var child in node.EnumerateArray()) { Vet(child); } }
    }
    private async Task<PdfWorkerResponse> WorkerAsync(string workspace, string action, string source, int[] pages, int dpi, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "OrbitConvert.App.exe");
        if (!File.Exists(executable)) { throw new PdfRejected(PdfError.ToolsUnavailable); }
        var id = Guid.NewGuid().ToString("N");
        var response = "response-" + id + ".json";
        var request = Path.Combine(workspace, "request-" + id + ".json");
        // Candidate/source files are flat, operation-owned workspace children.
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new PdfWorkerRequest(action, Path.GetFileName(source), response, pages, dpi)), Utf8, token).ConfigureAwait(false);
        var run = await RunAsync(executable, ["--pdf-worker", request], workspace, token).ConfigureAwait(false);
        CheckOutcome(run);
        var path = Path.Combine(workspace, response);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) { throw new PdfRejected(PdfError.InvalidPdf); }
        var result = JsonSerializer.Deserialize<PdfWorkerResponse>(await File.ReadAllTextAsync(path, Utf8, token).ConfigureAwait(false)) ?? throw new PdfRejected(PdfError.InvalidPdf);
        if (result.Error != PdfError.None) { throw new PdfRejected(result.Error); }
        if (run.ExitCode != 0) { throw new PdfRejected(PdfError.InvalidPdf); }
        return result;
    }
    private async Task<string> EngineAsync(string workspace, CancellationToken token)
    {
        var engine = locator.Locate() ?? throw new PdfRejected(PdfError.ToolsUnavailable);
        var result = await RunAsync(engine, ["--version"], workspace, token).ConfigureAwait(false);
        Check(result);
        if (result.StandardOutput.Split('\n')[0].Trim() != "qpdf version " + QpdfLocator.Version) { throw new PdfRejected(PdfError.ToolsIncompatible); }
        return engine;
    }
    private async Task<ProcessRunResult> RunAsync(string executable, IReadOnlyList<string> args, string workspace, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var overBudget = false;
        var observing = WatchAsync();
        try
        {
            var result = await runner.RunAsync(new(executable, args, PdfLimits.StageTimeout, MaximumJobMemoryBytes: PdfLimits.JobMemoryBytes), stop.Token).ConfigureAwait(false);
            if (!result.TerminationConfirmed) { throw new PdfRejected(PdfError.ProcessTerminationFailed); }
            if (overBudget) { throw new PdfRejected(PdfError.TooLarge); }
            return result;
        }
        finally { stop.Cancel(); await observing.ConfigureAwait(false); }
        async Task WatchAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    long length = 0; var count = 0;
                    foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (++count > 1200 || (length += new FileInfo(file).Length) > PdfLimits.TemporaryBytes) { overBudget = true; stop.Cancel(); break; }
                    }
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { overBudget = true; stop.Cancel(); }
            catch (UnauthorizedAccessException) { overBudget = true; stop.Cancel(); }
        }
    }
    private static int[][] Groups(PdfOperation operation, int[] pages, int count)
    {
        if (operation == PdfOperation.Boundaries)
        {
            if (pages.Length == 0 || pages[^1] >= count || !pages.SequenceEqual(pages.Order())) { throw new PdfRejected(PdfError.InvalidSelection); }
            var groups = new List<int[]>(); var first = 1;
            foreach (var end in pages.Append(count)) { groups.Add(Enumerable.Range(first, end - first + 1).ToArray()); first = end + 1; }
            return groups.ToArray();
        }
        return operation is PdfOperation.IndividualPages or PdfOperation.Render ? pages.Select(x => new[] { x }).ToArray() : [pages];
    }
    private static FileStream OpenSource(string path)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > PdfLimits.InputBytes) { input.Dispose(); throw new PdfRejected(PdfError.TooLarge); }
        return input;
    }
    private static async Task<string> CopyAsync(FileStream source, string workspace, int index, CancellationToken token)
    {
        var path = Path.Combine(workspace, $"input-{index:D4}.pdf");
        await StageAsync(source, path, token).ConfigureAwait(false);
        return path;
    }
    private static async Task StageAsync(string source, string output, CancellationToken token)
    { using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); await StageAsync(input, output, token).ConfigureAwait(false); }
    private static async Task StageAsync(Stream input, string output, CancellationToken token)
    { using var staged = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(staged, token).ConfigureAwait(false); staged.Flush(true); }
    private static string NewWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), ".orbitconvert-pdf-" + Guid.NewGuid().ToString("N"));
        if (!CreateDirectory(path, IntPtr.Zero)) { throw new IOException("Could not reserve workspace.", new Win32Exception(Marshal.GetLastWin32Error())); }
        return path;
    }
    private static bool Cleanup(string path, bool directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { if (directory) { Directory.Delete(path, true); } else { File.Delete(path); } return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return !File.Exists(path) && !Directory.Exists(path);
    }
    private static void CheckOutcome(ProcessRunResult result)
    {
        var error = !result.TerminationConfirmed ? PdfError.ProcessTerminationFailed : result.Outcome switch
        { ProcessOutcome.Cancelled => PdfError.Cancelled, ProcessOutcome.TimedOut => PdfError.TimedOut, ProcessOutcome.StartFailed => PdfError.ToolsUnavailable, _ => PdfError.None };
        if (error != PdfError.None) { throw new PdfRejected(error); }
    }
    private static void Check(ProcessRunResult result) { CheckOutcome(result); if (result.ExitCode != 0) { throw new PdfRejected(PdfError.InvalidPdf); } }
    private static bool Expected(Exception error) => error is PdfRejected or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException or OverflowException or OperationCanceledException or MagickException;
    private static PdfError Error(Exception error) => error switch { PdfRejected rejected => rejected.Error, OperationCanceledException => PdfError.Cancelled, JsonException or MagickException => PdfError.InvalidPdf, _ => PdfError.OutputFailure };
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
