using System.Diagnostics;
using OrbitConvert.App.Services.Media;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.Tests;

/// <summary>Pure tests of the video engine: no FFmpeg process is started.</summary>
public sealed class VideoEngineUnitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "video-unit-" + Guid.NewGuid().ToString("N"));

    public VideoEngineUnitTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Locator_PrefersBundledToolsOverTheDevelopmentOverride()
    {
        var app = Directory.CreateDirectory(Path.Combine(_directory, "app")).FullName;
        var bundled = Directory.CreateDirectory(Path.Combine(app, FfmpegLocator.BundledFolderName)).FullName;
        var development = Directory.CreateDirectory(Path.Combine(_directory, "dev")).FullName;
        foreach (var folder in new[] { bundled, development })
        {
            File.WriteAllBytes(Path.Combine(folder, "ffmpeg.exe"), []);
            File.WriteAllBytes(Path.Combine(folder, "ffprobe.exe"), []);
        }
        var tools = new FfmpegLocator(app, _ => development).Locate();
        Assert.Equal((Path.Combine(bundled, "ffmpeg.exe"), FfmpegSource.Bundled), (tools!.FfmpegPath, tools.Source));

        File.Delete(Path.Combine(bundled, "ffprobe.exe")); // An incomplete bundle is not used.
        Assert.Equal(FfmpegSource.DevelopmentOverride, new FfmpegLocator(app, _ => development).Locate()!.Source);
    }

    [Fact]
    public void Locator_NeverSearchesPathOrResolvesRelativeOverrides()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_directory, "empty")).FullName;
        Assert.Null(new FfmpegLocator(empty, _ => null).Locate());
        Assert.Null(new FfmpegLocator(empty, _ => "relative\\ffmpeg").Locate());
        Assert.Null(new FfmpegLocator(empty, _ => "   ").Locate());
    }

    [Fact]
    public void Capabilities_AreParsedFromVersionAndEncoderListings()
    {
        const string version = "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026\nbuilt with gcc";
        const string encoders = "Encoders:\n V..... = Video\n A..... = Audio\n ------\n V....D libx264              libx264 H.264\n" +
            " V....D h264_mf              H264 via MediaFoundation\n A....D aac                  AAC (Advanced Audio Coding)\n";
        var all = FfmpegLocator.ParseCapabilities(version, encoders);
        Assert.Equal("9.0.2-essentials_build-www.gyan.dev", all.Version);
        Assert.Equal((true, true, true, H264Encoder.LibX264), (all.HasLibX264, all.HasMediaFoundationH264, all.HasAacEncoder, all.PreferredH264Encoder));

        var lgplStyle = FfmpegLocator.ParseCapabilities(version, encoders.Replace(" V....D libx264              libx264 H.264\n", ""));
        Assert.Equal(H264Encoder.MediaFoundation, lgplStyle.PreferredH264Encoder);
        Assert.True(lgplStyle.CanConvertToMp4);

        var noAac = FfmpegLocator.ParseCapabilities(version, encoders.Replace("aac  ", "opus "));
        Assert.False(noAac.CanConvertToMp4);
        Assert.False(FfmpegLocator.ParseCapabilities(version, "").CanConvertToMp4);
    }

    private static string Probe(string streams, string format = """{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"3.000000"}""") =>
        $$"""{"streams":[{{streams}}],"format":{{format}}}""";

    private const string Video = """{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30000/1001","pix_fmt":"yuv420p"}""";

    [Fact]
    public void Parser_SelectsDefaultAudioSkipsCoverArtAndReadsRotation()
    {
        var json = Probe(
            """{"index":0,"codec_type":"video","codec_name":"mjpeg","width":300,"height":300,"disposition":{"attached_pic":1}},""" +
            """{"index":1,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"side_data_list":[{"rotation":-90}]},""" +
            """{"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","disposition":{"default":0}},""" +
            """{"index":3,"codec_type":"audio","codec_name":"opus","channels":6,"disposition":{"default":1}},""" +
            """{"index":4,"codec_type":"subtitle","codec_name":"subrip"}""");
        var result = VideoProbeParser.Parse(json, 1234);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var video = result.Inspection!;
        Assert.Equal((1, 90, 1080, 1920), (video.Video.Index, video.Video.Rotation, video.Video.DisplayWidth, video.Video.DisplayHeight));
        Assert.Equal((3, "opus", 6), (video.Audio!.Index, video.Audio.Codec, video.Audio.Channels));
        Assert.Equal((1, 2, 2), (video.VideoStreamCount, video.AudioStreamCount, video.OtherStreamCount));
        Assert.Equal(TimeSpan.FromSeconds(3), video.Duration);
    }

    [Theory]
    [InlineData("""{"index":0,"codec_type":"video","codec_name":"ffv1","width":64,"height":48}""", VideoError.UnsupportedCodec)]
    [InlineData("""{"index":0,"codec_type":"audio","codec_name":"aac","channels":2}""", VideoError.NoVideoStream)]
    [InlineData(Video + """,{"index":1,"codec_type":"audio","codec_name":"dts","channels":6}""", VideoError.UnsupportedCodec)]
    [InlineData("""{"index":0,"codec_type":"video","codec_name":"h264","width":8193,"height":1080}""", VideoError.TooLarge)]
    [InlineData("""{"index":0,"codec_type":"video","codec_name":"h264","width":8192,"height":8192}""", VideoError.TooLarge)]
    [InlineData("""{"index":0,"codec_type":"video","codec_name":"h264","width":0,"height":1080}""", VideoError.InvalidOrCorruptVideo)]
    public void Parser_RejectsUnsupportedOrUnsafeStreams(string streams, VideoError expected) =>
        Assert.Equal(expected, VideoProbeParser.Parse(Probe(streams), 10).Error);

    [Theory]
    [InlineData("""{"format_name":"hls","duration":"3"}""", VideoError.UnsupportedFormat)]
    [InlineData("""{"format_name":"image2"}""", VideoError.UnsupportedFormat)]
    [InlineData("""{"format_name":"matroska,webm","duration":"43201"}""", VideoError.TooLarge)]
    public void Parser_EnforcesContainerAndDurationLimits(string format, VideoError expected) =>
        Assert.Equal(expected, VideoProbeParser.Parse(Probe(Video, format), 10).Error);

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"streams":{},"format":{}}""")]
    public void Parser_MalformedJsonIsCorruptInput(string json) =>
        Assert.Equal(VideoError.InvalidOrCorruptVideo, VideoProbeParser.Parse(json, 10).Error);

    [Fact]
    public void Parser_RejectsTooManyStreamsAndUnknownDuration()
    {
        var many = string.Join(",", Enumerable.Range(0, VideoLimits.MaximumStreams + 1).Select(i => $$"""{"index":{{i}},"codec_type":"data"}"""));
        Assert.Equal(VideoError.TooLarge, VideoProbeParser.Parse(Probe(many), 10).Error);
        var unknown = VideoProbeParser.Parse(Probe(Video, """{"format_name":"avi","duration":"N/A"}"""), 10);
        Assert.True(unknown.IsSuccess);
        Assert.Null(unknown.Inspection!.Duration); // Progress then stays indeterminate instead of being invented.
    }

    private static VideoInspection Inspection(AudioStreamInfo? audio = null, int width = 1920, int height = 1080, int rotation = 0) =>
        new("mov,mp4,m4a,3gp,3g2,mj2", TimeSpan.FromSeconds(10), 1000, new(0, "h264", width, height, rotation, 30, "yuv420p"),
            audio, 1, audio is null ? 0 : 1, 0);

    [Fact]
    public void ConvertArguments_UseFileUrlsWhitelistsExplicitMapsAndOverwriteOnlyTheOwnedTemporaryFile()
    {
        var source = @"C:\بيانات\-rf & %PATH% 'clip'.mkv";
        var temporary = @"C:\out\.orbitconvert-0123.tmp";
        var arguments = FfmpegArguments.Convert(source, temporary, Inspection(new(3, "opus", 2, 48000)), new(VideoQuality.High, 720), H264Encoder.LibX264).ToList();

        Assert.Contains("file:" + source, arguments); // One element, never split or interpreted.
        Assert.Equal("file:" + temporary, arguments[^1]);
        Assert.Equal(arguments.IndexOf("-y") + 1, arguments.Count - 1); // -y applies only to the owned temporary output.
        Assert.Equal("file", arguments[arguments.IndexOf("-protocol_whitelist") + 1]);
        Assert.Equal(VideoLimits.AllowedDemuxers, arguments[arguments.IndexOf("-format_whitelist") + 1]);
        Assert.Equal(["0:0", "0:3"], arguments.Select((value, i) => (value, i)).Where(p => p.value == "-map").Select(p => arguments[p.i + 1]));
        Assert.Equal("scale=1280:720:flags=bicubic", arguments[arguments.IndexOf("-vf") + 1]);
        Assert.Equal("18", arguments[arguments.IndexOf("-crf") + 1]);
        Assert.Equal("192k", arguments[arguments.IndexOf("-b:a") + 1]);
        Assert.Equal("mp4", arguments[arguments.IndexOf("-f") + 1]);
        Assert.Contains("-nostdin", arguments);
        Assert.DoesNotContain(arguments, value => value.Contains("cmd", StringComparison.OrdinalIgnoreCase) || value.Contains("powershell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConvertArguments_WithoutAudioMapOnlyVideoAndMediaFoundationUsesQualityMode()
    {
        var arguments = FfmpegArguments.Convert(@"C:\a.mp4", @"C:\b.tmp", Inspection(), new(VideoQuality.Small), H264Encoder.MediaFoundation).ToList();
        Assert.Single(arguments, value => value == "-map");
        Assert.DoesNotContain("-c:a", arguments);
        Assert.Equal(["h264_mf", "quality", "45"],
            [arguments[arguments.IndexOf("-c:v") + 1], arguments[arguments.IndexOf("-rate_control") + 1], arguments[arguments.IndexOf("-quality") + 1]]);
    }

    [Fact]
    public void ProbeAndDecodeArguments_AreRestrictedToLocalFiles()
    {
        var probe = FfmpegArguments.Probe(@"C:\v.mp4").ToList();
        Assert.Equal(["file", VideoLimits.AllowedDemuxers, "file:C:\\v.mp4"],
            [probe[probe.IndexOf("-protocol_whitelist") + 1], probe[probe.IndexOf("-format_whitelist") + 1], probe[^1]]);
        var decode = FfmpegArguments.DecodeCheck(@"C:\t.tmp").ToList();
        Assert.Contains("-xerror", decode);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", decode[decode.IndexOf("-format_whitelist") + 1]);
    }

    [Theory]
    [InlineData(1920, 1080, 0, 1920, 1080)]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1920, 1080, 480, 854, 480)]
    [InlineData(640, 360, 1080, 640, 360)] // Never upscaled.
    [InlineData(1081, 1921, 1080, 608, 1080)] // Portrait.
    [InlineData(321, 241, 0, 320, 240)] // Even sizes for 4:2:0.
    public void OutputSize_PreservesAspectAndOnlyScalesDown(int width, int height, int maxHeight, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), new VideoConversionOptions(MaxHeight: maxHeight).OutputSize(width, height));

    [Fact]
    public void Options_RejectUnknownValues()
    {
        Assert.False(new VideoConversionOptions(MaxHeight: 333).IsValid);
        Assert.False(new VideoConversionOptions((VideoQuality)42).IsValid);
        Assert.True(new VideoConversionOptions(VideoQuality.Small, 480).IsValid);
    }

    [Fact]
    public void ProgressSink_ReportsRealFractionsAndStaysIndeterminateWithoutDuration()
    {
        var reports = new List<VideoProgress>();
        var sink = VideoConversionService.ProgressSink(VideoStage.Converting, TimeSpan.FromSeconds(10), Stopwatch.StartNew(), new SynchronousProgress(reports.Add));
        foreach (var line in new[] { "frame=1", "out_time_us=2500000", "progress=continue", "out_time_us=N/A", "progress=continue",
            "out_time_us=12000000", "progress=continue", "progress=end" })
        {
            sink(line);
        }
        Assert.Equal([0.25, 0.25, 0.999, 1.0], reports.Select(report => report.Fraction!.Value));

        var unknown = new List<VideoProgress>();
        var unknownSink = VideoConversionService.ProgressSink(VideoStage.Converting, null, Stopwatch.StartNew(), new SynchronousProgress(unknown.Add));
        unknownSink("out_time_us=5000000");
        unknownSink("progress=continue");
        Assert.Null(Assert.Single(unknown).Fraction);
        Assert.Equal(TimeSpan.FromSeconds(5), unknown[0].Processed);
    }

    [Fact]
    public async Task MissingTools_ReportToolsUnavailableWithoutStartingAnything()
    {
        var service = new VideoConversionService(new FfmpegLocator(_directory, _ => null), new ThrowingRunner());
        var source = Path.Combine(_directory, "in.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        Assert.Equal(VideoError.ToolsUnavailable, (await service.InspectAsync(source)).Error);
        Assert.Equal(VideoError.ToolsUnavailable, (await service.ConvertAsync(source, Path.Combine(_directory, "out.mp4"), new())).Error);
        Assert.False(File.Exists(Path.Combine(_directory, "out.mp4")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedTermination_IsNotReportedAsCleanCancellation(bool duringConversion)
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "ffmpeg.exe"), []);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "ffprobe.exe"), []);
        var source = Path.Combine(_directory, "in.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        var destination = Path.Combine(_directory, "out.mp4");
        var service = new VideoConversionService(new FfmpegLocator(_directory, _ => _directory), new UnconfirmedRunner(duringConversion));
        if (!duringConversion)
        {
            Assert.Equal(VideoError.ProcessTerminationFailed, (await service.InspectAsync(source)).Error);
        }
        var result = await service.ConvertAsync(source, destination, new());
        Assert.Equal(VideoError.ProcessTerminationFailed, result.Error);
        Assert.True(result.CleanupFailed);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    private sealed class UnconfirmedRunner(bool duringConversion) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            var probing = request.Arguments.Contains("-show_streams");
            if (request.Arguments.Contains("-movflags") || (probing && !duringConversion))
            {
                return Task.FromResult(new ProcessRunResult(ProcessOutcome.Cancelled, -1, "", "", TerminationConfirmed: false));
            }
            var text = probing ? Probe(Video) : request.Arguments.Contains("-encoders")
                ? " V....D libx264 encoder\n A....D aac encoder" : "ffmpeg version test";
            return Task.FromResult(new ProcessRunResult(ProcessOutcome.Exited, 0, text, ""));
        }
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No process may start when the tools are missing.");
    }
}
