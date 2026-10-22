using System.IO;
using ImageMagick;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Pdf;
using OrbitConvert.Tests;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

// Test-only executable: each invocation has genuinely fresh native ImageMagick state.
try
{
    // The parent test process may already be secured and export its temporary configuration path.
    // Clear that inherited path in this disposable probe only, before its first native access.
    Environment.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH", null);
    var mode = args[0];
    var folder = args[1];
    var source = Path.Combine(folder, "source.pdf");
    var output = Path.Combine(folder, "render.png");
    var builder = new PdfDocumentBuilder();
    var page = builder.AddPage(100, 100);
    page.AddText("Owned policy probe", 10, new PdfPoint(10, 60), builder.AddStandard14Font(Standard14Font.Helvetica));
    var original = builder.Build();
    File.WriteAllBytes(source, original);
    var png = ImageFixtures.Png(); // Managed WPF/PNG fixtures do not initialize ImageMagick.

    void ImageFirst()
    {
        using var input = new MemoryStream(png);
        var result = new MagickImageEngine().Inspect(input);
        if (!result.IsSuccess) { MagickRuntime.EnsureInitialized(); }
        Require(result.IsSuccess, "Image inspection failed: " + result.Error);
    }
    async Task PdfFirst()
    {
        var result = await new PdfService().ExecuteAsync(new(PdfOperation.Render, [source], output, "1", 150));
        Require(result.IsSuccess, "PDF render failed: " + result.Error);
    }
    if (mode.StartsWith("unprotected-", StringComparison.Ordinal) || mode is "missing-policy" or "invalid-policy")
    {
        var policyFailure = mode is "missing-policy" or "invalid-policy";
        if (mode == "unprotected-image-first") { using var unprotected = new MagickImage(png); }
        else if (!policyFailure) { _ = ResourceLimits.Disk; } // Intentionally bypass the gate in this child only.
        if (mode == "unprotected-coders")
        {
            // Matching resource getters alone cannot prove that coder restrictions were loaded.
            ResourceLimits.Disk = 0; ResourceLimits.Memory = 256ul * 1024 * 1024;
            ResourceLimits.Width = 8192; ResourceLimits.Height = 8192;
            ResourceLimits.ListLength = 8; ResourceLimits.Time = 30; ResourceLimits.Thread = 2;
        }
        var baseDirectory = AppContext.BaseDirectory;
        if (policyFailure)
        {
            if (mode == "invalid-policy")
            {
                Directory.CreateDirectory(Path.Combine(folder, "ImageMagick"));
                File.WriteAllText(Path.Combine(folder, "ImageMagick", "policy.xml"), "<policymap />");
            }
            // Redirect only this disposable child's managed base path; no installed policy is edited.
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", folder);
        }
        InvalidOperationException first;
        try { first = InitializationFailure(); }
        finally { if (policyFailure) { AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", baseDirectory); } }
        var reason = mode == "unprotected-coders" ? "coder policy" : mode == "invalid-policy" ? "approved restrictions" : "resource policy";
        Require(mode == "missing-policy" ? first.InnerException is IOException : first.InnerException?.Message.Contains(reason, StringComparison.Ordinal) == true,
            "Unexpected initialization failure: " + first);
        Require(ReferenceEquals(first, InitializationFailure()), "Failed initialization was retried instead of cached.");
        using var input = new MemoryStream(png);
        using var converted = new MemoryStream();
        var engine = new MagickImageEngine();
        Require(engine.Inspect(input).Error == ImageInspectionError.ResourceLimit, "Inspection did not fail closed.");
        input.Position = 0;
        Require(engine.Convert(input, converted, "JPEG").Error == ImageInspectionError.ResourceLimit && converted.Length == 0,
            "Conversion did not fail closed before output.");
        input.Position = 0;
        Require(engine.ValidateOutput(input, new(2, 3, 8, "PNG", false)).Error == ImageInspectionError.OutputValidationFailed,
            "Output validation did not fail closed.");
        var refused = await new PdfService().ExecuteAsync(new(PdfOperation.Render, [source], output, "1", 150));
        Require(refused.Error == PdfError.OutputValidationFailed && !refused.CleanupFailed && !File.Exists(output),
            "PDF did not refuse unsafe verification/clean up: " + refused);
        Require(Directory.GetFiles(folder).Length == 1 && original.SequenceEqual(File.ReadAllBytes(source)),
            "Failed operation left output/staging or changed source.");
        Console.WriteLine("PASS " + mode);
        return 0;
    }
    if (mode == "pdf-first") { await PdfFirst(); Verify(); ImageFirst(); }
    else if (mode == "image-first") { ImageFirst(); Verify(); await PdfFirst(); }
    else if (mode == "limits-first") { MagickRuntime.EnsureInitialized(); Verify(); await PdfFirst(); ImageFirst(); }
    else if (mode == "concurrent")
    {
        using var start = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(() => { start.Wait(); MagickRuntime.EnsureInitialized(); })).ToList();
        callers.Add(Task.Run(async () => { start.Wait(); await PdfFirst(); }));
        callers.Add(Task.Run(() => { start.Wait(); ImageFirst(); }));
        start.Set();
        await Task.WhenAll(callers);
    }
    else { throw new ArgumentException("Unknown scenario."); }
    Verify();
    Require(original.SequenceEqual(File.ReadAllBytes(source)), "Original PDF changed.");
    Console.WriteLine("PASS " + mode);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

static InvalidOperationException InitializationFailure()
{
    try { MagickRuntime.EnsureInitialized(); }
    catch (InvalidOperationException error) { return error; }
    throw new InvalidOperationException("Unsafe initialization was accepted.");
}

static void Verify()
{
    Require(ResourceLimits.Disk == 0, "Disk expected 0, actual " + ResourceLimits.Disk);
    Require(ResourceLimits.Memory == 256ul * 1024 * 1024, "Memory policy missing.");
    Require(ResourceLimits.Width == 8192 && ResourceLimits.Height == 8192, "Dimension policy missing.");
    Require(ResourceLimits.ListLength == 8 && ResourceLimits.Time == 30, "List/time policy missing.");
    Require(ResourceLimits.Thread is >= 1 and <= 2, "Thread policy missing.");
    Require(ResourceLimits.MaxProfileSize == 1024 * 1024 && ResourceLimits.MaxMemoryRequest == 128ul * 1024 * 1024,
        "Profile/allocation policy missing.");
    using var image = new MagickImage(ImageFixtures.Png());
    var writerBlocked = false;
    try { _ = image.ToByteArray(MagickFormat.Gif); }
    catch (MagickPolicyErrorException) { writerBlocked = true; }
    Require(writerBlocked, "Forbidden GIF writer was allowed.");
    try
    {
        using var gif = new MagickImage(Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"),
            new MagickReadSettings { Format = MagickFormat.Gif });
    }
    catch (MagickPolicyErrorException) { return; }
    throw new InvalidOperationException("Forbidden GIF reader was allowed.");
}
