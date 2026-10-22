using System.Security.Cryptography;
using ImageMagick;
using OrbitConvert.App.Services.Media;
using OrbitConvert.App.Services.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace OrbitConvert.Tests;

// No engine-availability skip: this phase's native coverage is mandatory locally and in CI.
public sealed class PdfEngineTests(Xunit.Abstractions.ITestOutputHelper log) : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "pdf-" + Guid.NewGuid().ToString("N"))).FullName;
    private string PathFor(string name) => Path.Combine(_folder, name);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private string Fixture(string name, params string[] labels)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.DocumentInformation.Title = "Generated QA";
        builder.DocumentInformation.Author = "OrbitConvert tests";
        for (var i = 0; i < labels.Length; i++)
        {
            var page = builder.AddPage(200 + 20 * i, 300 + 10 * i);
            page.AddText(labels[i], 16, new PdfPoint(20, 230), font);
        }
        var path = PathFor(name); File.WriteAllBytes(path, builder.Build()); return path;
    }
    [Fact]
    public async Task ActualInspectionTextAndRendering_UsePinnedNativeEngines()
    {
        var source = Fixture("عربي & English.pdf", "First page 123", "Second page 456");
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var service = Service();
        var inspected = await service.InspectAsync(source);
        Assert.True(inspected.IsSuccess, inspected.Error.ToString());
        Assert.Equal(2, inspected.Inspection!.Pages.Length);
        Assert.Equal("Generated QA", inspected.Inspection.Title);
        Assert.Equal("OrbitConvert tests", inspected.Inspection.Author);
        Assert.Equal(200, inspected.Inspection.Pages[0].Width);
        var text = await service.ExecuteAsync(new(PdfOperation.Text, [source], PathFor("نص.txt"), "2"));
        Assert.True(text.IsSuccess, text.Error.ToString());
        Assert.Contains("Second page 456", text.TextPreview); Assert.DoesNotContain("First", text.TextPreview);
        var rendered = await service.ExecuteAsync(new(PdfOperation.Render, [source], PathFor("صور"), "1-2", 150));
        Assert.True(rendered.IsSuccess, rendered.Error.ToString());
        Assert.Equal(2, rendered.Outputs!.Length);
        Assert.Equal(0ul, ResourceLimits.Disk);
        using (var verified = new MagickImage(rendered.Outputs[0]))
        { Assert.Throws<MagickPolicyErrorException>(() => verified.ToByteArray(MagickFormat.Gif)); }
        using var image = new MagickImage(rendered.Outputs[0]);
        Assert.Equal(MagickFormat.Png, image.Format);
        Assert.Equal(417u, image.Width); Assert.Equal(625u, image.Height);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }
    [Fact]
    public async Task ActualQpdfMergeReorderAndSplit_AreIndependentlyVerified()
    {
        var first = Fixture("أول.pdf", "A", "B", "C"); var second = Fixture("ثاني.pdf", "D");
        var service = Service();
        var merge = await service.ExecuteAsync(new(PdfOperation.Merge, [second, first], PathFor("merged.pdf")));
        Assert.True(merge.IsSuccess, merge.Error.ToString());
        using (var output = PdfDocument.Open(merge.Outputs!.Single()))
        { Assert.Equal(4, output.NumberOfPages); Assert.Equal("D", output.GetPage(1).Text); Assert.Equal("A", output.GetPage(2).Text); }
        var reorder = await service.ExecuteAsync(new(PdfOperation.Reorder, [first], PathFor("reordered.pdf"), "3,1"));
        Assert.True(reorder.IsSuccess, reorder.Error.ToString());
        using (var output = PdfDocument.Open(reorder.Outputs!.Single()))
        { Assert.Equal("C", output.GetPage(1).Text); Assert.Equal("A", output.GetPage(2).Text); }
        var split = await service.ExecuteAsync(new(PdfOperation.IndividualPages, [first], PathFor("pages"), "1-3"));
        Assert.True(split.IsSuccess, split.Error.ToString()); Assert.Equal(3, split.Outputs!.Length);
        var boundaries = await service.ExecuteAsync(new(PdfOperation.Boundaries, [first], PathFor("parts"), "1,2"));
        Assert.True(boundaries.IsSuccess, boundaries.Error.ToString()); Assert.Equal(3, boundaries.Outputs!.Length);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("2-1")]
    [InlineData("1,,2")]
    [InlineData("1,1")]
    [InlineData("١")]
    [InlineData("1-999999999999")]
    public void PageGrammarRejectsInvalidIndexes(string selection) => Assert.Throws<ArgumentException>(() => PdfPageSelection.Parse(selection, 3));
    [Fact]
    public async Task InvalidInputCollisionsAndDuplicates_NeverModifyOriginals()
    {
        var source = Fixture("source.pdf", "Original"); var before = File.ReadAllBytes(source);
        var destination = PathFor("existing.pdf"); File.WriteAllBytes(destination, [1, 2, 3]);
        var service = Service();
        Assert.Equal(PdfError.DestinationExists, (await service.ExecuteAsync(new(PdfOperation.Extract, [source], destination))).Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.Equal(PdfError.DuplicateInput, (await service.ExecuteAsync(new(PdfOperation.Merge, [source, source], PathFor("duplicate.pdf")))).Error);
        File.WriteAllText(PathFor("fake.pdf"), "not a PDF");
        Assert.False((await service.InspectAsync(PathFor("fake.pdf"))).IsSuccess);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    private PdfService Service() => new(new QpdfLocator(), new DiagnosticRunner(log));
    private sealed class DiagnosticRunner(Xunit.Abstractions.ITestOutputHelper output) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (!result.Succeeded && !request.Arguments.Contains("--is-encrypted")) { output.WriteLine(request.ExecutablePath + " " + string.Join(' ', request.Arguments) + "\n" + result.StandardError); }
            return result;
        }
    }
}
