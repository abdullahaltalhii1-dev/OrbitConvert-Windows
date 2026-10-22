using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using OrbitConvert.App.Services.Pdf;
using UglyToad.PdfPig;

namespace OrbitConvert.Tests;

public sealed class PdfArabicTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "pdf-ar-" + Guid.NewGuid().ToString("N"))).FullName;
    private string PathFor(string name) => Path.Combine(_folder, name);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private string Source()
    {
        var source = PathFor("عربية English fixture.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Pdf", "arabic-layout.pdf"), source);
        return source;
    }
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    public async Task ActualUnicodeText_PreservesArabicMixedAndEnglishLogicalOrder(string page)
    {
        var source = Source(); var before = SHA256.HashData(File.ReadAllBytes(source));
        var destination = PathFor("نص " + page + ".txt");
        var result = await new PdfService().ExecuteAsync(new(PdfOperation.Text, [source], destination, page));
        Assert.True(result.IsSuccess, result.Error.ToString()); Assert.False(result.CleanupFailed);
        var text = File.ReadAllText(destination, new UTF8Encoding(false, true));
        Assert.Equal(text[..Math.Min(text.Length, 4000)], result.TextPreview);
        if (page == "1")
        {
            Assert.Contains("اختبار الصفحة العربية الأولى", text);
            Assert.Contains("مرحبا بالعالم. هذه فقرة عربية فعلية لاختبار استخراج النص وترتيب القراءة.", text);
            Assert.Contains("الأرقام: ١٢٣ و ٤٥٦ و 789، وعلامات الترقيم: (قوسان)، سؤال؟", text);
            Assert.Contains("مَرْحَبًا بِالْعَالَمِ. لا لإتلاف الملفات؛ نعم للحفاظ على الأصول.", text);
            Assert.Contains("البند الأول: قراءة النص.", text); Assert.Contains("البند الثاني: حفظ نسخة جديدة.", text);
            Assert.True(text.IndexOf("هذه فقرة طويلة", StringComparison.Ordinal) < text.IndexOf("ونحافظ على الكلمات", StringComparison.Ordinal));
            var phrase = text.Substring(text.IndexOf("مرحبا", StringComparison.Ordinal), 5);
            Assert.Equal(new[] { 0x0645, 0x0631, 0x062D, 0x0628, 0x0627 }, phrase.EnumerateRunes().Select(x => x.Value));
            Assert.DoesNotContain("English paragraph TWO", text);
        }
        else if (page == "2")
        {
            Assert.Contains("هذا سطر عربي مع English words ورقم 2026 ورقم ١٢٣.", text);
            Assert.Contains("English before نص عربي and after 42.", text);
            Assert.Contains("English paragraph TWO. Mixed language and table reading order.", text);
            Assert.Contains("المنتج\tالعدد\tالوصف", text);
            Assert.Contains("كتاب\t٢\tاختبار الجدول", text);
            Assert.Contains("قلم\t3\tMixed text", text);
        }
        else { Assert.Contains("English landscape page THREE", text); Assert.Contains("TOP marker.", text); Assert.Contains("BOTTOM marker", text); }
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    [Fact]
    public async Task ActualArabicPdfOperations_AreReadableOrderedAndNeverOverwrite()
    {
        var source = Source(); var before = File.ReadAllBytes(source); var service = new PdfService();
        var extracted = await service.ExecuteAsync(new(PdfOperation.Extract, [source], PathFor("تقسيم.pdf"), "1-2"));
        Assert.True(extracted.IsSuccess, extracted.Error.ToString());
        using (var pdf = PdfDocument.Open(extracted.Outputs!.Single())) { Assert.Equal(2, pdf.NumberOfPages); }
        var reordered = await service.ExecuteAsync(new(PdfOperation.Reorder, [source], PathFor("ترتيب.pdf"), "3,1"));
        Assert.True(reordered.IsSuccess, reordered.Error.ToString());
        using (var pdf = PdfDocument.Open(reordered.Outputs!.Single()))
        { Assert.Equal(2, pdf.NumberOfPages); Assert.True(pdf.GetPage(1).Width > pdf.GetPage(1).Height); Assert.True(pdf.GetPage(2).Width < pdf.GetPage(2).Height); }
        var merged = await service.ExecuteAsync(new(PdfOperation.Merge, [reordered.Outputs!.Single(), extracted.Outputs!.Single()], PathFor("دمج.pdf")));
        Assert.True(merged.IsSuccess, merged.Error.ToString());
        using (var pdf = PdfDocument.Open(merged.Outputs!.Single())) { Assert.Equal(4, pdf.NumberOfPages); Assert.Contains("English landscape", pdf.GetPage(1).Text); }
        var rendered = await service.ExecuteAsync(new(PdfOperation.Render, [source], PathFor("صور"), "1-3", 150));
        Assert.True(rendered.IsSuccess, rendered.Error.ToString()); Assert.Equal(3, rendered.Outputs!.Length);
        foreach (var output in rendered.Outputs) { using var image = new MagickImage(output); Assert.Equal(MagickFormat.Png, image.Format); Assert.True(image.Width > 1000 && image.Height > 1000); }
        var saved = File.ReadAllBytes(extracted.Outputs!.Single());
        Assert.Equal(PdfError.DestinationExists, (await service.ExecuteAsync(new(PdfOperation.Extract, [source], extracted.Outputs!.Single(), "3"))).Error);
        Assert.Equal(saved, File.ReadAllBytes(extracted.Outputs!.Single())); Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFileSystemEntries(_folder, ".orbitconvert-*"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingOrder_UsesGlyphPositions_NotContentStreamEnumeration(bool reverseEnumeration)
    {
        using var document = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "TestData", "Pdf", "arabic-layout.pdf"),
            new ParsingOptions { UseLenientParsing = false, UseActualText = true });
        var glyphs = document.GetPage(2).Letters;
        var result = PdfTextReadingOrder.ExtractHorizontal(reverseEnumeration ? glyphs.Reverse().ToArray() : glyphs);
        Assert.Contains("هذا سطر عربي مع English words ورقم 2026 ورقم ١٢٣.", result);
        Assert.Contains("English before نص عربي and after 42.", result);
        Assert.Contains("قلم\t3\tMixed text", result);
    }
}
