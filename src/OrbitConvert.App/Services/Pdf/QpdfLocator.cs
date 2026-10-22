using System.IO;

namespace OrbitConvert.App.Services.Pdf;

public sealed class QpdfLocator(string? applicationDirectory = null, Func<string, string?>? environment = null)
{
    public const string Version = "12.4.2";
    public string? Locate()
    {
        var bundled = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "qpdf", "bin", "qpdf.exe");
        if (File.Exists(bundled)) { return bundled; }
        var directory = (environment ?? Environment.GetEnvironmentVariable)("ORBITCONVERT_QPDF_DIR");
        return directory is not null && Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "qpdf.exe"))
            ? Path.Combine(directory, "qpdf.exe") : null;
    }
}
