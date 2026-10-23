using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OrbitConvert.App.Services.Archives;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class ArchiveSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "arc-safe-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string path) => Path.Combine(_root, path);
    public void Dispose()
    {
        using var files = new ArchiveFileSystem(); files.PinDirectory(_root, rename: true);
        Assert.True(files.CleanupOwnedDirectory(_root), "Could not clean exact owned fixture root: " + _root);
    }
    private string Fixture(string name = "file", int count = 1)
    {
        var path = At(Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path); using var writer = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < count; index++)
        { using var data = writer.CreateEntry(count == 1 ? name : "file-" + index).Open(); data.Write(new byte[128 * 1024]); }
        return path;
    }
    [Theory]
    [InlineData(0xA1FF0000u, ArchiveError.LinksNotSupported)]
    [InlineData(0x400u, ArchiveError.LinksNotSupported)]
    public async Task ZipSymlinkAndReparseAttributes_AreRejected(uint attributes, ArchiveError expected)
    {
        var archive = Fixture(); var bytes = File.ReadAllBytes(archive);
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 38), attributes); File.WriteAllBytes(archive, bytes);
        Assert.Equal(expected, (await new ArchiveService().InspectAsync(archive)).Error);
    }
    [Fact]
    public async Task EncryptedUnsupportedMissingLockedAndExcessiveInputs_AreExplicit()
    {
        var archive = Fixture(); var before = File.ReadAllBytes(archive); var bytes = (byte[])before.Clone();
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        bytes[6] |= 1; bytes[central + 8] |= 1; File.WriteAllBytes(At("flag-encrypted.zip"), bytes);
        Assert.Equal(ArchiveError.Encrypted, (await new ArchiveService().InspectAsync(At("flag-encrypted.zip"))).Error);
        File.WriteAllText(At("unsupported.rar"), "Rar!\x1a\x07\0");
        Assert.Equal(ArchiveError.UnsupportedFormat, (await new ArchiveService().InspectAsync(At("unsupported.rar"))).Error);
        Assert.Equal(ArchiveError.UnreadableInput, (await new ArchiveService().InspectAsync(At("missing"))).Error);
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None))
        { Assert.Equal(ArchiveError.UnreadableInput, (await new ArchiveService().InspectAsync(archive)).Error); }
        var tooMany = Fixture(count: ArchiveLimits.Entries + 1);
        Assert.Equal(ArchiveError.TooLarge, (await new ArchiveService().InspectAsync(tooMany)).Error);
        Assert.Equal(ArchiveError.TooLarge, (await new ArchiveService().InspectAsync(Fixture(string.Join('/', Enumerable.Repeat("a", 17)) + "/file"))).Error);
        Assert.Equal(before, File.ReadAllBytes(archive));
    }
    [Fact]
    public async Task NativeCancellation_StopsOwnedWorkerAndCleansOnlyItsData()
    {
        var source = Fixture(count: 20); var original = File.ReadAllBytes(source);
        using var stop = new CancellationTokenSource(); Process? owned = null; string? workspace = null; var ready = false;
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            var actual = new ProcessRunner(process => { var started = process.Start(); if (started) { owned = Process.GetProcessById(process.Id); } return started; });
            return await actual.RunAsync(request with { StandardOutputLine = line => { if (line == "archive-worker-ready") { ready = true; stop.Cancel(); } } }, token);
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("cancelled")), token: stop.Token);
            Assert.True(ready); Assert.True(owned!.HasExited); Assert.Equal(ArchiveError.Cancelled, result.Error); Assert.False(result.CleanupFailed);
            Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("cancelled"))); Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }
    [Fact]
    public async Task ActualWorkerDeadline_StopsOwnedChildWithoutPublication()
    {
        Process? owned = null; string? workspace = null; var source = Fixture(); var before = File.ReadAllBytes(source);
        var runner = new DelegateRunner(async (request, token) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            var actual = new ProcessRunner(process => { var started = process.Start(); if (started) { owned = Process.GetProcessById(process.Id); } return started; });
            return await actual.RunAsync(request with { Timeout = TimeSpan.FromMilliseconds(1) }, token);
        });
        try
        {
            var result = await new ArchiveService(runner).ExecuteAsync(new(ArchiveOperation.Extract, [source], At("timeout")));
            Assert.Equal(ArchiveError.TimedOut, result.Error); Assert.True(owned!.HasExited); Assert.False(result.CleanupFailed);
            Assert.False(Directory.Exists(workspace)); Assert.False(Directory.Exists(At("timeout"))); Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally { owned?.Dispose(); }
    }
    [Fact]
    public async Task UnconfirmedTermination_PreservesOwnedWorkspaceInsteadOfCleaningWhileActive()
    {
        string? workspace = null;
        var runner = new DelegateRunner((request, _) =>
        {
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            return Task.FromResult(new ProcessRunResult(ProcessOutcome.Cancelled, -1, "", "", TerminationConfirmed: false));
        });
        var result = await new ArchiveService(runner).InspectAsync(Fixture());
        Assert.Equal(ArchiveError.ProcessTerminationFailed, result.Error); Assert.True(result.CleanupFailed);
        Assert.True(File.Exists(Path.Combine(workspace!, "input")));
        using var cleanup = new ArchiveFileSystem(); cleanup.PinDirectory(workspace!, rename: true);
        Assert.True(cleanup.CleanupOwnedDirectory(workspace!));
    }
    [Fact]
    public async Task ExtractionDestinationRace_DoesNotMergeOrReplaceAnyExistingData()
    {
        var source = Fixture(); var progress = new SynchronousProgress(stage =>
        {
            if (stage != "Verifying") { return; }
            Directory.CreateDirectory(At("winner")); File.WriteAllText(At("winner/keep"), "unrelated");
        });
        var result = await new ArchiveService().ExecuteAsync(new(ArchiveOperation.Extract, [source], At("winner")), progress);
        Assert.Equal(ArchiveError.DestinationExists, result.Error); Assert.False(result.CleanupFailed);
        Assert.Equal(new[] { Path.GetFullPath(At("winner/keep")) }, Directory.GetFiles(At("winner"))); Assert.Equal("unrelated", File.ReadAllText(At("winner/keep")));
    }
    [Fact]
    public async Task LockedOwnedTemporaryFile_ReportsCleanupFailureAndPreservesUnrelatedFiles()
    {
        var source = Fixture(); File.WriteAllText(At("keep.txt"), "unrelated"); FileStream? held = null; string? workspace = null;
        var runner = new DelegateRunner(async (request, token) =>
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            workspace = Path.GetDirectoryName(request.Arguments[1]);
            held = new FileStream(Path.Combine(workspace!, "locked"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return result;
        });
        try
        {
            var result = await new ArchiveService(runner).InspectAsync(source);
            Assert.True(result.IsSuccess); Assert.True(result.CleanupFailed); Assert.True(Directory.Exists(workspace));
            Assert.Equal("unrelated", File.ReadAllText(At("keep.txt")));
        }
        finally { held?.Dispose(); if (workspace is not null) { Directory.Delete(workspace, true); } }
    }
    [Fact]
    public async Task RootPinsRejectJunctionSourcesAndDestinationParents_WithoutEscaping()
    {
        var outside = Directory.CreateDirectory(At("outside")).FullName; File.WriteAllText(Path.Combine(outside, "keep"), "unchanged");
        Junction(At("junction"), outside);
        var result = await new ArchiveService().ExecuteAsync(new(ArchiveOperation.CreateZip, [], At("new.zip"), At("junction")));
        Assert.Equal(ArchiveError.LinksNotSupported, result.Error);
        var destination = await new ArchiveService().ExecuteAsync(new(ArchiveOperation.Extract, [Fixture()], At("junction/result")));
        Assert.Equal(ArchiveError.LinksNotSupported, destination.Error); Assert.False(Directory.Exists(Path.Combine(outside, "result")));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(outside, "keep")));
    }
    [Fact]
    public void PinnedAncestorsCannotBeRenamedDuringWriting_AndCleanupDoesNotFollowJunctions()
    {
        using var files = new ArchiveFileSystem();
        files.MakeDirectory(At("owned"), rename: true); files.EnsureParents(At("owned"), "sub/file");
        Assert.Throws<IOException>(() => Directory.Move(At("owned/sub"), At("moved")));
        var target = Directory.CreateDirectory(At("unrelated")).FullName; File.WriteAllText(Path.Combine(target, "keep"), "unchanged");
        Junction(At("owned/link"), target);
        Assert.True(files.CleanupOwnedDirectory(At("owned"))); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(target, "keep")));
    }
    [Fact]
    public void ReparseMutationAfterPin_IsRefusedWithoutWritingIntoTarget()
    {
        var target = Directory.CreateDirectory(At("outside")).FullName;
        using var files = new ArchiveFileSystem();
        files.MakeDirectory(At("owned"), rename: true); files.EnsureParents(At("owned"), "sub/file");
        Junction(At("owned/sub"), target);
        Assert.Equal(ArchiveError.LinksNotSupported, Assert.Throws<ArchiveRejected>(() => files.NewFile(At("owned/sub/escape"))).Error);
        Assert.False(File.Exists(Path.Combine(target, "escape")));
        Assert.True(files.CleanupOwnedDirectory(At("owned")));
    }
    [Fact]
    public async Task EmptyTarLongUnicodeAndBothRepackSources_Work()
    {
        var empty = await new ArchiveService().ExecuteAsync(new(ArchiveOperation.CreateTar, [], At("empty.tar")));
        Assert.True(empty.IsSuccess, empty.ToString());
        var name = new string('ع', 60) + ".txt"; File.WriteAllText(At(name), "data");
        var tar = await new ArchiveService().ExecuteAsync(new(ArchiveOperation.CreateTar, [At(name)], At("long.tar")));
        Assert.True(tar.IsSuccess, tar.ToString());
        var zip = Fixture("folder/data");
        Assert.True((await new ArchiveService().ExecuteAsync(new(ArchiveOperation.RepackZip, [zip], At("repack.zip")))).IsSuccess);
    }
    [Fact]
    public void SevenZipHeaderGuard_BoundsAllocationBeforeLibraryParsing()
    {
        using var input = new MemoryStream(Convert.FromHexString("377abcaf271c000281ca39520000000000000000000000040000000000000000"));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(ArchiveError.TooLarge, Assert.Throws<ArchiveRejected>(() => ArchiveContainerGuard.Read(input)).Error);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 256 * 1024);
    }
    private sealed class DelegateRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>> run) : IProcessRunner
    { public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token) => run(request, token); }
    private sealed class SynchronousProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }

    // Real Windows junction fixture; no developer mode, privilege change, shell or archive engine.
    private static void Junction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target); var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + print.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xa0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(data.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        Assert.True(DeviceIoControl(handle, 0x000900a4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero), "Junction fixture creation failed: " + Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int length, IntPtr output, int capacity, out int returned, IntPtr overlapped);
}
