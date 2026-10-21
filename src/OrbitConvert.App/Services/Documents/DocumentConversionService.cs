using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Documents;

public interface IDocumentConversionService
{
    Task<DocumentInspectionResult> InspectAsync(string path, CancellationToken token = default);
    Task<DocumentConversionResult> ConvertAsync(string source, string destination, DocumentFormat target, IProgress<DocumentStage>? progress = null, CancellationToken token = default);
}

public sealed class DocumentConversionService(PandocLocator locator, IProcessRunner runner) : IDocumentConversionService
{
    public DocumentConversionService() : this(new PandocLocator(), new ProcessRunner()) { }
    public Task<DocumentInspectionResult> InspectAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        string? workspace = null;
        var result = new DocumentInspectionResult(null, DocumentError.InvalidDocument);
        try
        {
            token.ThrowIfCancellationRequested();
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            workspace = NewWorkspace(Path.GetTempPath());
            var source = CopyInput(original, path, workspace);
            var format = DocumentGuard.Check(source);
            var engine = await EngineAsync(token).ConfigureAwait(false);
            var ast = await ReadAsync(engine, source, format, workspace, token).ConfigureAwait(false);
            result = new(DocumentAst.Describe(ast, format, original.Length), DocumentError.None);
        }
        catch (Exception error) when (Expected(error)) { result = new(null, Error(error)); }
        finally { if (workspace is not null && !Cleanup(workspace)) { result = result with { CleanupFailed = true }; } }
        return result.Error == DocumentError.ProcessTerminationFailed ? result with { CleanupFailed = true } : result;
    }, CancellationToken.None);

    public Task<DocumentConversionResult> ConvertAsync(string source, string destination, DocumentFormat target, IProgress<DocumentStage>? progress = null, CancellationToken token = default) =>
        Task.Run(() => ConvertCoreAsync(source, destination, target, progress, token), CancellationToken.None);

    private async Task<DocumentConversionResult> ConvertCoreAsync(string source, string destination, DocumentFormat target, IProgress<DocumentStage>? progress, CancellationToken token)
    {
        string? workspace = null;
        string? ownedOutput = null;
        var checkingOutput = false;
        var finalizing = false;
        var result = new DocumentConversionResult(DocumentError.ConversionFailed);
        try
        {
            token.ThrowIfCancellationRequested();
            var inputPath = Path.GetFullPath(source);
            var outputPath = Path.GetFullPath(destination);
            if (inputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) { return new(DocumentError.SamePath); }
            if (File.Exists(outputPath) || Directory.Exists(outputPath)) { return new(DocumentError.DestinationExists); }
            if (!Enum.IsDefined(target) || !Path.GetExtension(outputPath).Equals(target.Extension(), StringComparison.OrdinalIgnoreCase)) { return new(DocumentError.InvalidDestination); }
            using var original = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Keep parser work/ZIP rewrites out of user sync folders. Only the final owned .tmp is staged beside the destination.
            workspace = NewWorkspace(Path.GetTempPath());
            var copy = CopyInput(original, inputPath, workspace);
            progress?.Report(DocumentStage.Inspecting);
            var format = DocumentGuard.Check(copy);
            if (!DocumentFormats.Outputs(format).Contains(target)) { throw new DocumentRejected(DocumentError.UnsupportedDirection); }
            var engine = await EngineAsync(token).ConfigureAwait(false);
            var ast = await ReadAsync(engine, copy, format, workspace, token).ConfigureAwait(false);
            var inspected = DocumentAst.Describe(ast, format, original.Length);
            if (target == DocumentFormat.HTML) { DocumentDirection.ApplyHtmlDirections(ast["blocks"]); }
            var intermediate = Path.Combine(workspace, "safe.json");
            await File.WriteAllTextAsync(intermediate, ast.ToJsonString(), DocumentGuard.Utf8, token).ConfigureAwait(false);
            var candidate = Path.Combine(workspace, "output" + target.Extension());
            progress?.Report(DocumentStage.Converting);
            var args = Arguments(workspace).Concat(new[] { "--from", "json", "--to", target.Writer(), "--wrap=none", "--standalone", "--output", candidate, intermediate }).ToArray();
            Check(await runner.RunAsync(new(engine, args, DocumentFormats.Timeout), token).ConfigureAwait(false));
            checkingOutput = true;
            if (target == DocumentFormat.DOCX) { DocumentDirection.ApplyDocx(candidate); }
            if (target == DocumentFormat.ODT) { DocumentDirection.ApplyOdt(candidate); }
            progress?.Report(DocumentStage.Verifying);
            if (!File.Exists(candidate) || new FileInfo(candidate).Length > DocumentFormats.MaximumBytes) { throw new DocumentRejected(DocumentError.OutputValidationFailed); }
            ValidateHtml(candidate, target);
            var actualFormat = DocumentGuard.Check(candidate);
            if (actualFormat != target) { throw new DocumentRejected(DocumentError.OutputValidationFailed); }
            var verified = await ReadAsync(engine, candidate, target, workspace, token).ConfigureAwait(false);
            var output = DocumentAst.Describe(verified, target, new FileInfo(candidate).Length);
            if (!DocumentAst.PreservesText(inspected.Text, output.Text) || target is DocumentFormat.DOCX or DocumentFormat.ODT &&
                (output.Headings < inspected.Headings || output.Tables < inspected.Tables || output.Lists < inspected.Lists || output.Images < inspected.Images))
            { throw new DocumentRejected(DocumentError.OutputValidationFailed); }
            token.ThrowIfCancellationRequested();
            finalizing = true;
            var stage = Path.Combine(Path.GetDirectoryName(outputPath)!, ".orbitconvert-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var staged = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownedOutput = stage;
                using var verifiedInput = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                await verifiedInput.CopyToAsync(staged, token).ConfigureAwait(false);
                staged.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(stage, outputPath, overwrite: false);
            ownedOutput = null;
            result = new(DocumentError.None, outputPath);
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { result = new(DocumentError.DestinationExists); }
        catch (Exception error) when (Expected(error))
        {
            var code = Error(error);
            if (checkingOutput && code is DocumentError.InvalidDocument or DocumentError.UnreadableInput or DocumentError.UnsupportedFormat or DocumentError.UnsafeContent or DocumentError.TooLarge)
            { code = finalizing ? DocumentError.OutputFailure : DocumentError.OutputValidationFailed; }
            result = new(code);
        }
        finally
        {
            if (workspace is not null && !Cleanup(workspace)) { result = result with { CleanupFailed = true }; }
            if (ownedOutput is not null && !CleanupFile(ownedOutput)) { result = result with { CleanupFailed = true }; }
        }
        return result.Error == DocumentError.ProcessTerminationFailed ? result with { CleanupFailed = true } : result;
    }
    private async Task<string> EngineAsync(CancellationToken token)
    {
        var engine = locator.Locate() ?? throw new DocumentRejected(DocumentError.ToolsUnavailable);
        var version = await runner.RunAsync(new(engine, ["--version"], TimeSpan.FromSeconds(15)), token).ConfigureAwait(false);
        Check(version);
        if (version.StandardOutput.Split('\n')[0].Trim() != "pandoc " + PandocLocator.Version) { throw new DocumentRejected(DocumentError.ToolsIncompatible); }
        return engine;
    }
    private async Task<JsonObject> ReadAsync(string engine, string input, DocumentFormat format, string workspace, CancellationToken token)
    {
        JsonObject ast;
        if (format == DocumentFormat.TXT) { ast = DocumentAst.LiteralText(await File.ReadAllTextAsync(input, DocumentGuard.Utf8, token).ConfigureAwait(false)); }
        else
        {
            var json = Path.Combine(workspace, "read-" + Guid.NewGuid().ToString("N") + ".json");
            var args = Arguments(workspace).Concat(new[] { "--from", format.Reader(), "--to", "json", "--output", json, input }).ToList();
            if (format is DocumentFormat.DOCX or DocumentFormat.ODT or DocumentFormat.RTF) { args.Add("--extract-media=" + Path.Combine(workspace, "media")); }
            Check(await runner.RunAsync(new(engine, args, DocumentFormats.Timeout), token).ConfigureAwait(false));
            ast = DocumentAst.Read(json);
        }
        DocumentAst.Sanitize(ast, workspace);
        return ast;
    }
    private static string[] Arguments(string workspace) => ["--sandbox", "--fail-if-warnings", "--data-dir=" + workspace];
    private static void Check(ProcessRunResult result)
    {
        var error = !result.TerminationConfirmed ? DocumentError.ProcessTerminationFailed : result.Outcome switch
        {
            ProcessOutcome.Cancelled => DocumentError.Cancelled,
            ProcessOutcome.TimedOut => DocumentError.TimedOut,
            ProcessOutcome.StartFailed => DocumentError.ToolsUnavailable,
            _ => result.ExitCode == 0 ? DocumentError.None : DocumentError.ConversionFailed,
        };
        if (error != DocumentError.None) { throw new DocumentRejected(error); }
    }
    private static string NewWorkspace(string parent)
    {
        var path = Path.Combine(parent, ".orbitconvert-doc-" + Guid.NewGuid().ToString("N"));
        // The GUID-named directory is operation-owned; never adopt an existing path.
        if (Directory.Exists(path) || File.Exists(path)) { throw new IOException("Workspace collision."); }
        Directory.CreateDirectory(path);
        return path;
    }
    private static string CopyInput(FileStream input, string name, string workspace)
    {
        if (input.Length > DocumentFormats.MaximumBytes) { throw new DocumentRejected(DocumentError.TooLarge); }
        var copy = Path.Combine(workspace, "input" + Path.GetExtension(name));
        using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        return copy;
    }
    private static bool Cleanup(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return !Directory.Exists(path);
    }
    private static bool CleanupFile(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return !File.Exists(path);
    }
    private static void ValidateHtml(string path, DocumentFormat format)
    {
        if (format != DocumentFormat.HTML) { return; }
        var text = File.ReadAllText(path, DocumentGuard.Utf8);
        if (!text.Contains("<html", StringComparison.OrdinalIgnoreCase) || !text.Contains("charset=\"utf-8\"", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"<\s*(script|iframe|object|embed|form|base|link)\b|\bon\w+\s*=|url\s*\(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        { throw new DocumentRejected(DocumentError.OutputValidationFailed); }
    }
    private static bool Expected(Exception error) => error is DocumentRejected or InvalidDataException or IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException or XmlException or JsonException or InvalidOperationException or FormatException or OperationCanceledException or OverflowException;
    private static DocumentError Error(Exception error) => error switch
    {
        DocumentRejected rejected => rejected.Error,
        OperationCanceledException => DocumentError.Cancelled,
        InvalidDataException or XmlException or JsonException or InvalidOperationException or FormatException => DocumentError.InvalidDocument,
        _ => DocumentError.UnreadableInput,
    };
}
