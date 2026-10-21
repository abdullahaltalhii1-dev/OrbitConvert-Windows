using System.Diagnostics;
using System.Security.Cryptography;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.Services.Media;
using Xunit.Abstractions;

namespace OrbitConvert.Tests;

public sealed class AudioConversionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "audio-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } }
    private static AudioConversionService Service(IProcessRunner? runner = null) => new(FfmpegTestTools.Locator(), runner ?? new ProcessRunner());
    internal static string Source(string name = "pcm.wav", string encode = "-c:a pcm_s16le", int seconds = 2) =>
        VideoFixtures.Get("phase4-" + name, $"-f lavfi -i sine=frequency=997:sample_rate=48000:duration={seconds}", encode);

    public static IEnumerable<object[]> Formats =>
        from source in new[] { ("pcm.wav", "-c:a pcm_s16le"), ("mp3.mp3", "-c:a libmp3lame -b:a 192k"), ("flac.flac", "-c:a flac"),
            ("aac.aac", "-c:a aac"), ("m4a.m4a", "-c:a aac"), ("vorbis.ogg", "-c:a libvorbis") }
        from target in Enum.GetValues<AudioFormat>()
        select new object[] { source.Item1, source.Item2, target };

    [FfmpegTheory]
    [MemberData(nameof(Formats))]
    public async Task Convert_ActualFormatsIncludingSameFormat_AreDecodedAndOriginalsPreserved(string name, string encode, AudioFormat format)
    {
        var source = Path.Combine(_directory, "صوت & نسخة 'تجريبية'" + Path.GetExtension(name));
        File.Copy(Source(name, encode), source);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var options = new AudioConversionOptions(format);
        var destination = Path.Combine(_directory, "ناتج جديد" + options.Extension);
        var clock = Stopwatch.StartNew();
        var result = await Service().ConvertAsync(source, destination, options);
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.False(result.CleanupFailed);
        Assert.Equal(options.Codec, result.Output!.Audio.Codec);
        Assert.Equal(48000, result.Output.Audio.SampleRate);
        Assert.Equal(1, result.Output.Audio.Channels);
        Assert.Equal((1, 0, 0), (result.Output.AudioStreamCount, result.Output.VideoStreamCount, result.Output.OtherStreamCount));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
        output.WriteLine($"{name} (2 s, 48 kHz) -> {format}, {options.BitrateKbps} kbps MP3 / FLAC level {options.FlacCompression}: {clock.Elapsed.TotalSeconds:F3} s including validation; {new FileInfo(source).Length} -> {new FileInfo(destination).Length} bytes; {result.Output.Audio.Codec} decoded and verified.");
    }

    [FfmpegTheory]
    [InlineData(128, 44100)]
    [InlineData(192, 48000)]
    [InlineData(320, 0)]
    public async Task Mp3BitrateAndSampleRate_AreVerified(int bitrate, int rate)
    {
        var result = await Service().ConvertAsync(Source(), Path.Combine(_directory, "rate.mp3"), new(BitrateKbps: bitrate, SampleRate: rate));
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(bitrate * 1000, result.Output!.Audio.Bitrate);
        Assert.Equal(rate == 0 ? 48000 : rate, result.Output.Audio.SampleRate);
    }

    [FfmpegTheory]
    [InlineData(AudioFormat.WAV, 16, 5)]
    [InlineData(AudioFormat.FLAC, 16, 0)]
    [InlineData(AudioFormat.FLAC, 16, 8)]
    [InlineData(AudioFormat.FLAC, 24, 5)]
    public async Task LosslessPcmFidelity_IsByteExactAfterDecode(AudioFormat format, int bits, int level)
    {
        var source = Source($"pcm-{bits}.wav", $"-c:a pcm_s{bits}le");
        var options = new AudioConversionOptions(format, FlacCompression: level);
        var destination = Path.Combine(_directory, "lossless" + options.Extension);
        var result = await Service().ConvertAsync(source, destination, options);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var decoded = Path.Combine(_directory, "decoded.pcm");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-i", destination, "-c:a", $"pcm_s{bits}le", "-f", $"s{bits}le", decoded]);
        // Read original WAV data chunks independently of FFmpeg's decoder, then compare every PCM byte.
        Assert.Equal(WavPcm(source), await File.ReadAllBytesAsync(decoded));
        Assert.Equal(bits, result.Output!.Audio.BitsPerSample);
    }

    [FfmpegTheory]
    [InlineData(AudioFormat.MP3)]
    [InlineData(AudioFormat.WAV)]
    [InlineData(AudioFormat.FLAC)]
    public async Task Extract_DefaultAudioTrackExcludesVideoAndOtherTracks(AudioFormat format)
    {
        var source = Path.Combine(_directory, "multi.mkv");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "lavfi", "-i", "sine=frequency=880:duration=2",
            "-map", "0", "-map", "1", "-map", "2", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "flac",
            "-disposition:a:0", "0", "-disposition:a:1", "default", source]);
        var service = Service();
        var inspection = await service.InspectAsync(source);
        Assert.True(inspection.IsSuccess, inspection.Error.ToString());
        Assert.Equal(2, inspection.Inspection!.Audio.Index);
        Assert.Equal(2, inspection.Inspection.AudioStreamCount);
        Assert.Equal(1, inspection.Inspection.VideoStreamCount);
        var options = new AudioConversionOptions(format);
        var destination = Path.Combine(_directory, "extract" + options.Extension);
        var result = await service.ConvertAsync(source, destination, options);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var expectedPcm = Path.Combine(_directory, "expected.pcm");
        var actualPcm = Path.Combine(_directory, "actual.pcm");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-i", source, "-map", "0:2", "-f", "s16le", expectedPcm]);
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-i", destination, "-f", "s16le", actualPcm]);
        if (format != AudioFormat.MP3) { Assert.Equal(await File.ReadAllBytesAsync(expectedPcm), await File.ReadAllBytesAsync(actualPcm)); }
        Assert.Equal((1, 0, 0), (result.Output!.AudioStreamCount, result.Output.VideoStreamCount, result.Output.OtherStreamCount));
    }

    [FfmpegTheory]
    [InlineData(AudioFormat.MP3)]
    [InlineData(AudioFormat.WAV)]
    [InlineData(AudioFormat.FLAC)]
    public async Task MetadataAndCoverArt_AreNotCopied(AudioFormat format)
    {
        var cover = Path.Combine(_directory, "cover.png");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=16x16", "-frames:v", "1", cover]);
        var source = Path.Combine(_directory, "tagged.mp3");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-i", Source(), "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-c:v", "copy",
            "-disposition:v", "attached_pic", "-metadata", "title=عنوان خاص", "-metadata", "artist=private artist", source]);
        var options = new AudioConversionOptions(format);
        var destination = Path.Combine(_directory, "clean" + options.Extension);
        var result = await Service().ConvertAsync(source, destination, options);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var metadata = VideoFixtures.Run(FfmpegTestTools.Ffprobe, ["-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", destination]);
        Assert.DoesNotContain("عنوان خاص", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("private artist", metadata, StringComparison.Ordinal);
        Assert.Equal(0, result.Output!.OtherStreamCount);
    }

    [FfmpegTheory]
    [InlineData("random")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("playlist")]
    public async Task InvalidInput_IsRejectedWithoutOutput(string kind)
    {
        var source = Path.Combine(_directory, kind + ".mp3");
        var bytes = kind switch
        {
            "empty" => Array.Empty<byte>(),
            "truncated" => (await File.ReadAllBytesAsync(Source()))[..40],
            "playlist" => System.Text.Encoding.UTF8.GetBytes("#EXTM3U\nhttp://127.0.0.1:9/audio.mp3\n"),
            _ => RandomNumberGenerator.GetBytes(4096),
        };
        await File.WriteAllBytesAsync(source, bytes);
        var destination = Path.Combine(_directory, "bad.flac");
        Assert.False((await Service().InspectAsync(source)).IsSuccess);
        Assert.False((await Service().ConvertAsync(source, destination, new(AudioFormat.FLAC))).IsSuccess);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task AudioOnlyCapabilitiesAndNoAudioVideo_AreHandledIndependently()
    {
        var source = Source();
        var result = await Service(new AudioOnlyRunner()).ConvertAsync(source, Path.Combine(_directory, "audio-only.flac"), new(AudioFormat.FLAC));
        Assert.True(result.IsSuccess, result.Error.ToString());
        var silent = VideoFixtures.Get("phase4-silent.mp4", "-f lavfi -i testsrc2=size=64x48:duration=1", "-c:v libx264 -preset ultrafast -an");
        Assert.Equal(AudioError.NoAudioStream, (await Service().InspectAsync(silent)).Error);
    }

    [FfmpegFact]
    public async Task Progress_HasActualEncodingAndDecodeStages()
    {
        var reports = new List<AudioProgress>();
        var result = await Service().ConvertAsync(Source(), Path.Combine(_directory, "progress.wav"), new(AudioFormat.WAV), new AudioTestProgress(reports.Add));
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Contains(reports, p => p.Stage == AudioStage.Encoding && p.Fraction == 1);
        Assert.Contains(reports, p => p.Stage == AudioStage.Verifying && p.Fraction == 1);
    }

    [FfmpegTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeout_StopOwnedEncoderAndCleanOutput(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        using var runner = new SlowRunner(timeout);
        var destination = Path.Combine(_directory, "stopped.mp3");
        var progress = new AudioTestProgress(report => { if (!timeout && report.Fraction > 0.05) { cancellation.Cancel(); } });
        var result = await Service(runner).ConvertAsync(Source("long.wav", seconds: 8), destination, new(), progress, cancellation.Token);
        Assert.Equal(timeout ? AudioError.TimedOut : AudioError.Cancelled, result.Error);
        Assert.NotNull(runner.Owned);
        Assert.True(runner.Owned.HasExited);
        Assert.False(result.CleanupFailed);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task FileSafety_CollisionsIdentityMissingSourceAndDestinationRace()
    {
        var source = Source();
        var service = Service();
        var destination = Path.Combine(_directory, "existing.wav");
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        Assert.Equal(AudioError.DestinationExists, (await service.ConvertAsync(source, destination, new(AudioFormat.WAV))).Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(AudioError.SamePath, (await service.ConvertAsync(source, source, new(AudioFormat.WAV))).Error);
        Assert.Equal(AudioError.UnreadableInput, (await service.InspectAsync(Path.Combine(_directory, "missing.wav"))).Error);
        var race = Path.Combine(_directory, "race.wav");
        var result = await Service(new MutationRunner(_ => File.WriteAllBytes(race, [9, 8, 7]))).ConvertAsync(source, race, new(AudioFormat.WAV));
        Assert.Equal(AudioError.DestinationExists, result.Error);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(race));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task InvalidOutput_IsRejectedDespiteCleanEncoderExit()
    {
        var destination = Path.Combine(_directory, "bad-output.wav");
        var result = await Service(new MutationRunner(path => File.WriteAllBytes(path, [0, 1, 2]))).ConvertAsync(Source(), destination, new(AudioFormat.WAV));
        Assert.Equal(AudioError.OutputValidationFailed, result.Error);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    [FfmpegFact]
    public async Task StereoPcm_RemainsStereoAndByteExactInFlac()
    {
        var source = VideoFixtures.Get("phase4-stereo.wav", "-f lavfi -i aevalsrc=0.25*sin(2*PI*440*t)|0.25*sin(2*PI*880*t):s=48000:d=2", "-c:a pcm_s16le");
        var destination = Path.Combine(_directory, "stereo.flac");
        var result = await Service().ConvertAsync(source, destination, new(AudioFormat.FLAC));
        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(2, result.Output!.Audio.Channels);
        var pcm = Path.Combine(_directory, "stereo.pcm");
        VideoFixtures.Run(FfmpegTestTools.Ffmpeg, ["-v", "error", "-i", destination, "-f", "s16le", pcm]);
        Assert.Equal(WavPcm(source), await File.ReadAllBytesAsync(pcm));
    }

    [FfmpegTheory]
    [MemberData(nameof(VideoConversionTests.InputFormats), MemberType = typeof(VideoConversionTests))]
    public async Task Extraction_FromVerifiedVideoContainers(string label, string fileName, string encode, bool hasAudio)
    {
        var source = VideoFixtures.Get("phase4-extraction-" + label.Replace(' ', '-').Replace('+', '-') + Path.GetExtension(fileName),
            "-f lavfi -i testsrc2=size=64x48:rate=25:duration=1 -f lavfi -i sine=frequency=500:duration=1", encode + " -shortest");
        var destination = Path.Combine(_directory, "extract.wav");
        var result = await Service().ConvertAsync(source, destination, new(AudioFormat.WAV));
        if (hasAudio) { Assert.True(result.IsSuccess, $"{label}: {result.Error}"); Assert.Equal("pcm_s16le", result.Output!.Audio.Codec); }
        else { Assert.Equal(AudioError.NoAudioStream, result.Error); Assert.False(File.Exists(destination)); }
    }

    [FfmpegTheory]
    [InlineData(ProcessOutcome.StartFailed, true, AudioError.ToolsUnavailable)]
    [InlineData(ProcessOutcome.Cancelled, true, AudioError.Cancelled)]
    [InlineData(ProcessOutcome.TimedOut, true, AudioError.TimedOut)]
    [InlineData(ProcessOutcome.Exited, true, AudioError.ConversionFailed)]
    [InlineData(ProcessOutcome.Cancelled, false, AudioError.ProcessTerminationFailed)]
    public async Task ProcessFailure_UsesExistingCleanupContract(ProcessOutcome outcome, bool terminationConfirmed, AudioError expected)
    {
        var destination = Path.Combine(_directory, "failure.mp3");
        var result = await Service(new FailureRunner(outcome, terminationConfirmed)).ConvertAsync(Source(), destination, new());
        Assert.Equal(expected, result.Error);
        Assert.Equal(!terminationConfirmed, result.CleanupFailed);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".orbitconvert-*"));
    }

    private sealed class FailureRunner(ProcessOutcome outcome, bool confirmed) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken) => request.Arguments.Contains("-c:a")
            ? Task.FromResult(new ProcessRunResult(outcome, 1, "", "injected process failure", confirmed))
            : new ProcessRunner().RunAsync(request, cancellationToken);
    }

    private static byte[] WavPcm(string path)
    {
        using var input = new BinaryReader(File.OpenRead(path));
        input.ReadBytes(12);
        while (input.BaseStream.Position + 8 <= input.BaseStream.Length)
        {
            var chunk = System.Text.Encoding.ASCII.GetString(input.ReadBytes(4));
            var length = input.ReadInt32();
            if (chunk == "data") { return input.ReadBytes(length); }
            input.BaseStream.Seek(length + (length & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("Generated WAV has no PCM data chunk.");
    }

    private sealed class AudioOnlyRunner : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            var result = await new ProcessRunner().RunAsync(request, cancellationToken);
            return request.Arguments.Contains("-encoders")
                ? result with { StandardOutput = " A....D libmp3lame encoder\n A....D pcm_s16le encoder\n A....D flac encoder\n" } : result;
        }
    }
    private sealed class MutationRunner(Action<string> mutate) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            var result = await new ProcessRunner().RunAsync(request, cancellationToken);
            if (request.Arguments.Contains("-c:a") && result.Succeeded) { mutate(request.Arguments[^1]["file:".Length..]); }
            return result;
        }
    }
    private sealed class SlowRunner(bool timeout) : IProcessRunner, IDisposable
    {
        public Process? Owned { get; private set; }
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            if (!request.Arguments.Contains("-c:a")) { return new ProcessRunner().RunAsync(request, cancellationToken); }
            var args = request.Arguments.ToList();
            args.Insert(args.IndexOf("-i"), "-re");
            var runner = new ProcessRunner(process =>
            {
                var started = process.Start();
                if (started) { Owned = Process.GetProcessById(process.Id); Owned.EnableRaisingEvents = true; }
                return started;
            });
            return runner.RunAsync(request with { Arguments = args, Timeout = timeout ? TimeSpan.FromMilliseconds(500) : request.Timeout }, cancellationToken);
        }
        public void Dispose() => Owned?.Dispose();
    }
}

internal sealed class AudioTestProgress(Action<AudioProgress> report) : IProgress<AudioProgress>
{
    public void Report(AudioProgress value) => report(value);
}
