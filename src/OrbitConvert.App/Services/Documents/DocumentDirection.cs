using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace OrbitConvert.App.Services.Documents;

internal static class DocumentDirection
{
    // Keep block directions explicit in HTML; do not mark English paragraphs RTL because another block is Arabic.
    public static void ApplyHtmlDirections(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                ApplyHtmlDirections(array[i]);
                if (array[i] is JsonObject block && block["t"]?.GetValue<string>() is "Para" or "Plain" or "Header")
                {
                    var rtl = false;
                    try { rtl = DocumentAst.IsRtl(DocumentAst.Describe(new JsonObject { ["blocks"] = new JsonArray(block.DeepClone()) }, DocumentFormat.HTML, 0).Text); }
                    catch (DocumentRejected error) when (error.Error == DocumentError.InvalidDocument) { } // An image-only paragraph has no language.
                    array[i] = new JsonObject { ["t"] = "Div", ["c"] = new JsonArray(new JsonArray("", new JsonArray(),
                        new JsonArray(new JsonArray("dir", rtl ? "rtl" : "ltr"), new JsonArray("lang", rtl ? "ar" : "en"))), new JsonArray(block.DeepClone())) };
                }
            }
        }
        else if (node is JsonObject obj) { if (obj["c"] is { } content) { ApplyHtmlDirections(content); } }
    }
    public static void ApplyDocx(string path)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentRejected(DocumentError.OutputValidationFailed);
        XDocument document;
        using (var input = entry.Open()) { document = DocumentGuard.ReadXml(input); }
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            var text = string.Concat(paragraph.Descendants(w + "t").Select(t => t.Value));
            var properties = paragraph.Element(w + "pPr");
            if (properties is null) { properties = new XElement(w + "pPr"); paragraph.AddFirst(properties); }
            properties.Elements(w + "bidi").Remove();
            properties.Add(new XElement(w + "bidi", new XAttribute(w + "val", DocumentAst.IsRtl(text) ? "1" : "0")));
            foreach (var run in paragraph.Descendants(w + "r"))
            {
                var content = string.Concat(run.Descendants(w + "t").Select(t => t.Value));
                if (!content.Any(DocumentAst.IsArabic)) { continue; }
                var rpr = run.Element(w + "rPr");
                if (rpr is null) { rpr = new XElement(w + "rPr"); run.AddFirst(rpr); }
                rpr.Elements(w + "rtl").Remove();
                rpr.Add(new XElement(w + "rtl", new XAttribute(w + "val", "1")));
                if (rpr.Element(w + "rFonts") is null) { rpr.Add(new XElement(w + "rFonts", new XAttribute(w + "cs", "Arial"))); }
                if (rpr.Element(w + "lang") is null) { rpr.Add(new XElement(w + "lang", new XAttribute(w + "bidi", "ar-SA"))); }
            }
        }
        entry.Delete();
        using var output = zip.CreateEntry("word/document.xml").Open();
        document.Save(output);
    }
}
