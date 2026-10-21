using System.IO;

namespace OrbitConvert.App.Services.Documents;

public sealed class PandocLocator(string? applicationDirectory = null, Func<string, string?>? environment = null)
{
    public const string Version = "3.12.1";
    public string? Locate()
    {
        var local = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "pandoc", "pandoc.exe");
        if (File.Exists(local)) { return local; }
        var directory = (environment ?? Environment.GetEnvironmentVariable)("ORBITCONVERT_PANDOC_DIR");
        return directory is not null && Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "pandoc.exe"))
            ? Path.Combine(directory, "pandoc.exe") : null;
    }
}
