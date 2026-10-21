namespace OrbitConvert.App.Services.Audio;

public enum AudioFormat { MP3, WAV, FLAC }
public enum AudioStage { Encoding, Verifying }
public enum AudioError
{
    None, ToolsUnavailable, ToolsUnsupported, EmptyInput, UnreadableInput, UnsupportedFormat, UnsupportedCodec,
    NoAudioStream, UnsupportedChannels, UnsupportedPrecision, InvalidOrCorruptAudio, TooLarge, InvalidSettings,
    SamePath, DestinationExists, InvalidDestination, ConversionFailed, TimedOut, Cancelled, OutputFailure,
    OutputValidationFailed, ProcessTerminationFailed,
}

public sealed record AudioConversionOptions(AudioFormat Format = AudioFormat.MP3, int BitrateKbps = 192,
    int SampleRate = 0, int FlacCompression = 5)
{
    public static IReadOnlyList<int> Bitrates { get; } = [128, 192, 320];
    public static IReadOnlyList<int> SampleRates { get; } = [0, 44100, 48000];
    public bool IsValid => Enum.IsDefined(Format) && Bitrates.Contains(BitrateKbps) && SampleRates.Contains(SampleRate) && FlacCompression is >= 0 and <= 8;
    public string Extension => "." + Format.ToString().ToLowerInvariant();
    public string Codec => Format switch { AudioFormat.MP3 => "mp3", AudioFormat.WAV => "pcm_s16le", _ => "flac" };
    public int OutputRate(AudioInspection input) => SampleRate == 0 ? input.Audio.SampleRate : SampleRate;
    public int OutputBits(AudioInspection input) => Format == AudioFormat.WAV ? 16 : input.Audio.BitsPerSample is > 0 and <= 16 ? 16 : 24;
    public AudioError Validate(AudioInspection input)
    {
        if (!IsValid) { return AudioError.InvalidSettings; }
        var rate = OutputRate(input);
        // Only rates accepted by the verified libmp3lame encoder. Never silently resample an unsupported rate.
        if (Format == AudioFormat.MP3 && rate is not (32000 or 44100 or 48000))
        {
            return AudioError.InvalidSettings;
        }
        if (Format == AudioFormat.FLAC && (input.Audio.BitsPerSample > 24 ||
            (input.Audio.Codec.StartsWith("pcm_", StringComparison.Ordinal) &&
             (input.Audio.SampleFormat.StartsWith("flt", StringComparison.Ordinal) || input.Audio.SampleFormat.StartsWith("dbl", StringComparison.Ordinal)))))
        {
            return AudioError.UnsupportedPrecision;
        }
        return AudioError.None;
    }
}

public sealed record InspectedAudioStream(int Index, string Codec, int Channels, string? ChannelLayout,
    int SampleRate, string SampleFormat, int BitsPerSample, long? Bitrate);
public sealed record AudioInspection(string Container, TimeSpan? Duration, long SizeBytes, InspectedAudioStream Audio,
    int AudioStreamCount, int VideoStreamCount, int OtherStreamCount);
public sealed record AudioInspectionResult(AudioInspection? Inspection, AudioError Error)
{
    public bool IsSuccess => Error == AudioError.None && Inspection is not null;
    public static AudioInspectionResult Failure(AudioError error) => new(null, error);
}
public sealed record AudioConversionResult(AudioError Error, string? OutputPath = null, bool CleanupFailed = false, AudioInspection? Output = null)
{
    public bool IsSuccess => Error == AudioError.None && OutputPath is not null;
}
public sealed record AudioProgress(AudioStage Stage, double? Fraction, TimeSpan Processed, TimeSpan Elapsed);

public static class AudioLimits
{
    public const long MaximumInputBytes = 32L * 1024 * 1024 * 1024;
    public const int MaximumStreams = 32;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(12);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaximumRunTime = TimeSpan.FromHours(24);
    public const string AudioDemuxers = "mp3,wav,flac,aac,ogg";
    public const string InputDemuxers = AudioDemuxers + ",mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi";
    public static IReadOnlySet<string> Codecs { get; } = new HashSet<string>(
        ["mp3", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le", "flac", "aac", "vorbis", "opus", "ac3"], StringComparer.Ordinal);
}
