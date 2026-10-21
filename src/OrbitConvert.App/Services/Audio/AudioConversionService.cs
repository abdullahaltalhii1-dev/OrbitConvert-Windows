using System.Diagnostics;
using System.IO;
using System.Security;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Audio;

public interface IAudioConversionService
{
    Task<AudioInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<AudioConversionResult> ConvertAsync(string sourcePath, string destinationPath, AudioConversionOptions options,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Single local audio conversion/extraction using the shared, contained FFmpeg process runner.</summary>
public sealed class AudioConversionService(FfmpegLocator locator, IProcessRunner runner) : IAudioConversionService
{
    private (string Path, FfmpegCapabilities Capabilities)? _capabilities;
    public AudioConversionService() : this(new FfmpegLocator(), new ProcessRunner()) { }

    public async Task<AudioInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        try { await MediaOperationGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return AudioInspectionResult.Failure(AudioError.Cancelled); }
        try
        {
            var (tools, error) = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (tools is null) { return AudioInspectionResult.Failure(error); }
            var source = Path.GetFullPath(path);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await ProbeAsync(tools, source, input.Length, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error)) { return AudioInspectionResult.Failure(AudioError.UnreadableInput); }
        finally { MediaOperationGate.Instance.Release(); }
    }

    public async Task<AudioConversionResult> ConvertAsync(string sourcePath, string destinationPath, AudioConversionOptions options,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try { await MediaOperationGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(AudioError.Cancelled); }
        try { return await ConvertCoreAsync(sourcePath, destinationPath, options, progress, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsFileError(error)) { return new(AudioError.UnreadableInput); }
        finally { MediaOperationGate.Instance.Release(); }
    }

    private async Task<AudioConversionResult> ConvertCoreAsync(string sourcePath, string destinationPath, AudioConversionOptions options,
        IProgress<AudioProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.IsValid) { return new(AudioError.InvalidSettings); }
        string source, destination;
        try { source = Path.GetFullPath(sourcePath); destination = Path.GetFullPath(destinationPath); }
        catch (Exception error) when (IsFileError(error)) { return new(AudioError.InvalidDestination); }
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) { return new(AudioError.SamePath); }
        if (File.Exists(destination) || Directory.Exists(destination)) { return new(AudioError.DestinationExists); }
        if (!Path.GetExtension(destination).Equals(options.Extension, StringComparison.OrdinalIgnoreCase)) { return new(AudioError.InvalidDestination); }
        var (tools, toolsError) = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (tools is null) { return new(toolsError); }
        var capabilities = _capabilities!.Value.Capabilities;
        var supported = options.Format switch
        {
            AudioFormat.MP3 => capabilities.HasMp3Encoder,
            AudioFormat.WAV => capabilities.HasPcm16Encoder,
            _ => capabilities.HasFlacEncoder,
        };
        if (!supported) { return new(AudioError.ToolsUnsupported); }
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspected = await ProbeAsync(tools, source, input.Length, false, cancellationToken).ConfigureAwait(false);
        if (!inspected.IsSuccess) { return Failed(inspected.Error); }
        var audio = inspected.Inspection!;
        var settingsError = options.Validate(audio);
        if (settingsError != AudioError.None) { return new(settingsError); }

        string? owned = null;
        AudioConversionResult result;
        var cleanupFailed = false;
        try
        {
            var candidate = Path.Combine(Path.GetDirectoryName(destination)!, $".orbitconvert-{Guid.NewGuid():N}.tmp");
            new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            owned = candidate;
            var clock = Stopwatch.StartNew();
            var encoded = await runner.RunAsync(new(tools.FfmpegPath, AudioArguments.Convert(source, candidate, audio, options),
                AudioLimits.MaximumRunTime, AudioLimits.InactivityTimeout, Sink(AudioStage.Encoding, audio.Duration, clock, progress)), cancellationToken).ConfigureAwait(false);
            var failure = Failure(encoded);
            if (failure != AudioError.None) { result = Failed(failure); }
            else
            {
                var validated = await ValidateAsync(tools, candidate, audio, options, clock, progress, cancellationToken).ConfigureAwait(false);
                if (!validated.IsSuccess) { result = Failed(validated.Error); }
                else if (cancellationToken.IsCancellationRequested) { result = new(AudioError.Cancelled); }
                else
                {
                    File.Move(candidate, destination, overwrite: false);
                    owned = null;
                    result = new(AudioError.None, destination, Output: validated.Inspection);
                }
            }
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { result = new(AudioError.DestinationExists); }
        catch (Exception error) when (IsFileError(error)) { result = new(AudioError.OutputFailure); }
        finally { if (owned is not null) { cleanupFailed = !TryDelete(owned); } }
        return cleanupFailed ? result with { CleanupFailed = true } : result;
    }

    private async Task<AudioInspectionResult> ValidateAsync(FfmpegTools tools, string candidate, AudioInspection input, AudioConversionOptions options,
        Stopwatch clock, IProgress<AudioProgress>? progress, CancellationToken cancellationToken)
    {
        var output = await ProbeAsync(tools, candidate, new FileInfo(candidate).Length, true, cancellationToken).ConfigureAwait(false);
        if (!output.IsSuccess)
        {
            return IsStop(output.Error) ? output : AudioInspectionResult.Failure(AudioError.OutputValidationFailed);
        }
        var actual = output.Inspection!;
        var durationMatches = input.Duration is not { } expected || actual.Duration is { } length &&
            Math.Abs((expected - length).TotalSeconds) <= Math.Max(0.25, expected.TotalSeconds * 0.02);
        if (actual.Container != options.Format.ToString().ToLowerInvariant() || actual.Audio.Codec != options.Codec || actual.AudioStreamCount != 1 ||
            actual.VideoStreamCount != 0 || actual.OtherStreamCount != 0 || actual.Audio.Channels != input.Audio.Channels ||
            actual.Audio.SampleRate != options.OutputRate(input) || !durationMatches ||
            (options.Format == AudioFormat.MP3 && actual.Audio.Bitrate != options.BitrateKbps * 1000) ||
            (options.Format != AudioFormat.MP3 && actual.Audio.BitsPerSample != options.OutputBits(input)))
        {
            return AudioInspectionResult.Failure(AudioError.OutputValidationFailed);
        }
        var decoded = await runner.RunAsync(new(tools.FfmpegPath, AudioArguments.Decode(candidate), AudioLimits.MaximumRunTime,
            AudioLimits.InactivityTimeout, Sink(AudioStage.Verifying, actual.Duration, clock, progress)), cancellationToken).ConfigureAwait(false);
        var failure = Failure(decoded);
        return failure == AudioError.None ? output : AudioInspectionResult.Failure(IsStop(failure) ? failure : AudioError.OutputValidationFailed);
    }

    private async Task<AudioInspectionResult> ProbeAsync(FfmpegTools tools, string path, long size, bool output, CancellationToken token)
    {
        if (size == 0) { return AudioInspectionResult.Failure(AudioError.EmptyInput); }
        if (size > AudioLimits.MaximumInputBytes) { return AudioInspectionResult.Failure(AudioError.TooLarge); }
        var result = await runner.RunAsync(new(tools.FfprobePath, AudioArguments.Probe(path, output), AudioLimits.ProbeTimeout), token).ConfigureAwait(false);
        var failure = Failure(result);
        if (failure != AudioError.None)
        {
            return AudioInspectionResult.Failure(failure == AudioError.ConversionFailed
                ? result.StandardError.Contains("not on whitelist", StringComparison.Ordinal) ? AudioError.UnsupportedFormat : AudioError.InvalidOrCorruptAudio : failure);
        }
        return AudioProbeParser.Parse(result.StandardOutput, size);
    }

    private async Task<(FfmpegTools? Tools, AudioError Error)> ResolveAsync(CancellationToken token)
    {
        var tools = locator.Locate();
        if (tools is null) { return (null, AudioError.ToolsUnavailable); }
        if (_capabilities is not { } cached || cached.Path != tools.FfmpegPath)
        {
            var found = await FfmpegLocator.ProbeAsync(tools, runner, token).ConfigureAwait(false);
            if (found is null) { return (null, token.IsCancellationRequested ? AudioError.Cancelled : AudioError.ToolsUnavailable); }
            _capabilities = (tools.FfmpegPath, found);
        }
        // Inspection is independent of H.264/AAC and of the eventual output encoder.
        return (tools, AudioError.None);
    }

    private static Action<string> Sink(AudioStage stage, TimeSpan? total, Stopwatch clock, IProgress<AudioProgress>? progress) =>
        FfmpegProgress.Create(total, clock, (processed, fraction, elapsed) => progress?.Report(new(stage, fraction, processed, elapsed)));
    private static AudioConversionResult Failed(AudioError error) => new(error, CleanupFailed: error == AudioError.ProcessTerminationFailed);
    private static bool IsStop(AudioError error) => error is AudioError.Cancelled or AudioError.TimedOut or AudioError.ProcessTerminationFailed;
    private static AudioError Failure(ProcessRunResult result) => !result.TerminationConfirmed ? AudioError.ProcessTerminationFailed : result.Outcome switch
    {
        ProcessOutcome.Cancelled => AudioError.Cancelled,
        ProcessOutcome.TimedOut => AudioError.TimedOut,
        ProcessOutcome.StartFailed => AudioError.ToolsUnavailable,
        _ => result.ExitCode == 0 ? AudioError.None : AudioError.ConversionFailed,
    };
    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return true; }
            catch (Exception error) when (IsFileError(error)) { Thread.Sleep(200); }
        }
        return !File.Exists(path);
    }
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
