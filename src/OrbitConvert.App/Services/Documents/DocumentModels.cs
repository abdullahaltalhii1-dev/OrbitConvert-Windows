namespace OrbitConvert.App.Services.Documents;

public enum DocumentFormat { DOCX, ODT, RTF, TXT, Markdown, HTML }
public enum DocumentError { None, UnreadableInput, UnsupportedFormat, UnsupportedDirection, InvalidDocument, UnsafeContent, TooLarge, ToolsUnavailable, ToolsIncompatible, SamePath, DestinationExists, InvalidDestination, ConversionFailed, OutputValidationFailed, Cancelled, TimedOut, ProcessTerminationFailed, OutputFailure }
public enum DocumentStage { Inspecting, Converting, Verifying }
public sealed record DocumentInspection(DocumentFormat Format, long SizeBytes, string Text, int Headings, int Tables, int Lists, bool HasArabic);
public sealed record DocumentInspectionResult(DocumentInspection? Inspection, DocumentError Error, bool CleanupFailed = false)
{
    public bool IsSuccess => Error == DocumentError.None && Inspection is not null;
}
public sealed record DocumentConversionResult(DocumentError Error, string? OutputPath = null, bool CleanupFailed = false)
{
    public bool IsSuccess => Error == DocumentError.None && OutputPath is not null;
}
public static class DocumentFormats
{
    public static string Extension(this DocumentFormat format) => format switch { DocumentFormat.Markdown => ".md", _ => "." + format.ToString().ToLowerInvariant() };
    public static string Reader(this DocumentFormat format) => format switch { DocumentFormat.Markdown => "markdown-raw_html-raw_tex-yaml_metadata_block-pandoc_title_block", _ => format.ToString().ToLowerInvariant() };
    public static string Writer(this DocumentFormat format) => format switch { DocumentFormat.TXT => "plain", DocumentFormat.Markdown => "markdown-raw_html-raw_tex", DocumentFormat.HTML => "html5", _ => format.ToString().ToLowerInvariant() };
    // Deliberately reviewed matrix, not every combination advertised by the engine.
    public static IReadOnlyList<DocumentFormat> Outputs(DocumentFormat source) => source switch
    {
        DocumentFormat.DOCX => [DocumentFormat.ODT, DocumentFormat.Markdown, DocumentFormat.TXT],
        DocumentFormat.ODT => [DocumentFormat.DOCX, DocumentFormat.TXT],
        DocumentFormat.RTF => [DocumentFormat.DOCX, DocumentFormat.TXT],
        DocumentFormat.Markdown => [DocumentFormat.DOCX, DocumentFormat.HTML, DocumentFormat.TXT],
        DocumentFormat.HTML => [DocumentFormat.Markdown],
        DocumentFormat.TXT => [DocumentFormat.Markdown],
        _ => [],
    };
    public const long MaximumBytes = 25 * 1024 * 1024;
    public const long MaximumExpandedBytes = 64 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
}

internal sealed class DocumentRejected(DocumentError error) : Exception
{
    public DocumentError Error { get; } = error;
}
