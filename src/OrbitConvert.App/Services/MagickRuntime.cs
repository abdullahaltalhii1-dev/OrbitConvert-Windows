using System.IO;
using System.Xml.Linq;
using ImageMagick;
using ImageMagick.Configuration;

namespace OrbitConvert.App.Services;

/// <summary>The only initialization gate for native ImageMagick. A failed first attempt stays failed.</summary>
public static class MagickRuntime
{
    // ExecutionAndPublication serializes image/PDF first use and caches failures as well as success.
    private static readonly Lazy<bool> Initialized = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void EnsureInitialized() => _ = Initialized.Value;

    private static bool Initialize()
    {
        string? directory = null;
        try
        {
            var policy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ImageMagick", "policy.xml"));
            VerifyPolicy(policy);
            var configuration = ConfigurationFiles.Default;
            configuration.Policy.Data = policy;
            directory = MagickNET.Initialize(configuration);
            ResourceLimits.MaxProfileSize = 1024 * 1024;
            ResourceLimits.MaxMemoryRequest = 128ul * 1024 * 1024;

            // Initialize silently does nothing if native code was already used without this configuration.
            // Do not repair individual limits: coder/delegate rights might still be missing.
            if (ResourceLimits.Disk != 0 || ResourceLimits.Memory != 256ul * 1024 * 1024 ||
                ResourceLimits.Width != 8192 || ResourceLimits.Height != 8192 ||
                ResourceLimits.ListLength != 8 || ResourceLimits.Time != 30 ||
                ResourceLimits.Thread is < 1 or > 2 || ResourceLimits.MaxProfileSize != 1024 * 1024 ||
                ResourceLimits.MaxMemoryRequest != 128ul * 1024 * 1024)
            {
                throw new InvalidOperationException("ImageMagick resource policy verification failed.");
            }

            // Exercise actual native coder rights, not merely the XML or a managed format whitelist.
            using var image = new MagickImage(new byte[] { 0, 0, 0 }, new PixelReadSettings(1, 1, StorageType.Char, PixelMapping.RGB));
            RequireDenied(() => { _ = image.ToByteArray(MagickFormat.Gif); });
            RequireDenied(() =>
            {
                using var forbidden = new MagickImage(Convert.FromBase64String(
                    "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"), new MagickReadSettings { Format = MagickFormat.Gif });
            });

            var ownedDirectory = directory;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup(ownedDirectory);
            return true;
        }
        catch (Exception error)
        {
            if (directory is not null) { Cleanup(directory); }
            throw new MagickInitializationException(error);
        }
    }

    private static void RequireDenied(Action operation)
    {
        try { operation(); }
        catch (MagickPolicyErrorException) { return; }
        throw new InvalidOperationException("ImageMagick coder policy verification failed.");
    }

    private static void VerifyPolicy(string policy)
    {
        // The managed API has no map/delegate/filter policy getter. Verify the complete intended XML,
        // including those restrictions, before loading it; native limits and coder probes follow.
        var rules = new[]
        {
            ("delegate", "rights", "none", "pattern", "*"),
            ("filter", "rights", "none", "pattern", "*"),
            ("coder", "rights", "none", "pattern", "*"),
            ("coder", "rights", "read|write", "pattern", "{PNG,JPEG,WEBP}"),
            ("coder", "rights", "read", "pattern", "BMP"),
            ("coder", "rights", "write", "pattern", "BMP3"),
            ("resource", "name", "memory", "value", "256MiB"),
            ("resource", "name", "map", "value", "0"),
            ("resource", "name", "disk", "value", "0"),
            ("resource", "name", "width", "value", "8192"),
            ("resource", "name", "height", "value", "8192"),
            ("resource", "name", "list-length", "value", "8"),
            ("resource", "name", "thread", "value", "2"),
            ("resource", "name", "time", "value", "30"),
        };
        var expected = new XElement("policymap", rules.Select(rule => new XElement("policy",
            new XAttribute("domain", rule.Item1), new XAttribute(rule.Item2, rule.Item3), new XAttribute(rule.Item4, rule.Item5))));
        if (!XNode.DeepEquals(expected, XDocument.Parse(policy).Root))
        {
            throw new InvalidOperationException("ImageMagick policy does not match the approved restrictions.");
        }
    }

    private static void Cleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class MagickInitializationException(Exception inner)
    : InvalidOperationException("Image processing is unavailable because its security policy could not be verified.", inner);
