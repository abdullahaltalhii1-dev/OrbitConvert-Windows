using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OrbitConvert.App.Services.Documents;

internal static class DocumentGuard
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public static DocumentFormat Check(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > DocumentFormats.MaximumBytes) { throw new DocumentRejected(info.Length == 0 ? DocumentError.InvalidDocument : DocumentError.TooLarge); }
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[5];
        magic.Clear();
        stream.ReadExactly(magic[..(int)Math.Min(5, info.Length)]);
        if (magic[0] == 'P' && magic[1] == 'K') { return CheckPackage(path); }
        if (magic.SequenceEqual("{\\rtf"u8)) { return DocumentFormat.RTF; }
        var text = Utf8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
        if (text.Any(c => c == '\0' || char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) { throw new DocumentRejected(DocumentError.InvalidDocument); }
        if (string.IsNullOrWhiteSpace(text)) { throw new DocumentRejected(DocumentError.InvalidDocument); }
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => DocumentFormat.Markdown,
            ".txt" => DocumentFormat.TXT,
            ".html" or ".htm" when text.Contains('<') && text.Contains('>') => DocumentFormat.HTML,
            _ => throw new DocumentRejected(DocumentError.UnsupportedFormat),
        };
    }

    private static DocumentFormat CheckPackage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 512) { throw new DocumentRejected(DocumentError.TooLarge); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!names.Add(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Contains("..") ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) { throw new DocumentRejected(DocumentError.UnsafeContent); }
            expanded = checked(expanded + entry.Length);
            if (expanded > DocumentFormats.MaximumExpandedBytes || entry.Length > DocumentFormats.MaximumBytes) { throw new DocumentRejected(DocumentError.TooLarge); }
            if (name.Contains("vba", StringComparison.OrdinalIgnoreCase) || name.Contains("scripts", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("embeddings", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            { throw new DocumentRejected(DocumentError.UnsafeContent); }
            // Read every entry to force decompression/truncation errors before invoking the native reader.
            using var packed = entry.Open();
            using var content = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = packed.Read(buffer)) > 0)
            {
                if (content.Length + read > entry.Length) { throw new DocumentRejected(DocumentError.TooLarge); }
                content.Write(buffer, 0, read);
            }
            if (content.Length != entry.Length || Crc32(content.GetBuffer().AsSpan(0, (int)content.Length)) != entry.Crc32) { throw new DocumentRejected(DocumentError.InvalidDocument); }
            content.Position = 0;
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                var xml = ReadXml(content);
                foreach (var element in xml.Descendants())
                {
                    if (element.Name.LocalName is "object" or "object-ole" or "script" or "altChunk" or "oleObject" or "OLEObject" or "instrText" or "fldSimple")
                    { throw new DocumentRejected(DocumentError.UnsafeContent); }
                    if (element.Attributes().Any(a => a.Name.LocalName == "TargetMode" && a.Value == "External" &&
                        !(((string?)element.Attribute("Type"))?.EndsWith("/hyperlink", StringComparison.Ordinal) ?? false)))
                    { throw new DocumentRejected(DocumentError.UnsafeContent); }
                }
            }
            else { content.CopyTo(Stream.Null); }
        }
        if (names.Contains("word/document.xml") && names.Contains("[Content_Types].xml") && names.Contains("_rels/.rels")) { return DocumentFormat.DOCX; }
        if (names.Contains("content.xml") && names.Contains("mimetype") && names.Contains("META-INF/manifest.xml"))
        {
            using var reader = new StreamReader(zip.GetEntry("mimetype")!.Open(), Utf8);
            if (reader.ReadToEnd() == "application/vnd.oasis.opendocument.text") { return DocumentFormat.ODT; }
        }
        throw new DocumentRejected(DocumentError.UnsupportedFormat);
    }
    internal static XDocument ReadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = DocumentFormats.MaximumBytes });
        return XDocument.Load(reader);
    }
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        }
        return ~crc;
    }
}
