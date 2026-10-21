using System.Globalization;
using System.Text.Json;

namespace OrbitConvert.App.Services.Video;

/// <summary>Interprets <c>ffprobe -print_format json -show_format -show_streams</c> output and applies the video limits.</summary>
internal static class VideoProbeParser
{
    public static VideoInspectionResult Parse(string json, long sizeBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || !root.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                return VideoInspectionResult.Failure(VideoError.InvalidOrCorruptVideo);
            }
            if (streams.GetArrayLength() > VideoLimits.MaximumStreams) { return VideoInspectionResult.Failure(VideoError.TooLarge); }

            var container = Text(format, "format_name") ?? string.Empty;
            if (!container.Split(',').All(VideoLimits.AllowedDemuxers.Split(',').Contains))
            {
                return VideoInspectionResult.Failure(VideoError.UnsupportedFormat);
            }

            VideoStreamInfo? video = null;
            AudioStreamInfo? audio = null, defaultAudio = null;
            int videoCount = 0, audioCount = 0, otherCount = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                var attachedPicture = Disposition(stream, "attached_pic");
                if (type == "video" && !attachedPicture)
                {
                    videoCount++;
                    if (video is not null) { continue; }
                    var codec = Text(stream, "codec_name") ?? string.Empty;
                    if (!VideoLimits.AllowedVideoCodecs.Contains(codec)) { return VideoInspectionResult.Failure(VideoError.UnsupportedCodec); }
                    var width = Integer(stream, "width");
                    var height = Integer(stream, "height");
                    if (width <= 0 || height <= 0) { return VideoInspectionResult.Failure(VideoError.InvalidOrCorruptVideo); }
                    if (width > VideoLimits.MaximumSide || height > VideoLimits.MaximumSide || (long)width * height > VideoLimits.MaximumPixels)
                    {
                        return VideoInspectionResult.Failure(VideoError.TooLarge);
                    }
                    video = new(Integer(stream, "index"), codec, width, height, Rotation(stream), FrameRate(Text(stream, "avg_frame_rate")),
                        Text(stream, "pix_fmt"));
                }
                else if (type == "audio")
                {
                    audioCount++;
                    var codec = Text(stream, "codec_name") ?? string.Empty;
                    var candidate = new AudioStreamInfo(Integer(stream, "index"), codec, Integer(stream, "channels"), Integer(stream, "sample_rate"));
                    audio ??= candidate;
                    if (defaultAudio is null && Disposition(stream, "default")) { defaultAudio = candidate; }
                }
                else
                {
                    otherCount++; // Subtitles, data, attachments and cover art are not converted.
                }
            }
            if (video is null) { return VideoInspectionResult.Failure(VideoError.NoVideoStream); }
            var selectedAudio = defaultAudio ?? audio;
            if (selectedAudio is not null && (!VideoLimits.AllowedAudioCodecs.Contains(selectedAudio.Codec) || selectedAudio.Channels <= 0))
            {
                return VideoInspectionResult.Failure(VideoError.UnsupportedCodec);
            }

            TimeSpan? duration = Seconds(Text(format, "duration")) is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
            if (duration > VideoLimits.MaximumDuration) { return VideoInspectionResult.Failure(VideoError.TooLarge); }
            return new(new VideoInspection(container, duration, sizeBytes, video, selectedAudio, videoCount, audioCount, otherCount), VideoError.None);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return VideoInspectionResult.Failure(VideoError.InvalidOrCorruptVideo);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static bool Disposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var disposition) && Integer(disposition, name) == 1;

    private static double? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static double? FrameRate(string? text)
    {
        var parts = text?.Split('/');
        return parts is [var numerator, var denominator] && double.TryParse(numerator, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(denominator, CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0 ? n / d : null;
    }

    /// <summary>Clockwise rotation from the display-matrix side data (ffprobe reports it counter-clockwise).</summary>
    private static int Rotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideData) || sideData.ValueKind != JsonValueKind.Array) { return 0; }
        foreach (var item in sideData.EnumerateArray())
        {
            if (item.TryGetProperty("rotation", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var degrees))
            {
                var normalized = (((int)Math.Round(-degrees) % 360) + 360) % 360;
                return normalized is 90 or 180 or 270 ? normalized : 0;
            }
        }
        return 0;
    }
}
