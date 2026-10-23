using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OrbitConvert.App.Services.Archives;
using OrbitConvert.App.Services.Media;

namespace OrbitConvert.Tests;

public sealed class ArchiveTests(Xunit.Abstractions.ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "arc-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string name) => Path.Combine(_root, name);
    private ArchiveService Service() => new(new DiagnosticRunner(log));
    [Fact]
    public void NativePublication_RenamesHeldFilesAndDirectoriesWithoutReplacing()
    {
        using var files = new ArchiveFileSystem();
        var parent = files.PinDirectory(_root)!;
        var file = files.NewFile(At("stage"), rename: true); file.WriteByte(1); file.Flush(true);
        ArchiveFileSystem.Publish(file.SafeFileHandle, parent, "published.zip");
        Assert.True(File.Exists(At("published.zip")));
        files.MakeDirectory(At("stage-dir"), rename: true);
        var nested = files.NewFile(At("stage-dir/child")); nested.WriteByte(2); nested.Flush(true);
        files.PublishDirectory(At("stage-dir"), parent, "published-dir");
        Assert.True(File.Exists(At("published-dir/child")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCodec_RoundTripsDirectoriesAndUnicode(bool tar)
    {
        using var files = new ArchiveFileSystem();
        File.WriteAllBytes(At("input"), [1, 2]);
        var records = new[] { new ArchiveCreateEntry("مجلد", "", true, 0, ""), new ArchiveCreateEntry("مجلد/file", "input", false, 2, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2 }))) };
        var result = ArchiveCodec.Create(At("result.archive"), records, _root, files, tar);
        Assert.Equal(2, result.Entries.Length);
    }
    public void Dispose() { Directory.Delete(_root, true); }
    private string Zip(params (string Name, byte[] Data)[] entries)
    {
        var path = At(Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var output = zip.CreateEntry(entry.Name).Open(); output.Write(entry.Data); }
        return path;
    }
    [Fact]
    public async Task ZipCreateInspectExtract_PreserveUnicodePathsAndBinaryHashes()
    {
        var binary = Enumerable.Range(0, 8192).Select(x => (byte)(x * 17)).ToArray();
        var source = At("عربي mixed.bin"); File.WriteAllBytes(source, binary);
        var archive = At("نتيجة.zip");
        var made = await Service().ExecuteAsync(new(ArchiveOperation.CreateZip, [source], archive));
        Assert.True(made.IsSuccess, made.ToString()); Assert.False(made.CleanupFailed);
        using (var zip = ZipFile.OpenRead(archive)) { using var data = zip.Entries.Single().Open(); Assert.Equal(SHA256.HashData(binary), SHA256.HashData(data)); }
        var before = File.ReadAllBytes(archive);
        var inspected = await Service().InspectAsync(archive);
        Assert.True(inspected.IsSuccess, inspected.ToString());
        Assert.Equal("عربي mixed.bin", inspected.Inspection!.Entries.Single().Path);
        var destination = At("مجلد جديد");
        var extracted = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [archive], destination));
        Assert.True(extracted.IsSuccess, extracted.ToString()); Assert.False(extracted.CleanupFailed);
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(destination, "عربي mixed.bin")));
        Assert.Equal(binary, File.ReadAllBytes(source)); Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Empty(Directory.GetFileSystemEntries(_root, ".orbitconvert-*"));
    }
    [Fact]
    public async Task ZipNestedAndEmptyDirectories_AndExplicitFolderCreation_Work()
    {
        var folder = Directory.CreateDirectory(At("مجلد")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "نص.txt"), "مرحبا بالعالم English 123", Encoding.UTF8);
        var made = await Service().ExecuteAsync(new(ArchiveOperation.CreateZip, [], At("folder.zip"), folder));
        Assert.True(made.IsSuccess, made.ToString()); Assert.False(made.CleanupFailed);
        var extracted = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [At("folder.zip")], At("extracted")));
        Assert.True(extracted.IsSuccess, extracted.ToString());
        Assert.True(Directory.Exists(At("extracted/مجلد/empty")));
        Assert.Equal("مرحبا بالعالم English 123", File.ReadAllText(At("extracted/مجلد/nested/نص.txt")));
    }
    [Fact]
    public async Task EmptyZip_IsAValidEmptyResult()
    {
        var made = await Service().ExecuteAsync(new(ArchiveOperation.CreateZip, [], At("empty.zip")));
        Assert.True(made.IsSuccess, made.ToString()); Assert.Empty(made.Inspection!.Entries);
        var extracted = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [At("empty.zip")], At("empty")));
        Assert.True(extracted.IsSuccess, extracted.ToString()); Assert.Empty(Directory.GetFileSystemEntries(At("empty")));
    }
    [Fact]
    public async Task TarCreateExtractRepack_ReopensRealArchivesWithUnicode()
    {
        var source = At("بيانات.bin"); var bytes = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(source, bytes);
        var made = await Service().ExecuteAsync(new(ArchiveOperation.CreateTar, [source], At("created.tar")));
        Assert.True(made.IsSuccess, made.ToString()); Assert.False(made.CleanupFailed);
        using (var file = File.OpenRead(At("created.tar")))
        using (var reader = new TarReader(file)) { var entry = reader.GetNextEntry(); Assert.Equal("بيانات.bin", entry!.Name); Assert.Equal(4, entry.Length); Assert.Null(reader.GetNextEntry()); }
        var extracted = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [At("created.tar")], At("tar-result")));
        Assert.True(extracted.IsSuccess, extracted.ToString()); Assert.Equal(bytes, File.ReadAllBytes(At("tar-result/بيانات.bin")));
        var repack = await Service().ExecuteAsync(new(ArchiveOperation.RepackZip, [At("created.tar")], At("repacked.zip")));
        Assert.True(repack.IsSuccess, repack.ToString());
        using var zip = ZipFile.OpenRead(At("repacked.zip")); using var data = zip.Entries.Single().Open(); Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(data));
    }
    [Fact]
    public async Task RealSevenZip_InspectExtractAndRepack_AreVerified()
    {
        var archive = At("real.7z"); var bytes = Encoding.UTF8.GetBytes("مرحبا mixed English 123");
        using (var output = File.Create(archive))
        using (var writer = new SharpCompress.Writers.SevenZip.SevenZipWriter(output, new SharpCompress.Writers.SevenZip.SevenZipWriterOptions(SharpCompress.Common.CompressionType.LZMA)))
        { using var input = new MemoryStream(bytes); writer.Write("مجلد/نص.txt", input, null); }
        var inspected = await Service().InspectAsync(archive);
        Assert.True(inspected.IsSuccess, inspected.ToString()); Assert.Equal("7z", inspected.Inspection!.Format);
        var extracted = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [archive], At("seven")));
        Assert.True(extracted.IsSuccess, extracted.ToString()); Assert.Equal(bytes, File.ReadAllBytes(At("seven/مجلد/نص.txt")));
        var repacked = await Service().ExecuteAsync(new(ArchiveOperation.RepackZip, [archive], At("seven.zip")));
        Assert.True(repacked.IsSuccess, repacked.ToString());
    }
    [Fact]
    public async Task ClaimedSmallEntry_StillHitsActualStreamedByteLimit()
    {
        var archive = Zip(("large.bin", new byte[2 * 1024 * 1024]));
        var bytes = File.ReadAllBytes(archive);
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 1);
        File.WriteAllBytes(archive, bytes);
        using (var input = File.OpenRead(archive)) { Assert.Equal(1, ArchiveZipGuard.Read(input).Single().Size); }
        var result = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [archive], At("oversize")));
        Assert.Equal(ArchiveError.TooLarge, result.Error); Assert.False(result.CleanupFailed); Assert.False(Directory.Exists(At("oversize")));
    }
    [Fact]
    public async Task SevenZipAdvisoryHeader_AndTarLinks_AreRejectedBeforeExtraction()
    {
        var poc = Convert.FromHexString("377abcaf271c000281ca39520000000000000000000000040000000000000000");
        File.WriteAllBytes(At("allocation.7z"), poc);
        Assert.Equal(ArchiveError.TooLarge, (await Service().InspectAsync(At("allocation.7z"))).Error);
        using (var file = File.Create(At("link.tar")))
        using (var writer = new TarWriter(file)) { writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "../outside" }); }
        Assert.Equal(ArchiveError.LinksNotSupported, (await Service().InspectAsync(At("link.tar"))).Error);
    }
    [Fact]
    public async Task DestinationCreationRace_PreservesTheWinner()
    {
        var source = At("source.txt"); File.WriteAllText(source, "original");
        var progress = new InlineProgress(stage => { if (stage == "Verifying") { File.WriteAllText(At("race.zip"), "unrelated"); } });
        var result = await Service().ExecuteAsync(new(ArchiveOperation.CreateZip, [source], At("race.zip")), progress);
        Assert.Equal(ArchiveError.DestinationExists, result.Error); Assert.False(result.CleanupFailed);
        Assert.Equal("unrelated", File.ReadAllText(At("race.zip"))); Assert.Empty(Directory.GetFileSystemEntries(_root, ".orbitconvert-*"));
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("\\\\?\\C:\\device")]
    [InlineData("x:stream")]
    [InlineData("CON.txt")]
    [InlineData("com¹.txt")]
    [InlineData("a/.. /file")]
    [InlineData("a./file")]
    [InlineData("a//file")]
    [InlineData("a\\..\\escape")]
    public async Task HostileZipNames_AreRefusedWithoutPublishing(string name)
    {
        var archive = Zip((name, [1, 2, 3])); var before = File.ReadAllBytes(archive);
        var result = await Service().ExecuteAsync(new(ArchiveOperation.Extract, [archive], At("result")));
        Assert.False(result.IsSuccess); Assert.False(result.CleanupFailed); Assert.False(Directory.Exists(At("result")));
        Assert.Equal(before, File.ReadAllBytes(archive)); Assert.Empty(Directory.GetFileSystemEntries(_root, ".orbitconvert-*"));
    }
    [Theory]
    [InlineData("a.txt", "A.txt")]
    [InlineData("é.txt", "e\u0301.txt")]
    [InlineData("folder", "folder/a.txt")]
    public async Task DuplicateOrConflictingPaths_AreRejected(string first, string second)
    {
        var archive = Zip((first, [1]), (second, [2]));
        Assert.Equal(ArchiveError.DuplicateEntry, (await Service().InspectAsync(archive)).Error);
    }
    [Fact]
    public async Task CorruptionTruncationBombAndExistingDestination_AreRejected()
    {
        var archive = Zip(("zero.bin", new byte[2 * 1024 * 1024]));
        Assert.Equal(ArchiveError.TooLarge, (await Service().InspectAsync(archive)).Error);
        var good = Zip(("file", [1, 2, 3])); var data = File.ReadAllBytes(good); File.WriteAllBytes(At("truncated.zip"), data[..^1]);
        Assert.Equal(ArchiveError.InvalidArchive, (await Service().InspectAsync(At("truncated.zip"))).Error);
        data[30 + Encoding.UTF8.GetByteCount("file")] ^= 0x7f; File.WriteAllBytes(At("corrupt.zip"), data);
        Assert.Equal(ArchiveError.InvalidArchive, (await Service().InspectAsync(At("corrupt.zip"))).Error);
        Directory.CreateDirectory(At("existing")); File.WriteAllText(At("existing/keep.txt"), "unchanged");
        Assert.Equal(ArchiveError.DestinationExists, (await Service().ExecuteAsync(new(ArchiveOperation.Extract, [good], At("existing")))).Error);
        Assert.Equal("unchanged", File.ReadAllText(At("existing/keep.txt")));
    }
    private sealed class DiagnosticRunner(Xunit.Abstractions.ITestOutputHelper output) : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken token)
        {
            var result = await new ProcessRunner().RunAsync(request, token);
            if (!result.Succeeded) { output.WriteLine(result.Outcome + " " + result.ExitCode + "\n" + result.StandardOutput + "\n" + result.StandardError); }
            return result;
        }
    }
}
