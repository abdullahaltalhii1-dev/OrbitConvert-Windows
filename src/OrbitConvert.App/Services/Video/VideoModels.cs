namespace OrbitConvert.App.Services.Video;

public enum VideoQuality
{
    High,
    Balanced,
    Small,
}

public enum VideoError
{
    None,
    ToolsUnavailable,
    ToolsUnsupported,
    EmptyInput,
    UnreadableInput,
    UnsupportedFormat,
    UnsupportedCodec,
    NoVideoStream,
    InvalidOrCorruptVideo,
    TooLarge,
    InvalidSettings,
    SamePath,
    DestinationExists,
    InvalidDestination,
    ConversionFailed,
    TimedOut,
    Cancelled,
    OutputFailure,
    OutputValidationFailed,
}

/// <param name="MaxHeight">0 keeps the source resolution; otherwise the video is only ever scaled down.</param>
public sealed record VideoConversionOptions(VideoQuality Quality = VideoQuality.Balanced, int MaxHeight = 0)
{
    public static IReadOnlyList<int> MaxHeights { get; } = [0, 1080, 720, 480];

    public bool IsValid => Enum.IsDefined(Quality) && MaxHeights.Contains(MaxHeight);

    /// <summary>Output frame size: display orientation, never upscaled, even dimensions for 4:2:0 H.264.</summary>
    public (int Width, int Height) OutputSize(int displayWidth, int displayHeight)
    {
        if (MaxHeight > 0 && displayHeight > MaxHeight)
        {
            var width = (int)Math.Round(displayWidth * (double)MaxHeight / displayHeight / 2, MidpointRounding.AwayFromZero) * 2;
            return (Math.Max(2, width), MaxHeight);
        }
        return (Math.Max(2, displayWidth / 2 * 2), Math.Max(2, displayHeight / 2 * 2));
    }
}

/// <param name="Width">Coded width.</param>
/// <param name="Rotation">Clockwise display rotation in degrees (0, 90, 180, 270).</param>
public sealed record VideoStreamInfo(int Index, string Codec, int Width, int Height, int Rotation, double? FrameRate, string? PixelFormat)
{
    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;
    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;
}

public sealed record AudioStreamInfo(int Index, string Codec, int Channels, int SampleRate);

public sealed record VideoInspection(
    string Container,
    TimeSpan? Duration,
    long SizeBytes,
    VideoStreamInfo Video,
    AudioStreamInfo? Audio,
    int VideoStreamCount,
    int AudioStreamCount,
    int OtherStreamCount);

public sealed record VideoInspectionResult(VideoInspection? Inspection, VideoError Error)
{
    public bool IsSuccess => Error == VideoError.None && Inspection is not null;
    public static VideoInspectionResult Failure(VideoError error) => new(null, error);
}

public sealed record VideoConversionResult(VideoError Error, string? OutputPath = null, bool CleanupFailed = false,
    VideoInspection? Output = null)
{
    public bool IsSuccess => Error == VideoError.None && OutputPath is not null;
}

public enum VideoStage
{
    Converting,
    Verifying,
}

/// <param name="Fraction">0–1 when the source duration is known; null when progress cannot be computed honestly.</param>
public sealed record VideoProgress(VideoStage Stage, TimeSpan Processed, TimeSpan? Total, double? Fraction, TimeSpan Elapsed);

/// <summary>Video-specific safety limits (image limits do not apply to video).</summary>
public static class VideoLimits
{
    public const long MaximumInputBytes = 32L * 1024 * 1024 * 1024;
    public const int MaximumSide = 8192;
    public const long MaximumPixels = 8192L * 4320;
    public const int MaximumStreams = 32;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(12);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaximumRunTime = TimeSpan.FromHours(24);

    /// <summary>Demuxers allowed to open input; this also blocks playlists and other indirection formats.</summary>
    public const string AllowedDemuxers = "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi";

    public static IReadOnlySet<string> AllowedVideoCodecs { get; } =
        new HashSet<string>(["h264", "hevc", "mpeg4", "vp8", "vp9", "av1", "mpeg2video", "mjpeg"], StringComparer.Ordinal);

    public static IReadOnlySet<string> AllowedAudioCodecs { get; } =
        new HashSet<string>(["aac", "mp3", "opus", "vorbis", "flac", "pcm_s16le", "ac3"], StringComparer.Ordinal);
}
