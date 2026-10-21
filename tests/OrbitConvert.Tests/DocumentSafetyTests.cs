using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class DocumentSafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "documents-safety-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(_directory, name);
    private async Task<string> Source(string text = DocumentConversionTests.Markdown)
    { var path = PathFor("عربي English.md"); await File.WriteAllTextAsync(path, text); return path; }
    private static DocumentConversionService Service(IProcessRunner? runner = null) => new(new PandocLocator(), runner ?? new ProcessRunner());

    [PandocFact]
    public async Task EmbeddedPng_IsPreservedThroughOfficeAndMarkdown()
    {
        var bytes = ImageFixtures.Png();
        var source = await Source("# صورة\n\nنص عربي\n\n![صورة](data:image/png;base64," + Convert.ToBase64String(bytes) + ")\n");
        var docx = PathFor("embedded.docx");
        var first = await Service().ConvertAsync(source, docx, DocumentFormat.DOCX);
        Assert.True(first.IsSuccess, first.Error.ToString());
        using (var zip = ZipFile.OpenRead(docx))
        {
            var entry = Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
            using var buffer = new MemoryStream(); using var input = entry.Open(); input.CopyTo(buffer);
            Assert.Equal(bytes, buffer.ToArray());
        }
        var odt = PathFor("embedded.odt");
        var second = await Service().ConvertAsync(docx, odt, DocumentFormat.ODT);
        Assert.True(second.IsSuccess, second.Error.ToString());
        var md = PathFor("embedded.md");
        Assert.True((await Service().ConvertAsync(docx, md, DocumentFormat.Markdown)).IsSuccess);
        Assert.Contains("data:image/png;base64,", await File.ReadAllTextAsync(md));
    }
    [PandocTheory]
    [InlineData("عربي فقط، ١٢٣، مُختلَط")]
    [InlineData("English only 123 punctuation.")]
    [InlineData("العربية English مع ١٢٣ and 456.")]
    public async Task ActualDocx_RetainsLanguageTextAndDirection(string text)
    {
        var path = PathFor("language.docx");
        Assert.True((await Service().ConvertAsync(await Source("# " + text), path, DocumentFormat.DOCX)).IsSuccess);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync();
        Assert.Contains(DocumentAst.IsRtl(text) ? "w:bidi w:val=\"1\"" : "w:bidi w:val=\"0\"", xml);
        Assert.True((await Service().InspectAsync(path)).IsSuccess);
    }
    [PandocFact]
    public async Task DestinationRaceAndInvalidEncoderOutput_DoNotFinalize()
    {
        var source = await Source(); var output = PathFor("race.docx");
        var collision = await Service(new MutationRunner(_ => File.WriteAllBytes(output, [4, 5, 6]))).ConvertAsync(source, output, DocumentFormat.DOCX);
        Assert.Equal(DocumentError.DestinationExists, collision.Error);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(output));
        var invalid = PathFor("invalid.docx");
        Assert.Equal(DocumentError.OutputValidationFailed, (await Service(new MutationRunner(candidate => File.WriteAllBytes(candidate, [1, 2, 3]))).ConvertAsync(source, invalid, DocumentFormat.DOCX)).Error);
        Assert.False(File.Exists(invalid));
        Assert.Empty(Directory.GetDirectories(_directory, ".orbitconvert-doc-*"));
    }
    [PandocTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCancellationAndTimeout_ConfirmOwnedProcessExit(bool timeout)
    {
        using var cancel = new CancellationTokenSource();
        using var runner = new StoppingRunner(cancel, timeout);
        var source = await Source("# عنوان\n\n```text\n" + new string('x', 16 * 1024 * 1024) + "\n```\n");
        var original = await File.ReadAllBytesAsync(source);
        var output = PathFor("stopped.docx");
        var result = await Service(runner).ConvertAsync(source, output, DocumentFormat.DOCX, token: cancel.Token);
        Assert.Equal(timeout ? DocumentError.TimedOut : DocumentError.Cancelled, result.Error);
        Assert.NotNull(runner.Owned);
        Assert.True(runner.Owned.HasExited);
        Assert.False(result.CleanupFailed);
        Assert.False(File.Exists(output));
        Assert.NotNull(runner.Workspace);
        Assert.False(Directory.Exists(runner.Workspace));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*.tmp"));
        Assert.Empty(Directory.GetDirectories(_directory, ".orbitconvert-doc-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }
    [PandocTheory]
    [InlineData(ProcessOutcome.StartFailed, true, DocumentError.ToolsUnavailable)]
    [InlineData(ProcessOutcome.Cancelled, false, DocumentError.ProcessTerminationFailed)]
    public async Task FailedProcess_NeverClaimsSuccessOrConfirmedCleanup(ProcessOutcome outcome, bool confirmed, DocumentError expected)
    {
        var result = await Service(new FailureRunner(outcome, confirmed)).ConvertAsync(await Source(), PathFor("failure.docx"), DocumentFormat.DOCX);
        Assert.Equal(expected, result.Error);
        Assert.Equal(!confirmed, result.CleanupFailed);
        Assert.Empty(Directory.GetDirectories(_directory, ".orbitconvert-doc-*"));
    }
    [Fact]
    public async Task MissingToolFailsClearly_AndRelativeOverridesAreRejected()
    {
        var locator = new PandocLocator(_directory, _ => "relative");
        Assert.Null(locator.Locate());
        var result = await new DocumentConversionService(locator, new ProcessRunner()).InspectAsync(await Source());
        Assert.Equal(DocumentError.ToolsUnavailable, result.Error);
    }
    [PandocFact]
    public async Task IncompatibleVersionFailsBeforeConversion()
    {
        var result = await Service(new VersionRunner()).ConvertAsync(await Source(), PathFor("incompatible.docx"), DocumentFormat.DOCX);
        Assert.Equal(DocumentError.ToolsIncompatible, result.Error);
    }
    [PandocFact]
    public async Task ExternalImage_IsRejectedWithoutAnyNetworkConnection()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var accepted = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var source = await Source($"# عنوان\n\n![external](http://127.0.0.1:{port}/private.png)\n");
        var result = await Service().ConvertAsync(source, PathFor("network.docx"), DocumentFormat.DOCX);
        Assert.False(result.IsSuccess);
        Assert.False(accepted.IsCompletedSuccessfully);
        stop.Cancel();
        try { using var unexpected = await accepted; Assert.Fail("Document reader attempted a network connection."); }
        catch (OperationCanceledException) { }
    }
    [Fact]
    public void PackageLimitsExternalRelationsDtdAndCrc_AreRejected()
    {
        foreach (var xml in new[] { "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///C:/Windows/win.ini'>]><root>&secret;</root>",
            "<Relationships><Relationship TargetMode='External' Type='image' Target='file:///C:/Windows/win.ini'/></Relationships>" })
        {
            var path = PathFor(Guid.NewGuid() + ".docx");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            { using var writer = new StreamWriter(zip.CreateEntry("word/test.rels").Open()); writer.Write(xml); }
            Assert.ThrowsAny<Exception>(() => DocumentGuard.Check(path));
        }
        var crc = PathFor("crc.docx");
        using (var zip = ZipFile.Open(crc, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("test.txt", CompressionLevel.NoCompression).Open()); writer.Write("original payload"); }
        var bytes = File.ReadAllBytes(crc);
        var marker = Encoding.UTF8.GetBytes("original payload"); var index = bytes.AsSpan().IndexOf(marker); Assert.True(index >= 0);
        bytes[index] ^= 1; File.WriteAllBytes(crc, bytes);
        Assert.Equal(DocumentError.InvalidDocument, Assert.Throws<DocumentRejected>(() => DocumentGuard.Check(crc)).Error);
        var large = PathFor("large.docx");
        using (var zip = ZipFile.Open(large, ZipArchiveMode.Create))
        { using var entry = zip.CreateEntry("bomb.txt").Open(); var buffer = new byte[1024 * 1024]; for (var i = 0; i < 26; i++) { entry.Write(buffer); } }
        Assert.Equal(DocumentError.TooLarge, Assert.Throws<DocumentRejected>(() => DocumentGuard.Check(large)).Error);
    }
    private sealed class VersionRunner : IProcessRunner
    { public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => Task.FromResult(new ProcessRunResult(ProcessOutcome.Exited, 0, "pandoc 0.0", "")); }
    private sealed class FailureRunner(ProcessOutcome outcome, bool confirmed) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => request.Arguments.Contains("docx")
            ? Task.FromResult(new ProcessRunResult(outcome, 1, "", "failure", confirmed)) : new ProcessRunner().RunAsync(request, token);
    }
    private sealed class MutationRunner(Action<string> mutate) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (request.Arguments.Contains("docx") && result.Succeeded)
            { var args = request.Arguments.ToList(); mutate(args[args.IndexOf("--output") + 1]); }
            return result;
        }
    }
    private sealed class StoppingRunner(CancellationTokenSource cancellation, bool timeout) : IProcessRunner, IDisposable
    {
        public Process? Owned { get; private set; }
        public string? Workspace { get; private set; }
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var dataDirectory = request.Arguments.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.Ordinal));
            if (dataDirectory is not null) { Workspace ??= dataDirectory["--data-dir=".Length..]; }
            if (!request.Arguments.Contains("docx")) { return new ProcessRunner().RunAsync(request, token); }
            var runner = new ProcessRunner(process =>
            {
                var started = process.Start();
                if (started) { Owned = Process.GetProcessById(process.Id); Owned.EnableRaisingEvents = true; if (!timeout) { cancellation.CancelAfter(5); } }
                return started;
            });
            return runner.RunAsync(timeout ? request with { Timeout = TimeSpan.FromMilliseconds(1) } : request, token);
        }
        public void Dispose() => Owned?.Dispose();
    }
}
