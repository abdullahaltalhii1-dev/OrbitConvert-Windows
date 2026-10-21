using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.Services.Media;
using Xunit;

namespace OrbitConvert.Tests;

public sealed class PandocFactAttribute : FactAttribute
{
    public PandocFactAttribute() { if (new PandocLocator().Locate() is null && Environment.GetEnvironmentVariable("ORBITCONVERT_REQUIRE_PANDOC") != "1") { Skip = "Pinned local Pandoc unavailable."; } }
}
public sealed class PandocTheoryAttribute : TheoryAttribute
{
    public PandocTheoryAttribute() { if (new PandocLocator().Locate() is null && Environment.GetEnvironmentVariable("ORBITCONVERT_REQUIRE_PANDOC") != "1") { Skip = "Pinned local Pandoc unavailable."; } }
}

public sealed class DocumentConversionTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "مستندات " + Guid.NewGuid().ToString("N"))).FullName;
    internal const string Markdown = "# عنوان عربي English\n\nنص عربي **عريض** و*مائل* مع ١٢٣، ورقم 42.\n\nEnglish paragraph [link](https://example.com).\n\n| عربي | English |\n| --- | --- |\n| قيمة | Value |\n\n- بند عربي\n- English item\n\n> اقتباس\n\n`inline code`\n\n```text\ncode block\n```\n\n---\n";
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(_directory, name);
    private static DocumentConversionService Service(IProcessRunner? runner = null) => new(new PandocLocator(), runner ?? new ProcessRunner());
    private async Task<string> Fixture(DocumentFormat format)
    {
        var markdown = PathFor("أصل & English.md");
        await File.WriteAllTextAsync(markdown, Markdown, new UTF8Encoding(false));
        if (format == DocumentFormat.Markdown) { return markdown; }
        var path = PathFor("عينة" + format.Extension());
        if (format == DocumentFormat.TXT) { await File.WriteAllTextAsync(path, "عربي English ١٢٣\n\n# Literal *text*", DocumentGuard.Utf8); return path; }
        var engine = new PandocLocator().Locate();
        Assert.NotNull(engine);
        var result = await new ProcessRunner().RunAsync(new(engine, ["--sandbox", "--from=markdown", "--to=" + format.Writer(), "--standalone", "--output", path, markdown], TimeSpan.FromSeconds(20)), default);
        Assert.True(result.Succeeded, result.StandardError);
        return path;
    }
    public static IEnumerable<object[]> Matrix => Enum.GetValues<DocumentFormat>().SelectMany(source => DocumentFormats.Outputs(source).Select(target => new object[] { source, target }));
    [PandocTheory]
    [MemberData(nameof(Matrix))]
    public async Task VerifiedMatrix_PreservesUnicodeOriginalsAndMeaningfulOutput(DocumentFormat input, DocumentFormat output)
    {
        var source = await Fixture(input);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var destination = PathFor("ناتج جديد" + output.Extension());
        var stages = new List<DocumentStage>();
        var result = await Service().ConvertAsync(source, destination, output, new ImmediateProgress(stages.Add));
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.False(result.CleanupFailed);
        var inspected = await Service().InspectAsync(destination);
        Assert.True(inspected.IsSuccess, inspected.Error.ToString());
        Assert.Contains("عربي", inspected.Inspection!.Text);
        Assert.Contains("English", inspected.Inspection.Text);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Equal(new[] { DocumentStage.Inspecting, DocumentStage.Converting, DocumentStage.Verifying }, stages);
        Assert.Empty(Directory.GetDirectories(_directory, ".orbitconvert-doc-*"));
        if (output is DocumentFormat.DOCX or DocumentFormat.ODT)
        {
            Assert.True(inspected.Inspection.Tables > 0);
            Assert.True(inspected.Inspection.Headings > 0);
            Assert.True(inspected.Inspection.Lists > 0);
        }
        if (output == DocumentFormat.DOCX)
        {
            using var zip = ZipFile.OpenRead(destination);
            using var xml = zip.GetEntry("word/document.xml")!.Open();
            var document = XDocument.Load(xml);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            Assert.Contains(document.Descendants(w + "p"), p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)).StartsWith("عنوان") && (string?)p.Element(w + "pPr")?.Element(w + "bidi")?.Attribute(w + "val") == "1");
            Assert.NotEmpty(document.Descendants(w + "rtl"));
        }
        if (output == DocumentFormat.ODT)
        {
            using var zip = ZipFile.OpenRead(destination); using var xml = zip.GetEntry("content.xml")!.Open();
            var document = XDocument.Load(xml);
            XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
            Assert.Contains(document.Descendants(style + "paragraph-properties"), p => (string?)p.Attribute(style + "writing-mode") == "rl-tb");
        }
    }
    [PandocFact]
    public async Task TextIsLiteral_AndHtmlHasPerBlockDirections()
    {
        var source = await Fixture(DocumentFormat.TXT);
        var md = PathFor("literal.md");
        Assert.True((await Service().ConvertAsync(source, md, DocumentFormat.Markdown)).IsSuccess);
        Assert.Contains("\\#", await File.ReadAllTextAsync(md));
        var html = PathFor("mixed.html");
        var result = await Service().ConvertAsync(await Fixture(DocumentFormat.Markdown), html, DocumentFormat.HTML);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var text = await File.ReadAllTextAsync(html);
        Assert.Contains("dir=\"rtl\"", text);
        Assert.Contains("dir=\"ltr\"", text);
        Assert.Contains("lang=\"ar\"", text);
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
    }
    [PandocTheory]
    [InlineData("![outside](file:///C:/Windows/win.ini)")]
    [InlineData("![outside](https://127.0.0.1:9/secret.png)")]
    [InlineData("[execute](javascript:alert(1))")]
    [InlineData("[local](file:///C:/Windows/win.ini)")]
    public async Task ResourcesAndActiveLinks_AreRejected(string content)
    {
        var path = PathFor("unsafe.md");
        await File.WriteAllTextAsync(path, "ordinary text\n\n" + content);
        var output = PathFor("unsafe.docx");
        var result = await Service().ConvertAsync(path, output, DocumentFormat.DOCX);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(_directory, ".orbitconvert-doc-*"));
    }
    [PandocFact]
    public async Task Safety_SamePathCollisionUnsupportedAndPrecancel()
    {
        var source = await Fixture(DocumentFormat.Markdown);
        Assert.Equal(DocumentError.SamePath, (await Service().ConvertAsync(source, source, DocumentFormat.Markdown)).Error);
        var output = PathFor("existing.docx");
        await File.WriteAllBytesAsync(output, [1, 2, 3]);
        Assert.Equal(DocumentError.DestinationExists, (await Service().ConvertAsync(source, output, DocumentFormat.DOCX)).Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(DocumentError.UnsupportedDirection, (await Service().ConvertAsync(source, PathFor("unsupported.odt"), DocumentFormat.ODT)).Error);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(DocumentError.Cancelled, (await Service().ConvertAsync(source, PathFor("cancel.docx"), DocumentFormat.DOCX, token: cancel.Token)).Error);
    }
    [Theory]
    [InlineData("../escape.xml")]
    [InlineData("word/vbaProject.bin")]
    public async Task UnsafePackagesRejectedBeforeEngine(string entry)
    {
        var path = PathFor("unsafe.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        { using var stream = new StreamWriter(zip.CreateEntry(entry).Open()); stream.Write("<root/>"); }
        Assert.Equal(DocumentError.UnsafeContent, (await Service().InspectAsync(path)).Error);
    }
    [Fact]
    public async Task MalformedAndOversizeInputs_AreBounded()
    {
        var path = PathFor("bad.docx");
        await File.WriteAllBytesAsync(path, [0x50, 0x4b, 0, 1]);
        Assert.False((await Service().InspectAsync(path)).IsSuccess);
        using (var stream = File.Create(path)) { stream.SetLength(DocumentFormats.MaximumBytes + 1); }
        Assert.Equal(DocumentError.TooLarge, (await Service().InspectAsync(path)).Error);
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0]);
        Assert.False((await Service().InspectAsync(path)).IsSuccess);
    }
    private sealed class ImmediateProgress(Action<DocumentStage> report) : IProgress<DocumentStage> { public void Report(DocumentStage value) => report(value); }
}
