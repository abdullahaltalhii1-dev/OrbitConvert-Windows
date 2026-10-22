using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OrbitConvert.App.Services.Pdf;

internal sealed record PdfWorkerRequest(string Action, string Source, string Response, int[] Pages, int Dpi = 150);
internal sealed record PdfWorkerResponse(PdfError Error, PdfInspection? Inspection = null, string Preview = "");

/// <summary>Short-lived, no-window child of ProcessRunner. A stuck parser/render is stopped by terminating the job,
/// never by abandoning a thread inside the WPF process. No forms, JavaScript/V8 or external actions are initialized.</summary>
internal static class PdfNativeWorker
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static int Run(string requestPath)
    {
        string? response = null;
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
            if (!Path.GetFileName(root).StartsWith(".orbitconvert-pdf-", StringComparison.Ordinal) ||
                !Path.GetDirectoryName(root)!.Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(requestPath).Length > 16_384 || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { return 2; }
            var request = JsonSerializer.Deserialize<PdfWorkerRequest>(File.ReadAllText(requestPath, Utf8)) ?? throw new PdfRejected(PdfError.InvalidPdf);
            var source = Child(root, request.Source);
            response = Child(root, request.Response);
            if (new FileInfo(source).Length > PdfLimits.InputBytes) { throw new PdfRejected(PdfError.TooLarge); }
            if (request.Action is not ("inspect" or "render" or "text")) { throw new PdfRejected(PdfError.InvalidSelection); }
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = PdfDocument.Open(input, new ParsingOptions { UseLenientParsing = false });
            if (document.IsEncrypted) { throw new PdfRejected(PdfError.Encrypted); }
            if (document.NumberOfPages is < 1 or > PdfLimits.Pages) { throw new PdfRejected(PdfError.TooLarge); }
            if (request.Action == "text")
            {
                var text = new StringBuilder();
                foreach (var index in ValidatePages(request.Pages, document.NumberOfPages))
                {
                    var pageText = ContentOrderTextExtractor.GetText(document.GetPage(index));
                    if ((long)text.Length + pageText.Length + 2 > PdfLimits.TextCharacters) { throw new PdfRejected(PdfError.TooLarge); }
                    text.AppendLine(pageText);
                }
                if (string.IsNullOrWhiteSpace(text.ToString())) { throw new PdfRejected(PdfError.NoText); }
                using (var output = new FileStream(Child(root, "text.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output, Utf8)) { writer.Write(text.ToString()); }
                WriteResponse(response, new(PdfError.None, Preview: text.ToString(0, Math.Min(text.Length, 4000))));
                return 0;
            }
            LoadNativeLibrary();
            fpdfview.FPDF_InitLibrary();
            try
            {
                var native = fpdfview.FPDF_LoadDocument(source, null);
                if (native is null || native.__Instance == IntPtr.Zero) { throw new PdfRejected(PdfError.InvalidPdf); }
                try
                {
                    if (fpdfview.FPDF_GetPageCount(native) != document.NumberOfPages) { throw new PdfRejected(PdfError.InvalidPdf); }
                    var info = new List<PdfPageInfo>();
                    long pixels = 0;
                    var pages = request.Action == "render" ? ValidatePages(request.Pages, document.NumberOfPages) : Enumerable.Range(1, document.NumberOfPages).ToArray();
                    if (request.Action == "render" && (pages.Length > PdfLimits.OutputFiles || request.Dpi is not (150 or 300))) { throw new PdfRejected(PdfError.InvalidSelection); }
                    foreach (var index in pages)
                    {
                        var page = document.GetPage(index);
                        if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width is < 1 or > 2400 || page.Height is < 1 or > 2400 ||
                            page.Operations.Count > 100_000 || page.Letters.Count > 200_000) { throw new PdfRejected(PdfError.TooLarge); }
                        var nativePage = fpdfview.FPDF_LoadPage(native, index - 1);
                        if (nativePage is null || nativePage.__Instance == IntPtr.Zero) { throw new PdfRejected(PdfError.InvalidPdf); }
                        try
                        {
                            var widthPoints = fpdfview.FPDF_GetPageWidthF(nativePage);
                            var heightPoints = fpdfview.FPDF_GetPageHeightF(nativePage);
                            var dpi = request.Action == "render" ? request.Dpi : 36;
                            var width = checked((int)Math.Ceiling(widthPoints * dpi / 72.0));
                            var height = checked((int)Math.Ceiling(heightPoints * dpi / 72.0));
                            pixels += (long)width * height;
                            if (width < 1 || height < 1 || width > PdfLimits.Dimension || height > PdfLimits.Dimension || (long)width * height > PdfLimits.PagePixels ||
                                request.Action == "render" && pixels > PdfLimits.TotalPixels) { throw new PdfRejected(PdfError.TooLarge); }
                            var bitmap = fpdfview.FPDFBitmapCreate(width, height, 1);
                            if (bitmap is null || bitmap.__Instance == IntPtr.Zero) { throw new PdfRejected(PdfError.TooLarge); }
                            byte[] buffer;
                            int stride;
                            try
                            {
                                if (fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xffffffff) == 0) { throw new PdfRejected(PdfError.InvalidPdf); }
                                fpdfview.FPDF_RenderPageBitmap(bitmap, nativePage, 0, 0, width, height, 0, 1); // FPDF_ANNOT, no forms/actions.
                                stride = fpdfview.FPDFBitmapGetStride(bitmap);
                                if (stride < checked(width * 4) || stride > checked(width * 4 + 64)) { throw new PdfRejected(PdfError.InvalidPdf); }
                                buffer = new byte[checked(stride * height)];
                                Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap), buffer, 0, buffer.Length);
                            }
                            finally { fpdfview.FPDFBitmapDestroy(bitmap); }
                            if (request.Action == "render")
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, buffer, stride)));
                                using var output = new FileStream(Child(root, $"page-{index:D4}.png"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                encoder.Save(output);
                                if (output.Length > 96 * 1024 * 1024) { throw new PdfRejected(PdfError.TooLarge); }
                            }
                            else
                            {
                                using var hash = SHA256.Create();
                                using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                                { foreach (var operation in page.Operations) { operation.Write(sink); } sink.FlushFinalBlock(); }
                                var identity = Convert.ToHexString(hash.Hash!) + Convert.ToHexString(SHA256.HashData(buffer)) +
                                    widthPoints.ToString("R", CultureInfo.InvariantCulture) + "/" + heightPoints.ToString("R", CultureInfo.InvariantCulture);
                                info.Add(new(index, widthPoints, heightPoints, page.Rotation.Value, Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity)))));
                            }
                        }
                        finally { fpdfview.FPDF_ClosePage(nativePage); }
                    }
                    var inspection = request.Action == "inspect" ? new PdfInspection(Path.GetFileName(source), input.Length,
                        document.Version.ToString(CultureInfo.InvariantCulture), false, SafeMetadata(document.Information.Title), SafeMetadata(document.Information.Author), info.ToArray()) : null;
                    WriteResponse(response, new(PdfError.None, inspection));
                }
                finally { fpdfview.FPDF_CloseDocument(native); }
            }
            finally { fpdfview.FPDF_DestroyLibrary(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            // Process boundary: native faults may instead terminate this child; the parent never calls that success.
            var code = error switch
            {
                PdfRejected rejected => rejected.Error,
                DllNotFoundException or BadImageFormatException or EntryPointNotFoundException => PdfError.ToolsUnavailable,
                OutOfMemoryException or OverflowException => PdfError.TooLarge,
                _ => PdfError.InvalidPdf
            };
            if (response is not null) { try { WriteResponse(response, new(code)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return 1;
        }
    }
    private static string Child(string root, string name)
    {
        if (name.Length is < 1 or > 100 || name != Path.GetFileName(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { throw new PdfRejected(PdfError.InvalidPdf); }
        var path = Path.Combine(root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new PdfRejected(PdfError.UnsafeContent); }
        return path;
    }
    private static int[] ValidatePages(int[] pages, int count)
    {
        if (pages.Length is < 1 or > PdfLimits.Pages || pages.Any(x => x < 1 || x > count) || pages.Distinct().Count() != pages.Length) { throw new PdfRejected(PdfError.InvalidSelection); }
        return pages;
    }
    private static string SafeMetadata(string? text) => new((text ?? "").Where(c => !char.IsControl(c)).Take(1000).ToArray());
    private static void LoadNativeLibrary()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { throw new PdfRejected(PdfError.ToolsUnavailable); }
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll");
        using var input = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(input)) != "AE913F407E8D3F5CDBF08A9869564D647B13E132E01EB376C0A5A33B7A227A45")
        { throw new PdfRejected(PdfError.ToolsIncompatible); }
        NativeLibrary.SetDllImportResolver(typeof(fpdfview).Assembly, (name, _, _) =>
            name.Contains("pdfium", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(path) : IntPtr.Zero);
    }
    private static void WriteResponse(string path, PdfWorkerResponse result)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, result);
    }
}
