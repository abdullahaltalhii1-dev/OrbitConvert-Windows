using System.Diagnostics;
using System.Security.Cryptography;
using OrbitConvert.App.Services.Media;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.Tests;

/// <summary>End-to-end tests that run the real pinned FFmpeg/FFprobe build on deterministic generated media.</summary>
public sealed class VideoConversionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "video-" + Guid.NewGuid().ToString("N"));

    public VideoConversionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [FfmpegFact]
    public async Task Capabilities_ReportPinnedBuildWithH264AndAacEncoders()
    {
        var tools = FfmpegTestTools.Locator().Locate();
        Assert.NotNull(tools);
        Assert.Equal(FfmpegSource.DevelopmentOverride, tools.Source);
        var capabilities = await FfmpegLocator.ProbeAsync(tools, new ProcessRunner(), CancellationToken.None);
        Assert.NotNull(capabilities);
        Assert.StartsWith("9.0.2", capabilities.Version, StringComparison.Ordinal);
        Assert.True(capabilities.HasLibX264);
        Assert.True(capabilities.HasMediaFoundationH264);
        Assert.True(capabilities.HasAacEncoder);
        Assert.Equal(H264Encoder.LibX264, capabilities.PreferredH264Encoder);
    }

    [FfmpegFact]
    public async Task Inspect_ReportsActualStreamsDurationAndDimensions()
    {
        var result = await FfmpegTestTools.Service().InspectAsync(VideoFixtures.Standard());
        Assert.True(result.IsSuccess, result.Error.ToString());
        var video = result.Inspection!;
        Assert.Contains("mp4", video.Container, StringComparison.Ordinal);
        Assert.Equal("h264", video.Video.Codec);
        Assert.Equal((320, 240), (video.Video.Width, video.Video.Height));
        Assert.Equal("aac", video.Audio!.Codec);
        Assert.InRange(video.Duration!.Value.TotalSeconds, 2.9, 3.1);
        Assert.Equal((1, 1, 0), (video.VideoStreamCount, video.AudioStreamCount, video.OtherStreamCount));
    }

    public static IEnumerable<object[]> InputFormats =>
    [
        ["mp4 h264+aac", "صورة متحركة.mp4", "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac", true],
        ["mov h264+aac", "clip (نسخة) & 'final'.mov", "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac", true],
        ["mkv hevc+opus", "hevc 视频.mkv", "-c:v libx265 -preset ultrafast -x265-params log-level=error -c:a libopus", true],
        ["webm vp9+opus", "vp9.webm", "-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -c:a libopus", true],
        ["webm vp8+vorbis", "vp8.webm", "-c:v libvpx -deadline realtime -cpu-used 8 -c:a libvorbis", true],
        ["avi mpeg4+mp3", "mpeg4.avi", "-c:v mpeg4 -q:v 5 -c:a libmp3lame", true],
        ["mkv av1+flac", "av1.mkv", "-c:v libaom-av1 -cpu-used 8 -row-mt 1 -crf 50 -c:a flac", true],
        ["mkv mpeg2+ac3", "mpeg2.mkv", "-c:v mpeg2video -q:v 5 -c:a ac3", true],
        ["avi mjpeg+pcm", "mjpeg.avi", "-c:v mjpeg -q:v 5 -c:a pcm_s16le", true],
        ["mp4 h264 no audio", "silent فيديو.mp4", "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -an", false],
    ];

    [FfmpegTheory]
    [MemberData(nameof(InputFormats))]
    public async Task Convert_SupportedInputs_ProduceValidatedMp4AndKeepOriginal(string label, string fileName, string encode, bool hasAudio)
    {
        var fixture = VideoFixtures.Get(label.Replace(' ', '-').Replace('+', '-') + Path.GetExtension(fileName),
            "-f lavfi -i testsrc2=size=160x120:rate=25:duration=2 -f lavfi -i sine=frequency=330:duration=2", encode + " -shortest");
        var folder = Path.Combine(_directory, "مجلد الإدخال", "nested & more");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, fileName);
        File.Copy(fixture, source);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var destination = Path.Combine(folder, "مخرج " + label.Replace('+', '_') + ".mp4");

        var result = await FfmpegTestTools.Service().ConvertAsync(source, destination, new VideoConversionOptions());

        Assert.True(result.IsSuccess, $"{label}: {result.Error}");
        Assert.Equal(destination, result.OutputPath);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        var output = await FfmpegTestTools.Service().InspectAsync(destination);
        Assert.True(output.IsSuccess, output.Error.ToString());
        Assert.Equal("h264", output.Inspection!.Video.Codec);
        Assert.Equal((160, 120), (output.Inspection.Video.Width, output.Inspection.Video.Height));
        Assert.Equal(hasAudio, output.Inspection.Audio is { Codec: "aac" });
        Assert.InRange(output.Inspection.Duration!.Value.TotalSeconds, 1.8, 2.2);
        Assert.Empty(Directory.GetFiles(folder, ".orbitconvert-*"));
    }

    [FfmpegTheory]
    [InlineData(H264Encoder.LibX264)]
    [InlineData(H264Encoder.MediaFoundation)]
    public async Task QualityPresets_TradeSizeForMeasuredQuality(H264Encoder encoder)
    {
        var source = VideoFixtures.Get("quality-640x360.mp4", "-f lavfi -i testsrc2=size=640x360:rate=25:duration=3",
            "-c:v libx264 -preset ultrafast -qp 0 -pix_fmt yuv420p");
        var service = FfmpegTestTools.Service(encoder);
        var measured = new List<(long Size, double Ssim)>();
        foreach (var quality in new[] { VideoQuality.High, VideoQuality.Balanced, VideoQuality.Small })
        {
            var destination = Path.Combine(_directory, $"{encoder}-{quality}.mp4");
            var result = await service.ConvertAsync(source, destination, new VideoConversionOptions(quality));
            Assert.True(result.IsSuccess, $"{encoder}/{quality}: {result.Error}");
            measured.Add((new FileInfo(destination).Length, VideoFixtures.Ssim(destination, source)));
        }
        Assert.True(measured[0].Size > measured[1].Size && measured[1].Size > measured[2].Size, string.Join(", ", measured));
        Assert.True(measured[0].Ssim >= measured[1].Ssim && measured[1].Ssim >= measured[2].Ssim, string.Join(", ", measured));
        Assert.True(measured[0].Ssim > 0.95, string.Join(", ", measured));
    }

    [FfmpegTheory]
    [InlineData("1280x720", 480, 854, 480)]
    [InlineData("1280x720", 1080, 1280, 720)] // Never upscaled.
    [InlineData("321x241", 0, 320, 240)] // Odd sizes become even for 4:2:0 H.264.
    public async Task Resolution_PreservesAspectAndOnlyScalesDown(string size, int maxHeight, int width, int height)
    {
        var source = VideoFixtures.Get($"size-{size}.mp4", $"-f lavfi -i testsrc2=size={size}:rate=25:duration=1", "-c:v libx264 -preset ultrafast -pix_fmt yuv444p");
        var destination = Path.Combine(_directory, $"size-{size}-{maxHeight}.mp4");
        var result = await FfmpegTestTools.Service().ConvertAsync(source, destination, new VideoConversionOptions(MaxHeight: maxHeight));
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal((width, height), (result.Output!.Video.Width, result.Output.Video.Height));
    }

    [FfmpegFact]
    public async Task Rotation_IsAppliedToPixelsAndRemovedFromOutput()
    {
        var plain = VideoFixtures.Get("landscape-160x90.mp4", "-f lavfi -i testsrc2=size=160x90:rate=25:duration=1", "-c:v libx264 -preset ultrafast");
        var rotated = Path.Combine(_directory, "rotated.mp4");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-hide_banner", "-v", "error", "-display_rotation:v:0", "90", "-i", plain, "-c", "copy", rotated]);
        var service = FfmpegTestTools.Service();
        var inspected = await service.InspectAsync(rotated);
        Assert.True(inspected.IsSuccess, inspected.Error.ToString());
        Assert.NotEqual(0, inspected.Inspection!.Video.Rotation);
        Assert.Equal((90, 160), (inspected.Inspection.Video.DisplayWidth, inspected.Inspection.Video.DisplayHeight));
        var result = await service.ConvertAsync(rotated, Path.Combine(_directory, "upright.mp4"), new VideoConversionOptions());
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal((90, 160, 0), (result.Output!.Video.Width, result.Output.Video.Height, result.Output.Video.Rotation));
    }

    [FfmpegFact]
    public async Task StreamSelection_PrefersDefaultAudioAndIgnoresCoverArtAndSubtitles()
    {
        var subtitles = Path.Combine(_directory, "subs.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nمرحبا\n");
        var cover = Path.Combine(_directory, "cover.png");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1", cover]);
        var source = Path.Combine(_directory, "multi.mkv");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg,
        [
            "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=300:duration=2", "-f", "lavfi", "-i", "sine=frequency=900:duration=2", "-i", subtitles,
            "-attach", cover, "-metadata:s:t", "mimetype=image/png",
            "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "libopus", "-c:s", "srt",
            "-disposition:a:0", "0", "-disposition:a:1", "default", source,
        ]);
        var service = FfmpegTestTools.Service();
        var inspected = await service.InspectAsync(source);
        Assert.True(inspected.IsSuccess, inspected.Error.ToString());
        Assert.Equal(2, inspected.Inspection!.Audio!.Index); // The second audio stream carries the default disposition.
        Assert.Equal((1, 2), (inspected.Inspection.VideoStreamCount, inspected.Inspection.AudioStreamCount));
        Assert.True(inspected.Inspection.OtherStreamCount >= 2); // Subtitle + attachment.
        var result = await service.ConvertAsync(source, Path.Combine(_directory, "multi.mp4"), new VideoConversionOptions());
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal((1, 1, 0), (result.Output!.VideoStreamCount, result.Output.AudioStreamCount, result.Output.OtherStreamCount));
    }

    [FfmpegFact]
    public async Task Progress_IsRealMonotonicAndCompletesBothStages()
    {
        var reports = new List<VideoProgress>();
        var progress = new SynchronousProgress(reports.Add);
        var result = await FfmpegTestTools.Service().ConvertAsync(VideoFixtures.Standard(4), Path.Combine(_directory, "progress.mp4"),
            new VideoConversionOptions(), progress);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var converting = reports.Where(r => r.Stage == VideoStage.Converting).ToList();
        var verifying = reports.Where(r => r.Stage == VideoStage.Verifying).ToList();
        Assert.NotEmpty(converting);
        Assert.NotEmpty(verifying);
        Assert.Equal(1.0, converting[^1].Fraction);
        Assert.Equal(1.0, verifying[^1].Fraction);
        Assert.All(converting.Zip(converting.Skip(1)), pair => Assert.True(pair.Second.Fraction >= pair.First.Fraction));
        Assert.All(converting, r => Assert.Equal(TimeSpan.FromSeconds(4).TotalSeconds, r.Total!.Value.TotalSeconds, 1));
    }

    [FfmpegFact]
    public async Task Cancel_StopsFfmpegAndLeavesNoOutputOrTemporaryFile()
    {
        var source = VideoFixtures.Get("long-1280x720-20s.mp4", "-f lavfi -i testsrc2=size=1280x720:rate=30:duration=20 -f lavfi -i sine=duration=20",
            "-c:v libx264 -preset ultrafast -c:a aac");
        var destination = Path.Combine(_directory, "cancelled.mp4");
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report => { if (report.Fraction > 0.05) { cancellation.Cancel(); } });
        // Observe only this operation's encoder. System-wide counts race with parallel tests and user processes.
        Process? encoder = null;
        var runner = new ProcessRunner(process =>
        {
            var started = process.Start();
            if (started && process.StartInfo.ArgumentList.Contains("-movflags"))
            {
                encoder = Process.GetProcessById(process.Id);
                encoder.EnableRaisingEvents = true; // Retain the handle even after the runner disposes its Process.
            }
            return started;
        });
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await FfmpegTestTools.Service(runner: runner).ConvertAsync(source, destination, new VideoConversionOptions(VideoQuality.High), progress, cancellation.Token);
            Assert.Equal(VideoError.Cancelled, result.Error);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Cancellation took {clock.Elapsed}");
            Assert.NotNull(encoder);
            Assert.True(encoder.HasExited, $"Owned encoder {encoder.Id} must exit before returning.");
            Assert.False(result.CleanupFailed);
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
        }
        finally { encoder?.Dispose(); }
    }

    public static IEnumerable<object[]> InvalidInputs =>
    [
        ["truncated", VideoError.InvalidOrCorruptVideo],
        ["random", VideoError.InvalidOrCorruptVideo],
        ["text", VideoError.InvalidOrCorruptVideo],
        ["empty", VideoError.EmptyInput],
        ["png", VideoError.UnsupportedFormat],
        // An extension-disguised playlist may fail probing before demuxer identification. Both classifications
        // are safe; the pinned build reports invalid data rather than a whitelist diagnostic for this fixture.
        ["playlist", VideoError.InvalidOrCorruptVideo],
        ["audio-only", VideoError.NoVideoStream],
        ["unsupported-codec", VideoError.UnsupportedCodec],
    ];

    [FfmpegTheory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidOrUnsupportedInput_IsRejectedWithoutCreatingFiles(string kind, VideoError expected)
    {
        var source = Path.Combine(_directory, kind + ".mp4");
        switch (kind)
        {
            case "truncated":
                var standard = await File.ReadAllBytesAsync(VideoFixtures.Standard());
                await File.WriteAllBytesAsync(source, standard[..(standard.Length / 3)]);
                break;
            case "random": await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(64 * 1024)); break;
            case "text": await File.WriteAllTextAsync(source, "this is not a video"); break;
            case "empty": await File.WriteAllBytesAsync(source, []); break;
            case "png":
                VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:size=32x32", "-frames:v", "1", "-f", "image2", "-c:v", "png", source]);
                break;
            case "playlist":
                await File.WriteAllTextAsync(source, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nhttp://127.0.0.1:9/segment.ts\n#EXT-X-ENDLIST\n");
                break;
            case "audio-only":
                File.Copy(VideoFixtures.Get("audio-only.m4a", "-f lavfi -i sine=duration=1", "-c:a aac"), source);
                break;
            case "unsupported-codec":
                File.Copy(VideoFixtures.Get("ffv1.mkv", "-f lavfi -i testsrc2=size=64x48:duration=1", "-c:v ffv1"), source);
                break;
        }
        var service = FfmpegTestTools.Service();
        Assert.Equal(expected, (await service.InspectAsync(source)).Error);
        var destination = Path.Combine(_directory, kind + "-out.mp4");
        Assert.Equal(expected, (await service.ConvertAsync(source, destination, new VideoConversionOptions())).Error);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task FileSafety_MissingSourceExistingDestinationSamePathAndExtension()
    {
        var service = FfmpegTestTools.Service();
        var source = Path.Combine(_directory, "source.mp4");
        File.Copy(VideoFixtures.Standard(), source);
        Assert.Equal(VideoError.UnreadableInput, (await service.ConvertAsync(Path.Combine(_directory, "missing.mp4"), Path.Combine(_directory, "x.mp4"), new())).Error);
        var existing = Path.Combine(_directory, "existing.mp4");
        await File.WriteAllBytesAsync(existing, [1, 2, 3]);
        Assert.Equal(VideoError.DestinationExists, (await service.ConvertAsync(source, existing, new())).Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(existing));
        Assert.Equal(VideoError.SamePath, (await service.ConvertAsync(source, source, new())).Error);
        Assert.Equal(VideoError.InvalidDestination, (await service.ConvertAsync(source, Path.Combine(_directory, "out.mkv"), new())).Error);
        Assert.Equal(VideoError.InvalidSettings, (await service.ConvertAsync(source, Path.Combine(_directory, "out.mp4"), new(MaxHeight: 333))).Error);
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task ConcurrentRequestsForOneDestination_ExactlyOneWins()
    {
        var service = FfmpegTestTools.Service();
        var destination = Path.Combine(_directory, "shared.mp4");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            service.ConvertAsync(VideoFixtures.Standard(), destination, new VideoConversionOptions(VideoQuality.Small))));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(2, results.Count(result => result.Error == VideoError.DestinationExists));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task OutputValidation_RejectsAProcessThatExitsCleanlyWithInvalidOutput()
    {
        var destination = Path.Combine(_directory, "invalid.mp4");
        var service = FfmpegTestTools.Service(runner: new CorruptingRunner(new ProcessRunner()));
        var result = await service.ConvertAsync(VideoFixtures.Standard(), destination, new VideoConversionOptions());
        Assert.Equal(VideoError.OutputValidationFailed, result.Error);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    /// <summary>Lets the real encoder run, then truncates its output, so only output validation can catch the damage.</summary>
    private sealed class CorruptingRunner(IProcessRunner inner) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(request, cancellationToken);
            if (request.Arguments.Contains("-movflags"))
            {
                var output = request.Arguments[^1]["file:".Length..];
                var bytes = await File.ReadAllBytesAsync(output, cancellationToken);
                await File.WriteAllBytesAsync(output, bytes[..(bytes.Length / 2)], cancellationToken);
            }
            return result;
        }
    }
}

/// <summary>Reports on the calling thread (Progress&lt;T&gt; posts asynchronously and would race the assertions).</summary>
internal sealed class SynchronousProgress(Action<VideoProgress> report) : IProgress<VideoProgress>
{
    public void Report(VideoProgress value) => report(value);
}
