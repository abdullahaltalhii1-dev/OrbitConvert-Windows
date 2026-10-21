using System.Globalization;
using System.Text.Json;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.App.Services.Audio;

internal static class AudioProbeParser
{
    public static AudioInspectionResult Parse(string json, long size)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                return AudioInspectionResult.Failure(AudioError.InvalidOrCorruptAudio);
            }
            if (streams.GetArrayLength() > AudioLimits.MaximumStreams) { return AudioInspectionResult.Failure(AudioError.TooLarge); }
            var container = Text(format, "format_name");
            if (container.Length == 0 || !container.Split(',').All(AudioLimits.InputDemuxers.Split(',').Contains))
            {
                return AudioInspectionResult.Failure(AudioError.UnsupportedFormat);
            }
            JsonElement? first = null, preferred = null;
            int audio = 0, video = 0, other = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                if (type == "audio")
                {
                    audio++;
                    first ??= stream;
                    if (preferred is null && Flag(stream, "default")) { preferred = stream; }
                }
                else if (type == "video" && !Flag(stream, "attached_pic"))
                {
                    video++;
                    if (!VideoLimits.AllowedVideoCodecs.Contains(Text(stream, "codec_name"))) { return AudioInspectionResult.Failure(AudioError.UnsupportedCodec); }
                    var width = Number(stream, "width");
                    var height = Number(stream, "height");
                    if (width <= 0 || height <= 0) { return AudioInspectionResult.Failure(AudioError.InvalidOrCorruptAudio); }
                    if (width > VideoLimits.MaximumSide || height > VideoLimits.MaximumSide || width * height > VideoLimits.MaximumPixels)
                    {
                        return AudioInspectionResult.Failure(AudioError.TooLarge);
                    }
                }
                else { other++; } // Cover art is deliberately not selected or copied.
            }
            if ((preferred ?? first) is not { } selected) { return AudioInspectionResult.Failure(AudioError.NoAudioStream); }
            var codec = Text(selected, "codec_name");
            if (!AudioLimits.Codecs.Contains(codec)) { return AudioInspectionResult.Failure(AudioError.UnsupportedCodec); }
            var channels = Number(selected, "channels");
            var layout = Text(selected, "channel_layout");
            if (channels is < 1 or > 2 || (layout.Length > 0 && layout != (channels == 1 ? "mono" : "stereo")))
            {
                return AudioInspectionResult.Failure(AudioError.UnsupportedChannels);
            }
            var rate = Number(selected, "sample_rate");
            if (rate is < 8000 or > 192000) { return AudioInspectionResult.Failure(AudioError.UnsupportedFormat); }
            var seconds = Seconds(Text(format, "duration")) ?? Seconds(Text(selected, "duration"));
            if (seconds > AudioLimits.MaximumDuration.TotalSeconds) { return AudioInspectionResult.Failure(AudioError.TooLarge); }
            TimeSpan? duration = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
            var bits = Math.Max(Number(selected, "bits_per_raw_sample"), Number(selected, "bits_per_sample"));
            if (bits is < 0 or > 64) { return AudioInspectionResult.Failure(AudioError.InvalidOrCorruptAudio); }
            var bitrate = Number(selected, "bit_rate");
            var index = Number(selected, "index");
            if (index is < 0 or >= AudioLimits.MaximumStreams) { return AudioInspectionResult.Failure(AudioError.InvalidOrCorruptAudio); }
            var info = new InspectedAudioStream((int)index, codec, (int)channels, layout.Length == 0 ? null : layout,
                (int)rate, Text(selected, "sample_fmt"), (int)bits, bitrate > 0 ? bitrate : null);
            return new(new(container, duration, size, info, audio, video, other), AudioError.None);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return AudioInspectionResult.Failure(AudioError.InvalidOrCorruptAudio);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric)) ||
         (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))) ? numeric : 0;
    private static bool Flag(JsonElement stream, string name) => stream.TryGetProperty("disposition", out var flags) && Number(flags, name) == 1;
    private static double? Seconds(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) && seconds > 0 ? seconds : null;
}
