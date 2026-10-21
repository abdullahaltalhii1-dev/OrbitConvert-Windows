using System.Globalization;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Video;

/// <summary>
/// Builds FFmpeg/FFprobe argument lists. Every value is a separate element (no shell, no quoting), paths carry the
/// <c>file:</c> protocol prefix so they can never be read as options or URLs, and only local files plus the allowed
/// demuxers may be opened.
/// </summary>
internal static class FfmpegArguments
{
    private const string OutputDemuxers = "mov,mp4,m4a,3gp,3g2,mj2";

    public static string FileUrl(string fullPath) => "file:" + fullPath;

    public static IReadOnlyList<string> Probe(string fullPath, bool output = false) =>
    [
        "-hide_banner", "-v", "error",
        "-protocol_whitelist", "file", "-format_whitelist", output ? OutputDemuxers : VideoLimits.AllowedDemuxers,
        "-print_format", "json", "-show_format", "-show_streams",
        FileUrl(fullPath),
    ];

    public static IReadOnlyList<string> Convert(string sourcePath, string temporaryPath, VideoInspection source,
        VideoConversionOptions options, H264Encoder encoder)
    {
        var (width, height) = options.OutputSize(source.Video.DisplayWidth, source.Video.DisplayHeight);
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1",
            "-protocol_whitelist", "file", "-format_whitelist", VideoLimits.AllowedDemuxers,
            "-i", FileUrl(sourcePath),
            "-map", "0:" + source.Video.Index.ToString(CultureInfo.InvariantCulture),
        };
        if (source.Audio is { } audio) { arguments.AddRange(["-map", "0:" + audio.Index.ToString(CultureInfo.InvariantCulture)]); }
        // Rotation is applied to pixels by FFmpeg's autorotation; metadata and chapters are not carried over.
        arguments.AddRange(["-map_metadata", "-1", "-map_chapters", "-1",
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=bicubic")]);
        arguments.AddRange(VideoEncoder(encoder, options.Quality));
        if (source.Audio is not null) { arguments.AddRange(["-c:a", "aac", "-b:a", AudioBitrate(options.Quality)]); }
        arguments.AddRange(["-movflags", "+faststart", "-f", "mp4", "-y", FileUrl(temporaryPath)]);
        return arguments;
    }

    /// <summary>Decodes every packet of the produced file; any decoder error fails the check.</summary>
    public static IReadOnlyList<string> DecodeCheck(string temporaryPath) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-nostats", "-progress", "pipe:1",
        "-protocol_whitelist", "file", "-format_whitelist", OutputDemuxers,
        "-i", FileUrl(temporaryPath), "-map", "0", "-f", "null", "-",
    ];

    /// <summary>Quality presets. libx264 uses CRF (lower is better); Media Foundation uses its 0–100 quality mode.</summary>
    internal static IReadOnlyList<string> VideoEncoder(H264Encoder encoder, VideoQuality quality) => encoder switch
    {
        H264Encoder.LibX264 =>
            ["-c:v", "libx264", "-preset", "medium", "-crf", Crf(quality).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"],
        H264Encoder.MediaFoundation =>
            ["-c:v", "h264_mf", "-rate_control", "quality", "-quality", MediaFoundationQuality(quality).ToString(CultureInfo.InvariantCulture),
             "-pix_fmt", "nv12"],
        _ => throw new ArgumentOutOfRangeException(nameof(encoder)),
    };

    internal static int Crf(VideoQuality quality) => quality switch
    {
        VideoQuality.High => 18,
        VideoQuality.Balanced => 23,
        _ => 28,
    };

    internal static int MediaFoundationQuality(VideoQuality quality) => quality switch
    {
        VideoQuality.High => 85,
        VideoQuality.Balanced => 65,
        _ => 45,
    };

    internal static string AudioBitrate(VideoQuality quality) => quality switch
    {
        VideoQuality.High => "192k",
        VideoQuality.Balanced => "160k",
        _ => "128k",
    };
}
