using System.Globalization;

namespace OrbitConvert.App.Services.Audio;

internal static class AudioArguments
{
    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);
    public static IReadOnlyList<string> Probe(string path, bool output = false) =>
        ["-hide_banner", "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", output ? "mp3,wav,flac" : AudioLimits.InputDemuxers,
         "-print_format", "json", "-show_format", "-show_streams", "file:" + path];

    public static IReadOnlyList<string> Convert(string source, string temporary, AudioInspection input, AudioConversionOptions options)
    {
        var result = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", "-xerror", "-err_detect", "explode",
            "-protocol_whitelist", "file", "-format_whitelist", AudioLimits.InputDemuxers, "-i", "file:" + source,
            "-map", "0:" + Number(input.Audio.Index), "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-ac", Number(input.Audio.Channels), "-ar", Number(options.OutputRate(input)),
        };
        switch (options.Format)
        {
            case AudioFormat.MP3:
                result.AddRange(["-c:a", "libmp3lame", "-b:a", Number(options.BitrateKbps) + "k", "-id3v2_version", "0", "-write_id3v1", "0"]);
                break;
            case AudioFormat.WAV: result.AddRange(["-c:a", "pcm_s16le", "-fflags", "+bitexact"]); break;
            case AudioFormat.FLAC:
                result.AddRange(["-c:a", "flac", "-compression_level", Number(options.FlacCompression), "-sample_fmt", options.OutputBits(input) == 16 ? "s16" : "s32",
                    "-bits_per_raw_sample", Number(options.OutputBits(input))]);
                break;
        }
        result.AddRange(["-f", options.Format.ToString().ToLowerInvariant(), "-y", "file:" + temporary]);
        return result;
    }

    public static IReadOnlyList<string> Decode(string path) =>
        ["-hide_banner", "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", "-xerror", "-err_detect", "explode",
         "-protocol_whitelist", "file", "-format_whitelist", "mp3,wav,flac", "-i", "file:" + path, "-map", "0:a:0", "-f", "null", "-"];
}
