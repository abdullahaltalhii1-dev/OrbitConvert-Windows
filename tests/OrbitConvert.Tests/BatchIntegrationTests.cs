using System.Security.Cryptography;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.Services.Batch;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class BatchIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "batch-real-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string name) => Path.Combine(_root, name);
    public void Dispose() => Directory.Delete(_root, true);
    private BatchViewModel Model(BatchExecutor? executor = null)
    {
        var model = new BatchViewModel([], executor ?? new BatchExecutor(), new LocalizationService(new TestLanguagePreferenceStore()));
        model.SetDestinationFolder(_root); return model;
    }
    [Fact]
    public async Task RealImages_CorruptInputDoesNotStopQueue_OutputsIndependentlyDecode()
    {
        File.WriteAllBytes(At("عربي mixed.png"), ImageFixtures.Png()); File.WriteAllBytes(At("English.jpg"), ImageFixtures.Jpeg());
        File.WriteAllText(At("bad.png"), "corrupt");
        var sources = new[] { At("عربي mixed.png"), At("bad.png"), At("English.jpg") }; var hashes = sources.Select(x => SHA256.HashData(File.ReadAllBytes(x))).ToArray();
        var model = Model(); model.PresetIndex = 1; model.AddFiles(sources); await model.StartCommand.ExecuteAsync();
        Assert.Equal(new[] { BatchState.Completed, BatchState.Failed, BatchState.Completed }, model.Jobs.Select(x => x.State));
        foreach (var job in model.Jobs.Where(x => x.State == BatchState.Completed))
        { var image = ImageFixtures.Decode(File.ReadAllBytes(job.ResultPath)); Assert.Equal(2, image.Width); Assert.Equal(3, image.Height); }
        for (var i = 0; i < sources.Length; i++) { Assert.Equal(hashes[i], SHA256.HashData(File.ReadAllBytes(sources[i]))); }
        Assert.Empty(Directory.GetFiles(_root, ".orbitconvert-*")); Assert.False(File.Exists(model.Jobs[1].Destination));
        await model.ClearCompletedCommand.ExecuteAsync(); Assert.Single(model.Jobs); Assert.Equal(BatchState.Failed, model.Jobs[0].State);
    }
    [Theory]
    [InlineData("Inspect")]
    [InlineData("Convert")]
    [InlineData("Validate")]
    public async Task ImageCancellation_BetweenRealNativeStages_DoesNotPublishAndCleans(string stage)
    {
        File.WriteAllBytes(At("source.png"), ImageFixtures.Png()); var before = File.ReadAllBytes(At("source.png"));
        using var stop = new CancellationTokenSource(); var engine = new ObservedImageEngine(stage, stop.Cancel);
        var service = new ImageConversionService(engine);
        var result = await service.ConvertAsync(At("source.png"), At("output.jpg"), "JPEG", new(), stop.Token);
        Assert.Equal(ImageInspectionError.Cancelled, result.Error); Assert.False(result.CleanupFailed);
        Assert.False(File.Exists(At("output.jpg"))); Assert.Empty(Directory.GetFiles(_root, ".orbitconvert-*")); Assert.Equal(before, File.ReadAllBytes(At("source.png")));
    }
    [Fact]
    public async Task DestinationRace_RealImageServicePreservesWinnerAndOwnedCleanup()
    {
        File.WriteAllBytes(At("source.png"), ImageFixtures.Png()); var before = File.ReadAllBytes(At("source.png"));
        var engine = new ObservedImageEngine("Convert", () => File.WriteAllText(At("source-orbitconvert.jpg"), "winner"));
        var model = Model(new BatchExecutor(images: new ImageConversionService(engine))); model.PresetIndex = 1; model.AddFiles([At("source.png")]);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(BatchState.Failed, model.Jobs[0].State); Assert.Equal("ImageErrorDestinationExists", model.Jobs[0].ErrorKey);
        Assert.Equal("winner", File.ReadAllText(At("source-orbitconvert.jpg"))); Assert.Equal(before, File.ReadAllBytes(At("source.png"))); Assert.Empty(Directory.GetFiles(_root, ".orbitconvert-*"));
    }
    [Fact]
    public async Task ImageCancellation_WithLockedOwnedTemporary_ReportsCleanupWarning()
    {
        File.WriteAllBytes(At("source.png"), ImageFixtures.Png()); using var stop = new CancellationTokenSource(); FileStream? locked = null;
        var engine = new ObservedImageEngine("Validate", () =>
        {
            locked = new FileStream(Directory.GetFiles(_root, ".orbitconvert-*.tmp").Single(), FileMode.Open, FileAccess.Read, FileShare.Read); stop.Cancel();
        });
        try
        {
            var result = await new ImageConversionService(engine).ConvertAsync(At("source.png"), At("output.jpg"), "JPEG", new(), stop.Token);
            Assert.Equal(ImageInspectionError.Cancelled, result.Error); Assert.True(result.CleanupFailed); Assert.False(File.Exists(At("output.jpg")));
        }
        finally { locked?.Dispose(); }
    }
    [FfmpegFact]
    public async Task RealVideoAndAudioQueue_UseExistingEnginesAndPreserveSources()
    {
        File.Copy(VideoFixtures.Standard(seconds: 1, size: "160x120"), At("فيديو mixed.mp4"));
        File.Copy(AudioConversionTests.Source(), At("صوت.wav"));
        var video = At("فيديو mixed.mp4"); var audio = At("صوت.wav"); var vhash = SHA256.HashData(File.ReadAllBytes(video)); var ahash = SHA256.HashData(File.ReadAllBytes(audio));
        var model = Model(new BatchExecutor(video: FfmpegTestTools.Service(), audio: new AudioConversionService(FfmpegTestTools.Locator(), new App.Services.Media.ProcessRunner())));
        model.PresetIndex = 4; model.AddFiles([video]); model.PresetIndex = 5; model.AddFiles([audio]); await model.StartCommand.ExecuteAsync();
        Assert.All(model.Jobs, x => Assert.True(x.State == BatchState.Completed, x.ErrorText));
        Assert.True((await FfmpegTestTools.Service().InspectAsync(model.Jobs[0].ResultPath)).IsSuccess);
        Assert.True((await new AudioConversionService(FfmpegTestTools.Locator(), new App.Services.Media.ProcessRunner()).InspectAsync(model.Jobs[1].ResultPath)).IsSuccess);
        Assert.Equal(vhash, SHA256.HashData(File.ReadAllBytes(video))); Assert.Equal(ahash, SHA256.HashData(File.ReadAllBytes(audio))); Assert.Empty(Directory.GetFiles(_root, ".orbitconvert-*"));
    }
    [PandocFact]
    public async Task RealDocumentQueue_RespectsExistingMatrixAndProducesUnicodeOutputs()
    {
        File.WriteAllText(At("مستند.md"), DocumentConversionTests.Markdown); File.WriteAllText(At("نص.txt"), "عربي English 123");
        var model = Model(); model.PresetIndex = 8; model.AddFiles([At("مستند.md")]); model.PresetIndex = 11; model.AddFiles([At("نص.txt")]);
        var before = new[] { File.ReadAllBytes(At("مستند.md")), File.ReadAllBytes(At("نص.txt")) };
        await model.StartCommand.ExecuteAsync(); Assert.All(model.Jobs, x => Assert.True(x.State == BatchState.Completed, x.ErrorText));
        foreach (var job in model.Jobs)
        {
            var inspection = await new DocumentConversionService().InspectAsync(job.ResultPath); Assert.True(inspection.IsSuccess, inspection.Error.ToString()); Assert.Contains("عربي", inspection.Inspection!.Text);
        }
        Assert.Equal(before[0], File.ReadAllBytes(At("مستند.md"))); Assert.Equal(before[1], File.ReadAllBytes(At("نص.txt"))); Assert.Empty(Directory.GetFiles(_root, ".orbitconvert-*"));
    }
    private sealed class ObservedImageEngine(string stage, Action observed) : IImageEngine
    {
        private readonly MagickImageEngine _engine = new();
        public ImageInspectionResult Inspect(Stream input) { var value = _engine.Inspect(input); if (stage == "Inspect") { observed(); } return value; }
        public ImageInspectionResult Convert(Stream input, Stream output, string format, ImageConversionOptions? options = null) { var value = _engine.Convert(input, output, format, options); if (stage == "Convert") { observed(); } return value; }
        public ImageInspectionResult ValidateOutput(Stream output, ImageInspection expected) { var value = _engine.ValidateOutput(output, expected); if (stage == "Validate") { observed(); } return value; }
    }
}
