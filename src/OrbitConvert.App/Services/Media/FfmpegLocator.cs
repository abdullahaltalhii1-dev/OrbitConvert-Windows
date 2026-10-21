using System.IO;

namespace OrbitConvert.App.Services.Media;

public enum FfmpegSource
{
    Bundled,
    DevelopmentOverride,
}

public sealed record FfmpegTools(string FfmpegPath, string FfprobePath, FfmpegSource Source);

/// <summary>H.264 encoders OrbitConvert can drive, in order of preference.</summary>
public enum H264Encoder
{
    None,
    LibX264,
    MediaFoundation,
}

public sealed record FfmpegCapabilities(string Version, bool HasLibX264, bool HasMediaFoundationH264, bool HasAacEncoder)
{
    /// <summary>libx264 (CRF control) when present; otherwise the Windows Media Foundation encoder (LGPL-friendly builds).</summary>
    public H264Encoder PreferredH264Encoder => HasLibX264 ? H264Encoder.LibX264
        : HasMediaFoundationH264 ? H264Encoder.MediaFoundation : H264Encoder.None;

    public bool CanConvertToMp4 => PreferredH264Encoder != H264Encoder.None && HasAacEncoder;

    public bool Supports(H264Encoder encoder) => encoder switch
    {
        H264Encoder.LibX264 => HasLibX264,
        H264Encoder.MediaFoundation => HasMediaFoundationH264,
        _ => false,
    };
}

/// <summary>
/// Resolves FFmpeg/FFprobe from controlled locations only. The installed application ships them in an
/// application-local <c>ffmpeg</c> folder; <c>ORBITCONVERT_FFMPEG_DIR</c> is a development/CI override.
/// PATH is never searched and nothing is downloaded at runtime.
/// </summary>
public sealed class FfmpegLocator(string applicationDirectory, Func<string, string?> environment)
{
    public const string BundledFolderName = "ffmpeg";
    public const string OverrideVariable = "ORBITCONVERT_FFMPEG_DIR";

    public FfmpegLocator()
        : this(AppContext.BaseDirectory, Environment.GetEnvironmentVariable)
    {
    }

    public FfmpegTools? Locate()
    {
        var bundled = FromDirectory(Path.Combine(applicationDirectory, BundledFolderName), FfmpegSource.Bundled);
        if (bundled is not null) { return bundled; }
        var overrideDirectory = environment(OverrideVariable);
        return string.IsNullOrWhiteSpace(overrideDirectory) ? null
            : FromDirectory(overrideDirectory.Trim().Trim('"'), FfmpegSource.DevelopmentOverride);
    }

    private static FfmpegTools? FromDirectory(string directory, FfmpegSource source)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) { return null; } // Never resolve relative to the working directory.
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new(ffmpeg, ffprobe, source) : null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Reads the version and the encoders this build actually provides.</summary>
    public static async Task<FfmpegCapabilities?> ProbeAsync(FfmpegTools tools, IProcessRunner runner, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var version = await runner.RunAsync(new(tools.FfmpegPath, ["-hide_banner", "-version"], timeout), cancellationToken).ConfigureAwait(false);
        var encoders = await runner.RunAsync(new(tools.FfmpegPath, ["-hide_banner", "-encoders"], timeout), cancellationToken).ConfigureAwait(false);
        var probe = await runner.RunAsync(new(tools.FfprobePath, ["-hide_banner", "-version"], timeout), cancellationToken).ConfigureAwait(false);
        if (!version.Succeeded || !encoders.Succeeded || !probe.Succeeded) { return null; }
        return ParseCapabilities(version.StandardOutput, encoders.StandardOutput);
    }

    internal static FfmpegCapabilities ParseCapabilities(string versionOutput, string encodersOutput)
    {
        var firstLine = versionOutput.Split('\n', 2)[0].Trim();
        var version = firstLine.StartsWith("ffmpeg version ", StringComparison.Ordinal)
            ? firstLine["ffmpeg version ".Length..].Split(' ', 2)[0] : "unknown";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in encodersOutput.Split('\n'))
        {
            // Listing rows look like " V....D libx264   description"; the legend rows contain '=' instead.
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && !parts[1].Contains('=')) { names.Add(parts[1]); }
        }
        return new(version, names.Contains("libx264"), names.Contains("h264_mf"), names.Contains("aac"));
    }
}
