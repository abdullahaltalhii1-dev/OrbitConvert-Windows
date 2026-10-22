namespace OrbitConvert.App.Services.Pdf;

public enum PdfError { None, UnreadableInput, InvalidPdf, Encrypted, UnsafeContent, TooLarge, InvalidSelection, DuplicateInput, ToolsUnavailable, ToolsIncompatible, DestinationExists, InvalidDestination, NoText, OutputValidationFailed, Cancelled, TimedOut, ProcessTerminationFailed, OutputFailure }
public enum PdfOperation { Merge, Extract, IndividualPages, Boundaries, Reorder, Render, Text }
public sealed record PdfPageInfo(int Number, double Width, double Height, int Rotation, string Fingerprint);
public sealed record PdfInspection(string FileName, long SizeBytes, string Version, bool Encrypted, string Title, string Author, PdfPageInfo[] Pages);
public sealed record PdfInspectionResult(PdfInspection? Inspection, PdfError Error, bool CleanupFailed = false)
{ public bool IsSuccess => Error == PdfError.None && Inspection is not null; }
public sealed record PdfRequest(PdfOperation Operation, string[] Sources, string Destination, string Pages = "", int Dpi = 150);
public sealed record PdfResult(PdfError Error, string[]? Outputs = null, string TextPreview = "", bool CleanupFailed = false)
{ public bool IsSuccess => Error == PdfError.None && Outputs is { Length: > 0 }; }
public sealed record PdfProgress(int Completed, int Total, string Stage);
public static class PdfLimits
{
    public const long InputBytes = 64 * 1024 * 1024, AggregateInputBytes = 128 * 1024 * 1024;
    public const long TemporaryBytes = 512 * 1024 * 1024, JobMemoryBytes = 512 * 1024 * 1024;
    public const int Pages = 200, OutputFiles = 100, Dimension = 8192, PagePixels = 16_000_000;
    public const long TotalPixels = 160_000_000;
    public const int TextCharacters = 2_000_000;
    public static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(2);
}
internal sealed class PdfRejected(PdfError error) : Exception { public PdfError Error { get; } = error; }

/// <summary>Deliberately small grammar: ASCII 1-based page numbers and ascending ranges, comma separated.</summary>
public static class PdfPageSelection
{
    public static int[] Parse(string text, int count, bool allowRepeated = false)
    {
        if (count is < 1 or > PdfLimits.Pages) { throw new ArgumentException("Invalid page count."); }
        if (string.IsNullOrWhiteSpace(text)) { return Enumerable.Range(1, count).ToArray(); }
        if (text.Length > 2000) { throw new ArgumentException("Selection too long."); }
        var pages = new List<int>();
        foreach (var part in text.Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || range.Any(x => x.Trim().Length == 0 || x.Trim().Any(c => c is < '0' or > '9')) ||
                !int.TryParse(range[0], out var first) || first < 1 || first > count) { throw new ArgumentException("Invalid page selection."); }
            var last = first;
            if (range.Length == 2 && (!int.TryParse(range[1], out last) || last < first || last > count)) { throw new ArgumentException("Invalid range."); }
            pages.AddRange(Enumerable.Range(first, last - first + 1));
            if (pages.Count > PdfLimits.Pages) { throw new ArgumentException("Too many pages."); }
        }
        if (!allowRepeated && pages.Distinct().Count() != pages.Count) { throw new ArgumentException("Repeated pages are not supported."); }
        return pages.ToArray();
    }
}
