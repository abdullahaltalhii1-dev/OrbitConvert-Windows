using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class AudioEngineTests
{
    internal const string Stream = """{"index":0,"codec_type":"audio","codec_name":"pcm_s16le","sample_rate":"48000","channels":1,"sample_fmt":"s16","bits_per_sample":16}""";
    internal static string Json(string streams = Stream, string container = "wav", string duration = "2") =>
        $$"""{"format":{"format_name":"{{container}}","duration":"{{duration}}"},"streams":[{{streams}}]}""";

    [Fact]
    public void AudioCapabilities_DoNotRequireAnyVideoEncoder()
    {
        var caps = FfmpegLocator.ParseCapabilities("ffmpeg version test", " A....D libmp3lame encoder\n A....D pcm_s16le encoder\n A....D flac encoder\n");
        Assert.False(caps.CanConvertToMp4);
        Assert.True(caps.HasMp3Encoder && caps.HasPcm16Encoder && caps.HasFlacEncoder);
    }

    [Fact]
    public void Parser_PrefersDefaultTrackAndReportsCoverArtAsUnselected()
    {
        var selected = Stream.Replace("\"index\":0", "\"index\":1").Replace("\"bits_per_sample\":16", "\"bits_per_sample\":16,\"disposition\":{\"default\":1}");
        var cover = """{"index":2,"codec_type":"video","codec_name":"png","disposition":{"attached_pic":1}}""";
        var result = AudioProbeParser.Parse(Json(Stream + "," + selected + "," + cover), 100);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Inspection!.Audio.Index);
        Assert.Equal((2, 0, 1), (result.Inspection.AudioStreamCount, result.Inspection.VideoStreamCount, result.Inspection.OtherStreamCount));
    }

    [Theory]
    [InlineData("hls", AudioError.UnsupportedFormat)]
    [InlineData("concat", AudioError.UnsupportedFormat)]
    [InlineData("png_pipe", AudioError.UnsupportedFormat)]
    public void Parser_RejectsIndirectAndUnsupportedContainers(string container, AudioError expected) =>
        Assert.Equal(expected, AudioProbeParser.Parse(Json(container: container), 100).Error);

    [Theory]
    [InlineData("1e100", AudioError.TooLarge)]
    [InlineData("43201", AudioError.TooLarge)]
    public void Duration_IsBoundedBeforeTimeSpanConversion(string duration, AudioError expected) =>
        Assert.Equal(expected, AudioProbeParser.Parse(Json(duration: duration), 100).Error);

    [Fact]
    public void Parser_UnknownDurationIsHonest_AndSurroundIsNotSilentlyDownmixed()
    {
        Assert.Null(AudioProbeParser.Parse(Json(duration: "N/A"), 100).Inspection!.Duration);
        Assert.Equal(AudioError.UnsupportedChannels, AudioProbeParser.Parse(Json(Stream.Replace("\"channels\":1", "\"channels\":6")), 100).Error);
        Assert.Equal(AudioError.NoAudioStream, AudioProbeParser.Parse(Json(""), 100).Error);
        Assert.Equal(AudioError.InvalidOrCorruptAudio, AudioProbeParser.Parse("{}", 100).Error);
        Assert.Equal(AudioError.UnsupportedCodec, AudioProbeParser.Parse(Json(Stream.Replace("pcm_s16le", "not-a-codec")), 100).Error);
    }

    [Fact]
    public void Options_RejectUnsupportedPrecisionAndMp3RatesRatherThanSilentlyChangingThem()
    {
        var input = AudioProbeParser.Parse(Json(), 100).Inspection!;
        Assert.Equal(AudioError.None, new AudioConversionOptions().Validate(input));
        Assert.False(new AudioConversionOptions(BitrateKbps: 999).IsValid);
        Assert.False(new AudioConversionOptions(SampleRate: 96000).IsValid);
        Assert.Equal(AudioError.InvalidSettings, new AudioConversionOptions().Validate(input with { Audio = input.Audio with { SampleRate = 96000 } }));
        Assert.Equal(AudioError.None, new AudioConversionOptions(SampleRate: 48000).Validate(input with { Audio = input.Audio with { SampleRate = 96000 } }));
        Assert.Equal(AudioError.UnsupportedPrecision, new AudioConversionOptions(AudioFormat.FLAC).Validate(input with { Audio = input.Audio with { BitsPerSample = 32 } }));
    }

    [Fact]
    public void Arguments_MapOnlyTheChosenAudioAndStripSourceMetadata()
    {
        var input = AudioProbeParser.Parse(Json(), 100).Inspection!;
        var args = AudioArguments.Convert(@"C:\صوت & test\source.wav", @"C:\output\owned.tmp", input, new());
        Assert.Equal("0:0", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Equal("file", args[args.ToList().IndexOf("-protocol_whitelist") + 1]);
        Assert.Contains("-vn", args);
        Assert.Contains("-map_metadata", args);
        Assert.Contains("-xerror", args);
        Assert.Equal("file:C:\\output\\owned.tmp", args[^1]);
    }
}
