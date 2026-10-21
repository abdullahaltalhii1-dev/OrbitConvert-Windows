using System.Diagnostics;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task AlreadyCancelled_DoesNotStartAnyProcess()
    {
        var runner = new ProcessRunner(_ => throw new InvalidOperationException("Must not start."));
        var result = await runner.RunAsync(new("ignored.exe", [], TimeSpan.FromSeconds(5)), new CancellationToken(true));
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.True(result.TerminationConfirmed);
    }

    [Fact]
    public async Task MissingExecutable_ReportsStartFailedInsteadOfThrowing()
    {
        var result = await new ProcessRunner().RunAsync(
            new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"), [], TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
    }

    [Fact]
    public async Task StalledProcessCreation_TimesOutAndNeverBlocksTheCaller()
    {
        // Process.Start has been observed to block indefinitely while security software inspects a new executable.
        using var release = new ManualResetEventSlim();
        var runner = new ProcessRunner(_ => { release.Wait(TimeSpan.FromSeconds(30)); return false; });
        var clock = Stopwatch.StartNew();
        var result = await runner.RunAsync(new("ignored.exe", [], TimeSpan.FromSeconds(30), StartTimeout: TimeSpan.FromMilliseconds(300)), CancellationToken.None);
        release.Set();
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.False(result.TerminationConfirmed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task StalledProcessCreation_HonorsCancellation()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var runner = new ProcessRunner(_ => { release.Wait(TimeSpan.FromSeconds(30)); return false; });
        var result = await runner.RunAsync(new("ignored.exe", [], TimeSpan.FromSeconds(30)), cancellation.Token);
        release.Set();
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.False(result.TerminationConfirmed);
    }

    [FfmpegFact]
    public async Task Arguments_ArePassedLiterallyWithoutAnyShell()
    {
        // Characters a shell would interpret must reach the program unchanged as one argument.
        var hostile = "عربي \"quoted\" & | > %PATH% $(whoami) ; 'x' ^ !";
        var result = await new ProcessRunner().RunAsync(new(FfmpegTestTools.Ffprobe,
            ["-hide_banner", "-v", "error", "-protocol_whitelist", "file", FfmpegArgumentsProbe(hostile)], TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("%PATH%", result.StandardError, StringComparison.Ordinal); // Not expanded.
        Assert.Contains("$(whoami)", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("عربي", result.StandardError, StringComparison.Ordinal); // UTF-8 round trip.
    }

    [FfmpegFact]
    public async Task Timeout_KillsTheProcessTreeAndWaitsForExit()
    {
        var clock = Stopwatch.StartNew();
        var result = await new ProcessRunner().RunAsync(new(FfmpegTestTools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-f", "null", "-"],
            TimeSpan.FromSeconds(2)), CancellationToken.None);
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 20);
    }

    [FfmpegFact]
    public async Task InactivityTimeout_StopsAProcessThatStopsReportingProgress()
    {
        // -re with a 1 fps source and no -progress writes nothing to standard output.
        var result = await new ProcessRunner().RunAsync(new(FfmpegTestTools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=1", "-f", "null", "-"],
            TimeSpan.FromMinutes(5), InactivityTimeout: TimeSpan.FromSeconds(2)), CancellationToken.None);
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
    }

    [FfmpegFact]
    public async Task Cancellation_StopsARunningProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var lines = 0;
        var result = await new ProcessRunner().RunAsync(new(FfmpegTestTools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1", "-re", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-f", "null", "-"],
            TimeSpan.FromMinutes(5), StandardOutputLine: _ => Interlocked.Increment(ref lines)), cancellation.Token);
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.True(lines > 0, "Progress lines should stream while the process runs.");
    }

    private static string FfmpegArgumentsProbe(string name) => "file:" + Path.Combine(Path.GetTempPath(), name + ".mp4");
}
