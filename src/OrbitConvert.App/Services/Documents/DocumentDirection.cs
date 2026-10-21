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
                    array[i] = new JsonObject
                    {
                        ["t"] = "Div",
                        ["c"] = new JsonArray(new JsonArray("", new JsonArray(),
                        new JsonArray(new JsonArray("dir", rtl ? "rtl" : "ltr"), new JsonArray("lang", rtl ? "ar" : "en"))), new JsonArray(block.DeepClone()))
                    };
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
            var existing = properties.Element(w + "bidi");
            var rtl = existing is null ? DocumentAst.IsRtl(text) : (string?)existing.Attribute(w + "val") is null or "1" or "true" or "on";
            properties.Elements(w + "bidi").Remove();
            var bidi = new XElement(w + "bidi", new XAttribute(w + "val", rtl ? "1" : "0"));
            var following = properties.Elements().FirstOrDefault(e => e.Name.LocalName is "adjustRightInd" or "snapToGrid" or "spacing" or "ind" or "contextualSpacing" or "mirrorIndents" or "suppressOverlap" or "jc" or "textDirection" or "textAlignment" or "textboxTightWrap" or "outlineLvl" or "divId" or "cnfStyle" or "rPr" or "sectPr" or "pPrChange");
            if (following is null) { properties.Add(bidi); } else { following.AddBeforeSelf(bidi); }
            foreach (var run in paragraph.Descendants(w + "r"))
            {
                var content = string.Concat(run.Descendants(w + "t").Select(t => t.Value));
                if (!content.Any(DocumentAst.IsArabic)) { continue; }
                var rpr = run.Element(w + "rPr");
                if (rpr is null) { rpr = new XElement(w + "rPr"); run.AddFirst(rpr); }
                rpr.Elements(w + "rtl").Remove();
                rpr.Add(new XElement(w + "rtl", new XAttribute(w + "val", "1")));
                if (rpr.Element(w + "rFonts") is null)
                {
                    var fonts = new XElement(w + "rFonts", new XAttribute(w + "cs", "Arial"));
                    var next = rpr.Elements().FirstOrDefault(e => e.Name != w + "rStyle");
                    if (next is null) { rpr.Add(fonts); } else { next.AddBeforeSelf(fonts); }
                }
                if (rpr.Element(w + "lang") is null) { rpr.Add(new XElement(w + "lang", new XAttribute(w + "bidi", "ar-SA"))); }
            }
        }
        entry.Delete();
        using var output = zip.CreateEntry("word/document.xml").Open();
        document.Save(output);
    }

    public static void ApplyOdt(string path)
    {
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("content.xml") ?? throw new DocumentRejected(DocumentError.OutputValidationFailed);
        XDocument document;
        using (var input = entry.Open()) { document = DocumentGuard.ReadXml(input); }
        var styles = document.Root!.Element(office + "automatic-styles");
        if (styles is null) { styles = new XElement(office + "automatic-styles"); document.Root.AddFirst(styles); }
        var index = 0;
        foreach (var paragraph in document.Descendants().Where(e => e.Name == text + "p" || e.Name == text + "h").ToArray())
        {
            var parent = (string?)paragraph.Attribute(text + "style-name");
            var original = styles.Elements(style + "style").FirstOrDefault(e => (string?)e.Attribute(style + "name") == parent);
            var generated = original is null ? new XElement(style + "style", new XAttribute(style + "family", "paragraph")) : new XElement(original);
            var name = "OrbitConvertDirection" + index++;
            generated.SetAttributeValue(style + "name", name);
            if (original is null && parent is not null) { generated.SetAttributeValue(style + "parent-style-name", parent); }
            var properties = generated.Element(style + "paragraph-properties");
            if (properties is null) { properties = new XElement(style + "paragraph-properties"); generated.AddFirst(properties); }
            if (properties.Attribute(style + "writing-mode") is null) { properties.SetAttributeValue(style + "writing-mode", DocumentAst.IsRtl(paragraph.Value) ? "rl-tb" : "lr-tb"); }
            styles.Add(generated); paragraph.SetAttributeValue(text + "style-name", name);
        }
        entry.Delete();
        using var output = zip.CreateEntry("content.xml").Open();
        document.Save(output);
    }
}
