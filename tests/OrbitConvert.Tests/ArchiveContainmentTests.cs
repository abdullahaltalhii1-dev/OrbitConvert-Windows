using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using OrbitConvert.App.Services.Archives;
using OrbitConvert.App.Services.Media;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Writers.SevenZip;

namespace OrbitConvert.Tests;

public sealed class ArchiveContainmentTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "contain-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string path) => Path.Combine(_root, path);
    public void Dispose()
    {
        using var files = new ArchiveFileSystem(); files.PinDirectory(_root, rename: true);
        Assert.True(files.CleanupOwnedDirectory(_root));
    }
    private string Fixture()
    {
        var path = At("input.zip");
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("عربي.txt").Open(); entry.Write("original"u8);
        return path;
    }

    [Fact]
    public async Task HeldStdin_PreventsRequestAccessUntilSuccessfulAssignment()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), ".orbitconvert-archive-" + Guid.NewGuid().ToString("N"))).FullName;
        using var pins = new ArchiveFileSystem(); pins.PinDirectory(root, rename: true);
        Process? owned = null; var held = false;
        var path = Path.Combine(root, "request.json");
        // The request does not even exist until the startup barrier has been observed.
        var runner = new ProcessRunner(process =>
        {
            var started = process.Start(); owned = Process.GetProcessById(process.Id);
            held = !owned.WaitForExit(1500) && !File.Exists(Path.Combine(root, "response.json"));
            File.WriteAllText(path, JsonSerializer.Serialize(new ArchiveWorkerRequest("zip", "response.json", [])));
            return started;
        });
        try
        {
            var result = await runner.RunAsync(new(Path.Combine(AppContext.BaseDirectory, "OrbitConvert.App.exe"), ["--archive-worker", path], TimeSpan.FromSeconds(15), MaximumJobMemoryBytes: ArchiveLimits.JobMemoryBytes), default);
            Assert.True(held, "Worker accessed the absent request before stdin EOF / assignment.");
            Assert.True(result.Succeeded, result.StandardError); Assert.True(owned!.HasExited);
            Assert.Equal("archive-worker-ready", result.StandardOutput.Trim());
            Assert.True(File.Exists(Path.Combine(root, "response.json")));
        }
        finally { owned?.Dispose(); Assert.True(pins.CleanupOwnedDirectory(root)); }
    }

    [Fact]
    public async Task FailedAssignment_NeverParsesAndCleansWithoutPublication()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source);
        Process? owned = null; string? workspace = null; ProcessRunResult? run = null;
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            var actual = new ProcessRunner(process =>
            {
                var started = process.Start(); owned = Process.GetProcessById(process.Id);
                owned.WaitForExit(1500); // Deterministically give the old, ungated worker time to parse.
                return started;
            }, (_, handle) => JobAssignmentTestSupport.AssignProcessToJobObject(IntPtr.Zero, handle));
            run = await actual.RunAsync(request, token);
            Assert.False(File.Exists(Path.Combine(workspace!, "response.json")));
            return run;
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("result")));
            Assert.Equal(ProcessOutcome.StartFailed, run!.Outcome); Assert.True(run.TerminationConfirmed);
            Assert.DoesNotContain("archive-worker-ready", run.StandardOutput); Assert.True(owned!.HasExited);
            Assert.False(result.IsSuccess); Assert.False(result.CleanupFailed);
            Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("result")));
            Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task CancellationDuringStartupBarrier_EventuallyTerminatesWithoutParsing()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), ".orbitconvert-archive-" + Guid.NewGuid().ToString("N"))).FullName;
        using var pins = new ArchiveFileSystem(); pins.PinDirectory(root, rename: true);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProcessRunner(process =>
        {
            var success = process.Start(); var child = Process.GetProcessById(process.Id);
            child.WaitForExit(1500); started.SetResult(child);
            release.Wait(TimeSpan.FromSeconds(10)); return success;
        });
        Process? owned = null;
        try
        {
            var task = runner.RunAsync(new(Path.Combine(AppContext.BaseDirectory, "OrbitConvert.App.exe"), ["--archive-worker", Path.Combine(root, "absent.json")], TimeSpan.FromSeconds(15)), stop.Token);
            owned = await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(owned.HasExited); stop.Cancel();
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProcessOutcome.Cancelled, result.Outcome); Assert.False(result.TerminationConfirmed);
            release.Set(); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            release.Set();
            if (owned is { HasExited: false }) { owned.Kill(true); owned.WaitForExit(5000); }
            owned?.Dispose(); Assert.True(pins.CleanupOwnedDirectory(root));
        }
    }

    [Fact]
    public async Task ParentVerificationCancellation_CleansOwnedStagesAndPreservesUnrelatedData()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source); File.WriteAllText(At("keep"), "unrelated");
        using var stop = new CancellationTokenSource(); Process? owned = null; string? workspace = null; var verifying = false;
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            return await new ProcessRunner(process => { var ok = process.Start(); owned = Process.GetProcessById(process.Id); return ok; }).RunAsync(request, token);
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("result")), new InlineProgress(stage =>
            {
                if (stage == "Verifying") { verifying = true; Assert.True(owned!.HasExited); stop.Cancel(); }
            }), stop.Token);
            Assert.True(verifying); Assert.Equal(ArchiveError.Cancelled, result.Error); Assert.False(result.CleanupFailed);
            Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("result")));
            Assert.Empty(Directory.GetFileSystemEntries(_root, ".orbitconvert-archive-*"));
            Assert.Equal(before, File.ReadAllBytes(source)); Assert.Equal("unrelated", File.ReadAllText(At("keep")));
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task UnexpectedWorkerExit_IsNotMisreportedAsMemoryExhaustion()
    {
        var source = Fixture(); var before = File.ReadAllBytes(source); Process? owned = null; string? workspace = null;
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            return await new ProcessRunner(process =>
            {
                var ok = process.Start(); owned = Process.GetProcessById(process.Id);
                // Kill this owned child while still at the barrier, guaranteeing no response.
                owned.Kill(true); owned.WaitForExit(5000); return ok;
            }).RunAsync(request, token);
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("result")));
            Assert.Equal(ArchiveError.OutputFailure, result.Error); Assert.True(owned!.HasExited);
            Assert.False(result.CleanupFailed); Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("result")));
            Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task RealJobMemoryLimit_RejectsLegitimateLargeDictionaryWithoutPublication()
    {
        // The decoder grows its dictionary lazily; a tiny file does NOT exercise the quota.
        // Repeated random blocks keep this legitimate 40 MiB entry below the 200:1 ratio cap.
        var source = At("dictionary.7z"); var data = new byte[40 * 1024 * 1024];
        new Random(7123).NextBytes(data.AsSpan(0, 1024 * 1024));
        for (var offset = 1024 * 1024; offset < data.Length; offset += 1024 * 1024)
        { data.AsSpan(0, 1024 * 1024).CopyTo(data.AsSpan(offset)); }
        using (var output = File.Create(source))
        using (var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA)
        { LzmaProperties = new LzmaEncoderProperties(false, 64 * 1024 * 1024), CompressHeader = false }))
        { using var input = new MemoryStream(data); writer.Write("legitimate.bin", input, null); }
        var before = File.ReadAllBytes(source);
        var normal = await new ArchiveService().InspectAsync(source);
        Assert.True(normal.IsSuccess, normal.ToString());
        Process? owned = null; string? workspace = null; ProcessRunResult? run = null;
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            run = await new ProcessRunner(process => { var ok = process.Start(); owned = Process.GetProcessById(process.Id); return ok; })
                .RunAsync(request with { MaximumJobMemoryBytes = 64L * 1024 * 1024 }, token);
            var response = JsonSerializer.Deserialize<ArchiveWorkerResponse>(File.ReadAllText(Path.Combine(workspace!, "response.json")));
            Assert.Equal(ArchiveError.TooLarge, response!.Error);
            return run;
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("result")));
            Assert.Equal(ArchiveError.TooLarge, result.Error);
            Assert.True(owned!.HasExited); Assert.True(run!.TerminationConfirmed); Assert.Equal(ProcessOutcome.Exited, run.Outcome);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("archive-worker-ready", run.StandardOutput);
            Assert.False(result.CleanupFailed); Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("result")));
            Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }

    private sealed class DelegateRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run) : IProcessRunner
    { public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => run(request, token); }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
