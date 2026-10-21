using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.App.Services.Video;

public interface IVideoConversionService
{
    Task<VideoInspectionResult> InspectAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<VideoConversionResult> ConvertAsync(string sourcePath, string destinationPath, VideoConversionOptions options,
        IProgress<VideoProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts one local video to MP4 (H.264 + AAC) with FFmpeg. The original is opened read-only for the whole
/// operation, output goes to an owned temporary file, and only an output that passes FFprobe checks and a full
/// decode is moved (without overwrite) to the destination.
/// </summary>
public sealed class VideoConversionService(FfmpegLocator locator, IProcessRunner runner, H264Encoder? encoderOverride = null)
    : IVideoConversionService
{
    public const string OutputExtension = ".mp4";
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private (string Path, FfmpegCapabilities Capabilities)? _capabilities;

    public VideoConversionService()
        : this(new FfmpegLocator(), new ProcessRunner())
    {
    }

    public async Task<VideoInspectionResult> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        try { await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return VideoInspectionResult.Failure(VideoError.Cancelled); }
        try
        {
            var (tools, error) = await ResolveToolsAsync(cancellationToken).ConfigureAwait(false);
            if (tools is null) { return VideoInspectionResult.Failure(error); }
            var source = Path.GetFullPath(sourcePath);
            using var handle = OpenSource(source, out var openError);
            return handle is null ? VideoInspectionResult.Failure(openError)
                : await ProbeAsync(tools, source, handle.Length, output: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsFileError(error)) { return VideoInspectionResult.Failure(VideoError.UnreadableInput); }
        finally { OperationGate.Release(); }
    }

    public async Task<VideoConversionResult> ConvertAsync(string sourcePath, string destinationPath, VideoConversionOptions options,
        IProgress<VideoProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try { await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(VideoError.Cancelled); }
        try { return await ConvertCoreAsync(sourcePath, destinationPath, options, progress, cancellationToken).ConfigureAwait(false); }
        finally { OperationGate.Release(); }
    }

    private async Task<VideoConversionResult> ConvertCoreAsync(string sourcePath, string destinationPath, VideoConversionOptions options,
        IProgress<VideoProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.IsValid) { return new(VideoError.InvalidSettings); }
        string source, destination;
        try
        {
            source = Path.GetFullPath(sourcePath);
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception error) when (IsFileError(error)) { return new(VideoError.InvalidDestination); }
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) { return new(VideoError.SamePath); }
        if (File.Exists(destination) || Directory.Exists(destination)) { return new(VideoError.DestinationExists); }
        if (!Path.GetExtension(destination).Equals(OutputExtension, StringComparison.OrdinalIgnoreCase)) { return new(VideoError.InvalidDestination); }

        var (tools, toolsError) = await ResolveToolsAsync(cancellationToken).ConfigureAwait(false);
        if (tools is null) { return new(toolsError); }
        var encoder = encoderOverride ?? _capabilities!.Value.Capabilities.PreferredH264Encoder;

        // Held for the whole operation: FFmpeg may read, but nothing can modify or replace the original meanwhile.
        using var sourceHandle = OpenSource(source, out var openError);
        if (sourceHandle is null) { return new(openError); }
        var inspected = await ProbeAsync(tools, source, sourceHandle.Length, output: false, cancellationToken).ConfigureAwait(false);
        if (!inspected.IsSuccess) { return new(inspected.Error, CleanupFailed: inspected.Error == VideoError.ProcessTerminationFailed); }
        var input = inspected.Inspection!;
        var cleanupFailed = false;

        string? ownedTemporaryPath = null;
        VideoConversionResult result;
        try
        {
            var candidate = Path.Combine(Path.GetDirectoryName(destination)!, $".orbitconvert-{Guid.NewGuid():N}.tmp");
            // Reserve a unique name we own; FFmpeg then overwrites only this file.
            new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            ownedTemporaryPath = candidate;

            var clock = Stopwatch.StartNew();
            var conversion = await runner.RunAsync(new ProcessRunRequest(tools.FfmpegPath,
                FfmpegArguments.Convert(source, candidate, input, options, encoder), VideoLimits.MaximumRunTime, VideoLimits.InactivityTimeout,
                ProgressSink(VideoStage.Converting, input.Duration, clock, progress)), cancellationToken).ConfigureAwait(false);
            var failure = Failure(conversion);
            if (failure != VideoError.None) { result = new(failure, CleanupFailed: !conversion.TerminationConfirmed); }
            else
            {
                var validation = await ValidateAsync(tools, candidate, input, options, clock, progress, cancellationToken).ConfigureAwait(false);
                if (!validation.IsSuccess) { result = new(validation.Error, CleanupFailed: validation.Error == VideoError.ProcessTerminationFailed); }
                else if (cancellationToken.IsCancellationRequested) { result = new(VideoError.Cancelled); }
                else
                {
                    File.Move(candidate, destination, overwrite: false);
                    ownedTemporaryPath = null;
                    result = new(VideoError.None, destination, Output: validation.Inspection);
                }
            }
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
        {
            result = new(VideoError.DestinationExists);
        }
        catch (Exception error) when (IsFileError(error))
        {
            result = new(VideoError.OutputFailure);
        }
        finally
        {
            if (ownedTemporaryPath is not null && !TryDelete(ownedTemporaryPath)) { cleanupFailed = true; }
        }
        return cleanupFailed ? result with { CleanupFailed = true } : result;
    }

    private async Task<VideoInspectionResult> ValidateAsync(FfmpegTools tools, string candidate, VideoInspection input,
        VideoConversionOptions options, Stopwatch clock, IProgress<VideoProgress>? progress, CancellationToken cancellationToken)
    {
        var size = new FileInfo(candidate).Length;
        if (size == 0) { return VideoInspectionResult.Failure(VideoError.OutputValidationFailed); }
        var probed = await ProbeAsync(tools, candidate, size, output: true, cancellationToken).ConfigureAwait(false);
        if (probed.Error is VideoError.Cancelled or VideoError.TimedOut or VideoError.ProcessTerminationFailed) { return probed; }
        if (!probed.IsSuccess) { return VideoInspectionResult.Failure(VideoError.OutputValidationFailed); }
        var output = probed.Inspection!;
        var (width, height) = options.OutputSize(input.Video.DisplayWidth, input.Video.DisplayHeight);
        var audioMatches = input.Audio is null
            ? output.AudioStreamCount == 0
            : output.AudioStreamCount == 1 && output.Audio is { Codec: "aac" };
        var durationMatches = input.Duration is not { } expected || output.Duration is not { } actual ||
            Math.Abs((actual - expected).TotalSeconds) <= Math.Max(1.0, expected.TotalSeconds * 0.05);
        if (output.Video.Codec != "h264" || output.VideoStreamCount != 1 || output.OtherStreamCount != 0 ||
            output.Video.Width != width || output.Video.Height != height || output.Video.Rotation != 0 ||
            !audioMatches || !durationMatches)
        {
            return VideoInspectionResult.Failure(VideoError.OutputValidationFailed);
        }

        var decode = await runner.RunAsync(new ProcessRunRequest(tools.FfmpegPath, FfmpegArguments.DecodeCheck(candidate),
            VideoLimits.MaximumRunTime, VideoLimits.InactivityTimeout, ProgressSink(VideoStage.Verifying, output.Duration, clock, progress)),
            cancellationToken).ConfigureAwait(false);
        var failure = Failure(decode);
        return failure switch
        {
            VideoError.None => probed,
            VideoError.Cancelled or VideoError.TimedOut or VideoError.ProcessTerminationFailed => VideoInspectionResult.Failure(failure),
            _ => VideoInspectionResult.Failure(VideoError.OutputValidationFailed),
        };
    }

    private async Task<VideoInspectionResult> ProbeAsync(FfmpegTools tools, string path, long size, bool output, CancellationToken cancellationToken)
    {
        if (size == 0) { return VideoInspectionResult.Failure(VideoError.EmptyInput); }
        if (size > VideoLimits.MaximumInputBytes) { return VideoInspectionResult.Failure(VideoError.TooLarge); }
        var probe = await runner.RunAsync(new ProcessRunRequest(tools.FfprobePath, FfmpegArguments.Probe(path, output), VideoLimits.ProbeTimeout),
            cancellationToken).ConfigureAwait(false);
        return probe.Outcome switch
        {
            _ when !probe.TerminationConfirmed => VideoInspectionResult.Failure(VideoError.ProcessTerminationFailed),
            ProcessOutcome.Cancelled => VideoInspectionResult.Failure(VideoError.Cancelled),
            ProcessOutcome.TimedOut => VideoInspectionResult.Failure(VideoError.TimedOut),
            ProcessOutcome.StartFailed => VideoInspectionResult.Failure(VideoError.ToolsUnavailable),
            _ when probe.ExitCode != 0 => VideoInspectionResult.Failure(
                probe.StandardError.Contains("not on whitelist", StringComparison.Ordinal) ? VideoError.UnsupportedFormat : VideoError.InvalidOrCorruptVideo),
            _ => VideoProbeParser.Parse(probe.StandardOutput, size),
        };
    }

    private async Task<(FfmpegTools? Tools, VideoError Error)> ResolveToolsAsync(CancellationToken cancellationToken)
    {
        var tools = locator.Locate();
        if (tools is null) { return (null, VideoError.ToolsUnavailable); }
        if (_capabilities is not { } cached || cached.Path != tools.FfmpegPath)
        {
            var capabilities = await FfmpegLocator.ProbeAsync(tools, runner, cancellationToken).ConfigureAwait(false);
            if (capabilities is null) { return (null, cancellationToken.IsCancellationRequested ? VideoError.Cancelled : VideoError.ToolsUnavailable); }
            _capabilities = (tools.FfmpegPath, capabilities);
        }
        var available = _capabilities.Value.Capabilities;
        var usable = available.CanConvertToMp4 && (encoderOverride is not { } forced || available.Supports(forced));
        return usable ? (tools, VideoError.None) : (null, VideoError.ToolsUnsupported);
    }

    /// <summary>Turns FFmpeg <c>-progress</c> key/value lines into real progress reports.</summary>
    internal static Action<string> ProgressSink(VideoStage stage, TimeSpan? total, Stopwatch clock, IProgress<VideoProgress>? progress)
    {
        var processed = TimeSpan.Zero;
        return line =>
        {
            if (progress is null) { return; }
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) &&
                microseconds >= 0)
            {
                processed = TimeSpan.FromTicks(microseconds * 10);
            }
            else if (line.StartsWith("progress=", StringComparison.Ordinal))
            {
                var done = line == "progress=end";
                double? fraction = total is { TotalSeconds: > 0 } duration
                    ? (done ? 1.0 : Math.Clamp(processed.TotalSeconds / duration.TotalSeconds, 0, 0.999)) : null;
                progress.Report(new VideoProgress(stage, processed, total, fraction, clock.Elapsed));
            }
        };
    }

    private static VideoError Failure(ProcessRunResult run) => run.Outcome switch
    {
        _ when !run.TerminationConfirmed => VideoError.ProcessTerminationFailed,
        ProcessOutcome.Cancelled => VideoError.Cancelled,
        ProcessOutcome.TimedOut => VideoError.TimedOut,
        ProcessOutcome.StartFailed => VideoError.ToolsUnavailable,
        _ => run.ExitCode == 0 ? VideoError.None : VideoError.ConversionFailed,
    };

    private static FileStream? OpenSource(string path, out VideoError error)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            error = VideoError.None;
            return stream;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            error = VideoError.UnreadableInput;
            return null;
        }
    }

    /// <summary>Deletes only the owned temporary file; retries briefly because antivirus or sync may hold a new file.</summary>
    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception error) when (IsFileError(error)) { Thread.Sleep(200); }
        }
        return !File.Exists(path);
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
