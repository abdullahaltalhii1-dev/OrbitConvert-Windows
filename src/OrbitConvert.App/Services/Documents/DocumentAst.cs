using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using OrbitConvert.App.Services;

namespace OrbitConvert.App.Services.Documents;

/// <summary>Vets Pandoc's intermediate representation; it is not a new document reader or writer.</summary>
internal static class DocumentAst
{
    private static readonly HashSet<string> Nodes = new(StringComparer.Ordinal)
    {
        "Plain", "Para", "LineBlock", "CodeBlock", "BlockQuote", "OrderedList", "BulletList", "DefinitionList", "Header", "HorizontalRule", "Table", "Div", "Figure",
        "Str", "Emph", "Underline", "Strong", "Strikeout", "Superscript", "Subscript", "SmallCaps", "Quoted", "Cite", "Code", "Space", "SoftBreak", "LineBreak", "Link", "Image", "Note", "Span",
        "AlignLeft", "AlignRight", "AlignCenter", "AlignDefault", "ColWidth", "ColWidthDefault", "DefaultStyle", "Example", "Decimal", "LowerRoman", "UpperRoman", "LowerAlpha", "UpperAlpha", "DefaultDelim", "Period", "OneParen", "TwoParens", "SingleQuote", "DoubleQuote",
    };
    public static JsonObject Read(string path)
    {
        if (new FileInfo(path).Length > DocumentFormats.MaximumExpandedBytes) { throw new DocumentRejected(DocumentError.TooLarge); }
        return JsonNode.Parse(File.ReadAllText(path, DocumentGuard.Utf8), documentOptions: new() { MaxDepth = 128 })?.AsObject()
            ?? throw new DocumentRejected(DocumentError.InvalidDocument);
    }
    public static void Sanitize(JsonObject root, string workspace)
    {
        if (root["blocks"] is not JsonArray || root["pandoc-api-version"]?.ToJsonString() != "[1,23,1,2]") { throw new DocumentRejected(DocumentError.ToolsIncompatible); }
        root["meta"] = new JsonObject(); // No input templates, metadata includes, CSS, filters or title blocks.
        var count = 0;
        Visit(root);
        void Visit(JsonNode? node)
        {
            if (++count > 250_000) { throw new DocumentRejected(DocumentError.TooLarge); }
            if (node is JsonObject obj)
            {
                if (obj["t"] is { } type)
                {
                    var tag = type.GetValue<string>();
                    if (!Nodes.Contains(tag)) { throw new DocumentRejected(DocumentError.UnsafeContent); }
                    if (tag is "Link" or "Image")
                    {
                        var target = obj["c"]!.AsArray()[2]!.AsArray();
                        var url = target[0]!.GetValue<string>();
                        if (tag == "Image") { target[0] = SafeImage(url, workspace); }
                        else if (!url.StartsWith('#') && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"))
                        { throw new DocumentRejected(DocumentError.UnsafeContent); }
                    }
                }
                foreach (var child in obj.ToArray()) { Visit(child.Value); }
            }
            else if (node is JsonArray array)
            {
                // Pandoc Attr triples: strip untrusted attributes/classes, including event handlers and styles.
                if (array.Count == 3 && array[0] is JsonValue value && value.TryGetValue<string>(out _) && array[1] is JsonArray && array[2] is JsonArray)
                { array[0] = ""; array[1] = new JsonArray(); array[2] = new JsonArray(); }
                foreach (var child in array) { Visit(child); }
            }
        }
    }
    private static string SafeImage(string url, string workspace)
    {
        byte[] bytes;
        if (url.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || url.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))
        { bytes = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]); }
        else
        {
            // Only images explicitly extracted by Pandoc from this operation's bounded office package.
            var full = Path.GetFullPath(url);
            var allowed = Path.GetFullPath(Path.Combine(workspace, "media")) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { throw new DocumentRejected(DocumentError.UnsafeContent); }
            if (new FileInfo(full).Length > 4 * 1024 * 1024) { throw new DocumentRejected(DocumentError.TooLarge); }
            bytes = File.ReadAllBytes(full);
        }
        if (bytes.Length > 4 * 1024 * 1024) { throw new DocumentRejected(DocumentError.TooLarge); }
        using var input = new MemoryStream(bytes, writable: false);
        var inspected = new MagickImageEngine().Inspect(input);
        if (!inspected.IsSuccess || inspected.Inspection!.Format is not ("PNG" or "JPEG")) { throw new DocumentRejected(DocumentError.UnsafeContent); }
        return "data:image/" + (inspected.Inspection.Format == "PNG" ? "png" : "jpeg") + ";base64," + Convert.ToBase64String(bytes);
    }
    public static DocumentInspection Describe(JsonObject root, DocumentFormat format, long size)
    {
        var text = new StringBuilder();
        int headings = 0, tables = 0, lists = 0;
        Walk(root["blocks"]);
        void Walk(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var tag = obj["t"]?.GetValue<string>();
                if (tag == "Header") { headings++; }
                if (tag == "Table") { tables++; }
                if (tag is "BulletList" or "OrderedList") { lists++; }
                if (tag == "Str") { text.Append(obj["c"]!.GetValue<string>()).Append(' '); return; }
                if (tag is "Code" or "CodeBlock") { text.Append(obj["c"]!.AsArray()[1]!.GetValue<string>()).Append(' '); return; }
                if (obj["c"] is { } content) { Walk(content); }
            }
            else if (node is JsonArray array) { foreach (var child in array) { Walk(child); } }
        }
        var actual = text.ToString().Trim();
        if (actual.Length == 0) { throw new DocumentRejected(DocumentError.InvalidDocument); }
        return new(format, size, actual, headings, tables, lists, actual.Any(IsArabic));
    }
    internal static bool IsArabic(char c) => c is >= '\u0600' and <= '\u06ff' or >= '\u0750' and <= '\u077f' or >= '\u08a0' and <= '\u08ff';
    internal static bool IsRtl(string text) => text.Where(char.IsLetter).Take(1).Any(IsArabic);
    public static JsonObject LiteralText(string text) => new()
    {
        ["pandoc-api-version"] = new JsonArray(1, 23, 1, 2), ["meta"] = new JsonObject(),
        ["blocks"] = new JsonArray(text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split("\n\n").Select(p => (JsonNode)new JsonObject
        { ["t"] = "Para", ["c"] = new JsonArray(new JsonObject { ["t"] = "Str", ["c"] = p }) }).ToArray()),
    };
    public static bool PreservesText(string source, string output)
    {
        static string Significant(string text) => new(text.Normalize(NormalizationForm.FormC).Where(c => char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark).ToArray());
        var expected = Significant(source);
        var actual = Significant(output);
        var index = 0;
        foreach (var c in actual) { if (index < expected.Length && expected[index] == c) { index++; } }
        return index == expected.Length;
    }
}
