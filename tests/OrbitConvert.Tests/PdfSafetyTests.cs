using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrbitConvert.App.Services.Media;
using OrbitConvert.App.Services.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace OrbitConvert.Tests;

public sealed class PdfSafetyTests(Xunit.Abstractions.ITestOutputHelper log) : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "pdf-safety-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private string PathFor(string name) => Path.Combine(_folder, name);
    private string Fixture(string name = "source.pdf", int count = 3, bool text = true)
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < count; i++)
        {
            var page = builder.AddPage(200, 300);
            if (text) { page.AddText("Page " + (i + 1), 12, new PdfPoint(20, 250), font); }
            else { page.AddPng(ImageFixtures.Png(), new PdfRectangle(20, 20, 80, 80)); }
        }
        var path = PathFor(name); File.WriteAllBytes(path, builder.Build()); return path;
    }
    [Theory]
    [InlineData("password")]
    [InlineData("")]
    public async Task EveryEncryptedInput_IsRejectedWithoutPasswordBypass(string password)
    {
        var source = Fixture(); var encrypted = PathFor("encrypted.pdf");
        var tool = new QpdfLocator().Locate(); Assert.NotNull(tool);
        var made = await new ProcessRunner().RunAsync(new(tool, ["--encrypt", password, "owner-test", "256", "--", source, encrypted], TimeSpan.FromSeconds(15)), CancellationToken.None);
        Assert.True(made.Succeeded, made.StandardError);
        var before = File.ReadAllBytes(encrypted);
        Assert.Equal(PdfError.Encrypted, (await new PdfService().InspectAsync(encrypted)).Error);
        Assert.Equal(before, File.ReadAllBytes(encrypted));
    }
    [Fact]
    public async Task EmptyImageOnlyText_MissingAndTruncatedPdf_AreExplicitFailures()
    {
        var service = new PdfService(new QpdfLocator(), new DiagnosticRunner(log));
        var noText = await service.ExecuteAsync(new(PdfOperation.Text, [Fixture(text: false)], PathFor("empty.txt")));
        Assert.Equal(PdfError.NoText, noText.Error); Assert.False(File.Exists(PathFor("empty.txt")));
        Assert.Equal(PdfError.UnreadableInput, (await service.InspectAsync(PathFor("missing.pdf"))).Error);
        var bytes = File.ReadAllBytes(Fixture("valid.pdf")); File.WriteAllBytes(PathFor("truncated.pdf"), bytes[..(bytes.Length / 2)]);
        Assert.False((await service.InspectAsync(PathFor("truncated.pdf"))).IsSuccess);
    }
    [Theory]
    [InlineData(PdfOperation.Extract, "0")]
    [InlineData(PdfOperation.Extract, "4")]
    [InlineData(PdfOperation.Extract, "1,1")]
    [InlineData(PdfOperation.Boundaries, "3")]
    [InlineData(PdfOperation.Boundaries, "2,1")]
    [InlineData(PdfOperation.Boundaries, "")]
    public async Task InvalidSelectionsNeverPublish(PdfOperation operation, string pages)
    {
        var source = Fixture(); var before = File.ReadAllBytes(source);
        var result = await new PdfService().ExecuteAsync(new(operation, [source], PathFor("out.pdf"), pages));
        Assert.Equal(PdfError.InvalidSelection, result.Error); Assert.False(File.Exists(PathFor("out.pdf")));
        Assert.Equal(before, File.ReadAllBytes(source));
    }
    [Theory]
    [InlineData(150, 417, 625)]
    [InlineData(300, 834, 1250)]
    public async Task RealRenderingAtBothDpi_PreservesDimensions(int dpi, uint width, uint height)
    {
        var result = await new PdfService().ExecuteAsync(new(PdfOperation.Render, [Fixture()], PathFor("image.png"), "2", dpi));
        Assert.True(result.IsSuccess, result.Error.ToString());
        using var image = new ImageMagick.MagickImage(result.Outputs!.Single());
        Assert.Equal(width, image.Width); Assert.Equal(height, image.Height);
    }
    [Fact]
    public async Task RotatedPage_RendersEffectiveOrientation_AndPreservesSelectedSequence()
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(200, 300); page.AddText("Rotation test", 15, new PdfPoint(20, 250), font);
        page.SetRotation(new UglyToad.PdfPig.Content.PageRotationDegrees(90));
        var source = PathFor("rotated.pdf"); File.WriteAllBytes(source, builder.Build());
        var service = new PdfService(); var inspection = await service.InspectAsync(source);
        Assert.True(inspection.IsSuccess, inspection.Error.ToString());
        Assert.Equal(300, inspection.Inspection!.Pages.Single().Width); Assert.Equal(200, inspection.Inspection.Pages.Single().Height);
        var result = await service.ExecuteAsync(new(PdfOperation.Render, [source], PathFor("rotated.png"), "1"));
        Assert.True(result.IsSuccess, result.Error.ToString());
        using var image = new ImageMagick.MagickImage(result.Outputs!.Single());
        Assert.Equal(625u, image.Width); Assert.Equal(417u, image.Height);
        Assert.True((await service.ExecuteAsync(new(PdfOperation.Reorder, [source], PathFor("copy.pdf"), "1"))).IsSuccess);
    }
    [Theory]
    [InlineData("action")]
    [InlineData("crop")]
    [InlineData("dimensions")]
    [InlineData("pixels")]
    public async Task RealQpdfAuthoredEdgeCases_AreVettedBeforePublishing(string kind)
    {
        var source = Fixture(count: 1); var json = PathFor("edit.json"); var changed = PathFor("changed.pdf");
        var tool = new QpdfLocator().Locate()!; var runner = new ProcessRunner();
        Assert.True((await runner.RunAsync(new(tool, ["--json-output=2", "--json-stream-data=none", source, json], TimeSpan.FromSeconds(15)), CancellationToken.None)).Succeeded);
        var document = JsonNode.Parse(File.ReadAllText(json))!;
        var objects = document["qpdf"]![1]!.AsObject().Select(x => x.Value?["value"]).Where(x => x is not null).ToArray();
        var page = objects.Single(x => (string?)x!["/Type"] == "/Page")!;
        if (kind == "action") { objects.Single(x => (string?)x!["/Type"] == "/Catalog")!["/OpenAction"] = JsonNode.Parse("{\"/S\":\"/JavaScript\",\"/JS\":\"u:/* inert generated rejection fixture */\"}"); }
        else if (kind == "crop") { page["/CropBox"] = JsonNode.Parse("[20,30,180,270]"); page["/Rotate"] = 90; }
        else { var side = kind == "dimensions" ? 3000 : 2300; page["/MediaBox"] = new JsonArray(0, 0, side, side); }
        File.WriteAllText(json, document.ToJsonString());
        var authored = await runner.RunAsync(new(tool, [source, "--update-from-json=" + json, changed], TimeSpan.FromSeconds(15)), CancellationToken.None);
        Assert.True(authored.Succeeded, authored.StandardError);
        var before = File.ReadAllBytes(changed); var service = new PdfService(); var inspection = await service.InspectAsync(changed);
        if (kind is "action" or "dimensions") { Assert.Equal(kind == "action" ? PdfError.UnsafeContent : PdfError.TooLarge, inspection.Error); }
        else
        {
            Assert.True(inspection.IsSuccess, inspection.Error.ToString());
            var output = PathFor("render.png"); var rendered = await service.ExecuteAsync(new(PdfOperation.Render, [changed], output, "1"));
            if (kind == "pixels") { Assert.Equal(PdfError.TooLarge, rendered.Error); Assert.False(File.Exists(output)); }
            else
            {
                Assert.Equal(240, inspection.Inspection!.Pages[0].Width); Assert.Equal(160, inspection.Inspection.Pages[0].Height);
                Assert.True(rendered.IsSuccess, rendered.Error.ToString());
                using var image = new ImageMagick.MagickImage(output); Assert.Equal(500u, image.Width); Assert.Equal(334u, image.Height);
            }
        }
        Assert.Equal(before, File.ReadAllBytes(changed));
    }
    [Fact]
    public async Task TooManyOutputsAndInvalidDpi_AreRejected()
    {
        var result = await new PdfService().ExecuteAsync(new(PdfOperation.IndividualPages, [Fixture(count: 101)], PathFor("many")));
        Assert.Equal(PdfError.TooLarge, result.Error); Assert.False(Directory.Exists(PathFor("many")));
        var invalid = await new PdfService().ExecuteAsync(new(PdfOperation.Render, [Fixture("dpi.pdf")], PathFor("dpi.png"), "1", 900));
        Assert.Equal(PdfError.InvalidSelection, invalid.Error); Assert.False(File.Exists(PathFor("dpi.png")));
    }
    [Fact]
    public async Task FinalizationRace_PreservesUserFile_AndCleansOwnedStaging()
    {
        var source = Fixture(); var output = PathFor("race.pdf");
        var service = new PdfService(new QpdfLocator(), new AfterWorker(() => File.WriteAllText(output, "user-owned")));
        var result = await service.ExecuteAsync(new(PdfOperation.Reorder, [source], output, "3,1"));
        Assert.Equal(PdfError.DestinationExists, result.Error); Assert.False(result.CleanupFailed);
        Assert.Equal("user-owned", File.ReadAllText(output)); Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    [Fact]
    public async Task MultiFilePublishRace_PreservesEntireExistingDirectory_WithoutPartialResults()
    {
        var source = Fixture(); var output = PathFor("racing-folder");
        var service = new PdfService(new QpdfLocator(), new AfterWorker(() =>
        {
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "user.txt"), "keep me");
        }));
        var result = await service.ExecuteAsync(new(PdfOperation.IndividualPages, [source], output));
        Assert.Equal(PdfError.DestinationExists, result.Error); Assert.False(result.CleanupFailed);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(output, "user.txt")));
        Assert.Single(Directory.GetFiles(output)); Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    [Fact]
    public async Task LockedAndEmptySources_AreRejectedWithoutContentChanges()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        { Assert.Equal(PdfError.UnreadableInput, (await new PdfService().InspectAsync(source)).Error); }
        var empty = PathFor("empty.pdf"); File.WriteAllBytes(empty, []);
        Assert.Equal(PdfError.InvalidPdf, (await new PdfService().InspectAsync(empty)).Error);
        Assert.Equal(before, File.ReadAllBytes(source));
    }
    [Theory]
    [InlineData(false, PdfError.ToolsUnavailable)]
    [InlineData(true, PdfError.ToolsIncompatible)]
    public async Task MissingOrWrongNativeAsset_IsExplicit_WithoutChangingSharedBuildAssets(bool wrongHash, PdfError expected)
    {
        var isolated = Directory.CreateDirectory(PathFor("isolated-app")).FullName;
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) { File.Copy(file, Path.Combine(isolated, Path.GetFileName(file))); }
        if (wrongHash)
        {
            var native = Directory.CreateDirectory(Path.Combine(isolated, "runtimes", "win-x64", "native")).FullName;
            File.WriteAllBytes(Path.Combine(native, "pdfium.dll"), [1, 2, 3]);
        }
        var source = Fixture(); var before = File.ReadAllBytes(source);
        var result = await new PdfService(new QpdfLocator(), new IsolatedWorker(isolated)).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("out.pdf")));
        Assert.Equal(expected, result.Error); Assert.False(result.CleanupFailed);
        Assert.False(File.Exists(PathFor("out.pdf"))); Assert.Equal(before, File.ReadAllBytes(source));
    }
    private sealed class IsolatedWorker(string directory) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) =>
            new ProcessRunner().RunAsync(request.Arguments[0] == "--pdf-worker" ? request with { ExecutablePath = Path.Combine(directory, "OrbitConvert.App.exe") } : request, token);
    }
    [Fact]
    public async Task ValidButWrongPageOutput_IsNotSuccess()
    {
        var source = Fixture(); var other = Fixture("other.pdf", 1);
        var runner = new MutationRunner(candidate => File.Copy(other, candidate, true));
        var result = await new PdfService(new QpdfLocator(), runner).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("wrong.pdf"), "1-2"));
        Assert.Equal(PdfError.OutputValidationFailed, result.Error); Assert.False(File.Exists(PathFor("wrong.pdf")));
    }
    [Theory]
    [InlineData(ProcessOutcome.Cancelled, false, PdfError.ProcessTerminationFailed)]
    [InlineData(ProcessOutcome.TimedOut, true, PdfError.TimedOut)]
    public async Task FailedNativeOutcomes_AreNotMisreported(ProcessOutcome outcome, bool confirmed, PdfError expected)
    {
        var source = Fixture(); var before = File.ReadAllBytes(source);
        var result = await new PdfService(new QpdfLocator(), new FailureRunner(outcome, confirmed)).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("failed.pdf")));
        Assert.Equal(expected, result.Error); Assert.Equal(!confirmed, result.CleanupFailed);
        Assert.False(File.Exists(PathFor("failed.pdf"))); Assert.Equal(before, File.ReadAllBytes(source));
    }
    [Fact]
    public async Task CancellationAfterNativeLoad_StopsActualChild_AndRemovesOwnedWorkspace()
    {
        var source = Fixture(count: 180); var before = File.ReadAllBytes(source);
        using var cancel = new CancellationTokenSource();
        using var runner = new ObservedWorker(cancel);
        var result = await new PdfService(new QpdfLocator(), runner).ExecuteAsync(new(PdfOperation.IndividualPages, [source], PathFor("cancelled")), token: cancel.Token);
        Assert.True(runner.NativeReady);
        Assert.Equal(PdfError.Cancelled, result.Error); Assert.False(result.CleanupFailed);
        Assert.True(runner.Worker!.HasExited); Assert.False(Directory.Exists(runner.Workspace));
        Assert.False(Directory.Exists(PathFor("cancelled"))); Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    [Fact]
    public async Task ActualWorkerDeadline_StopsChild_AndNeverPublishes()
    {
        using var runner = new ObservedWorker(null);
        var source = Fixture(count: 180); var before = File.ReadAllBytes(source);
        var result = await new PdfService(new QpdfLocator(), runner).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("timeout.pdf")));
        Assert.Equal(PdfError.TimedOut, result.Error); Assert.False(result.CleanupFailed);
        Assert.True(runner.Worker!.HasExited); Assert.False(Directory.Exists(runner.Workspace));
        Assert.False(File.Exists(PathFor("timeout.pdf"))); Assert.Equal(before, File.ReadAllBytes(source));
    }
    [Fact]
    public async Task UnexpectedOwnedWorkerExit_IsNotSuccess_AndCleansWorkspace()
    {
        var source = Fixture(count: 180); var before = File.ReadAllBytes(source);
        Process? owned = null; string? workspace = null; var ready = false;
        var runner = new DelegateRunner(async (request, token) =>
        {
            if (request.Arguments[0] != "--pdf-worker") { return await new ProcessRunner().RunAsync(request, token); }
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            var actual = new ProcessRunner(process => { var started = process.Start(); if (started) { owned = Process.GetProcessById(process.Id); } return started; });
            return await actual.RunAsync(request with { StandardOutputLine = line => { if (line == "pdf-worker-ready") { ready = true; owned!.Kill(entireProcessTree: true); } } }, token);
        });
        try
        {
            var result = await new PdfService(new QpdfLocator(), runner).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("crash.pdf")));
            Assert.True(ready); Assert.True(owned!.HasExited); Assert.False(result.IsSuccess); Assert.False(result.CleanupFailed);
            Assert.False(File.Exists(PathFor("crash.pdf"))); Assert.False(Directory.Exists(workspace)); Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }
    [Fact]
    public async Task LockedOwnedTemporaryFile_ReportsCleanupFailure_WithoutDeletingUnrelatedFiles()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source); var unrelated = PathFor("keep.txt"); File.WriteAllText(unrelated, "user data");
        FileStream? held = null; string? workspace = null;
        var runner = new DelegateRunner(async (request, token) =>
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (request.Arguments[0] == "--pdf-worker" && result.Succeeded)
            {
                workspace = Path.GetDirectoryName(request.Arguments[1]);
                held = new FileStream(Path.Combine(workspace!, "locked-test.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            return result;
        });
        try
        {
            var result = await new PdfService(new QpdfLocator(), runner).InspectAsync(source);
            Assert.True(result.IsSuccess); Assert.True(result.CleanupFailed); Assert.True(Directory.Exists(workspace));
            Assert.Equal(before, File.ReadAllBytes(source)); Assert.Equal("user data", File.ReadAllText(unrelated));
        }
        finally { held?.Dispose(); if (workspace is not null && Directory.Exists(workspace)) { Directory.Delete(workspace, true); } }
    }
    [Fact]
    public async Task ActualWorkspaceFileCountGuard_StopsAtBudget_AndCleansOwnedData()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source); string? workspace = null; var stopped = false;
        var runner = new DelegateRunner(async (request, token) =>
        {
            if (request.Arguments[0] == "--version") { return await new ProcessRunner().RunAsync(request, token); }
            workspace = Path.GetDirectoryName(request.Arguments[^1]);
            for (var i = 0; i < 1201; i++) { using var file = File.Create(Path.Combine(workspace!, $"budget-{i:D4}.tmp")); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); }
            catch (OperationCanceledException) { stopped = true; }
            return new ProcessRunResult(ProcessOutcome.Cancelled, -1, "", "");
        });
        var result = await new PdfService(new QpdfLocator(), runner).ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("budget.pdf")));
        Assert.True(stopped); Assert.Equal(PdfError.TooLarge, result.Error); Assert.False(result.CleanupFailed);
        Assert.False(Directory.Exists(workspace)); Assert.False(File.Exists(PathFor("budget.pdf"))); Assert.Equal(before, File.ReadAllBytes(source));
    }
    private sealed class DelegateRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run) : IProcessRunner
    { public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => run(request, token); }
    private sealed class ObservedWorker(CancellationTokenSource? cancel) : IProcessRunner, IDisposable
    {
        public bool NativeReady { get; private set; }
        public Process? Worker { get; private set; }
        public string? Workspace { get; private set; }
        public void Dispose() => Worker?.Dispose();
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            if (request.Arguments[0] != "--pdf-worker") { return await new ProcessRunner().RunAsync(request, token); }
            Workspace = Path.GetDirectoryName(request.Arguments[1]);
            Assert.Equal(512L * 1024 * 1024, request.MaximumJobMemoryBytes);
            var runner = new ProcessRunner(process => { var started = process.Start(); if (started) { Worker = Process.GetProcessById(process.Id); } return started; });
            var observed = request with
            {
                Timeout = cancel is null ? TimeSpan.Zero : request.Timeout,
                StandardOutputLine = line => { if (line == "pdf-worker-ready") { NativeReady = true; cancel?.Cancel(); } },
            };
            return await runner.RunAsync(observed, token);
        }
    }
    private sealed class AfterWorker(Action race) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (request.Arguments.Count == 2 && request.Arguments[0] == "--pdf-worker")
            {
                using var json = JsonDocument.Parse(File.ReadAllText(request.Arguments[1]));
                if (json.RootElement.GetProperty("Source").GetString()!.StartsWith("output-", StringComparison.Ordinal)) { race(); }
            }
            return result;
        }
    }
    private sealed class MutationRunner(Action<string> mutate) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (result.Succeeded && request.Arguments.Contains("--pages")) { mutate(request.Arguments[^1]); }
            return result;
        }
    }
    private sealed class FailureRunner(ProcessOutcome outcome, bool confirmed) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => request.Arguments[0] == "--version"
            ? new ProcessRunner().RunAsync(request, token) : Task.FromResult(new ProcessRunResult(outcome, 1, "", "test failure", confirmed));
    }
    private sealed class DiagnosticRunner(Xunit.Abstractions.ITestOutputHelper output) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (!result.Succeeded && !request.Arguments.Contains("--is-encrypted")) { output.WriteLine(string.Join(' ', request.Arguments) + "\n" + result.StandardError); }
            return result;
        }
    }
}
