using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class MagickRuntimeTests
{
    [Theory]
    [InlineData("pdf-first")]
    [InlineData("image-first")]
    [InlineData("limits-first")]
    [InlineData("concurrent")]
    [InlineData("unprotected-image-first")]
    [InlineData("unprotected-limits-first")]
    [InlineData("unprotected-coders")]
    [InlineData("missing-policy")]
    [InlineData("invalid-policy")]
    public async Task FreshProcess_EnforcesPolicyRegardlessOfFirstCaller(string scenario)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "magick-runtime-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, "MagickRuntimeProbe", "OrbitConvert.MagickRuntimeProbe.exe");
            Assert.True(File.Exists(probe), "Fresh-process probe was not built/copied.");
            var result = await new ProcessRunner().RunAsync(new(probe, [scenario, folder], TimeSpan.FromSeconds(90)), CancellationToken.None);
            Assert.True(result.Succeeded, result.Outcome + "\n" + result.StandardOutput + "\n" + result.StandardError);
            Assert.Contains("PASS " + scenario, result.StandardOutput);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void NativeAccess_RemainsInsideTheApprovedGatedProductionFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OrbitConvert.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "src", "OrbitConvert.App");
        var allowed = new[] { "Services/MagickRuntime.cs", "Services/MagickImageEngine.cs", "Services/Pdf/PdfService.cs" };
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) { continue; }
            if (System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), @"\bnew\s+(?:ImageMagick\.)?Magick(?:Image|ImageCollection)\b|\b(?:MagickNET|ResourceLimits|MagickColors)\s*\."))
            {
                Assert.Contains(relative, allowed);
            }
        }
        foreach (var caller in allowed.Skip(1))
        {
            var code = File.ReadAllText(Path.Combine(source, caller));
            var gate = code.IndexOf("MagickRuntime.EnsureInitialized();", StringComparison.Ordinal);
            Assert.True(gate >= 0 && gate < code.IndexOf("new MagickImage", StringComparison.Ordinal), caller);
        }
    }
}
