using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Batch;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class BatchQueueTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "OrbitConvert.Tests", "batch-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string name) => Path.Combine(_root, name);
    public void Dispose() => Directory.Delete(_root, true);
    private string Source(string name) { File.WriteAllText(At(name), "original"); return At(name); }
    private BatchViewModel Model(IBatchExecutor? executor = null, int maximum = 100)
    {
        var model = new BatchViewModel([], executor ?? new DelegateExecutor((r, _, _) => Task.FromResult(new BatchOutcome(OutputPath: r.Destination))),
            new LocalizationService(new TestLanguagePreferenceStore()), maximumJobs: maximum);
        model.SetDestinationFolder(_root); return model;
    }
    [Fact]
    public async Task Fifo_MixedPresets_OneFailureDoesNotStopLaterJobs()
    {
        var order = new List<string>();
        var executor = new DelegateExecutor((r, _, _) => { order.Add(Path.GetFileName(r.Source)); return Task.FromResult(r.Source.EndsWith("bad.png", StringComparison.Ordinal) ? new BatchOutcome("ImageErrorInvalidOrCorruptImage") : new BatchOutcome(OutputPath: r.Destination)); });
        var model = Model(executor);
        model.AddFiles([Source("first.png"), Source("bad.png")]);
        model.PresetIndex = 5; model.AddFiles([Source("last.wav")]);
        Assert.All(model.Jobs, x => Assert.Equal(BatchState.Pending, x.State)); Assert.Empty(order);
        await model.StartCommand.ExecuteAsync();
        Assert.Equal(new[] { "first.png", "bad.png", "last.wav" }, order);
        Assert.Equal(new[] { BatchState.Completed, BatchState.Failed, BatchState.Completed }, model.Jobs.Select(x => x.State));
        Assert.False(model.IsBusy); Assert.False(model.HasCleanupWarning);
    }
    [Fact]
    public void QueueLimit_EmptyAndDuplicateSelections_AreAtomic()
    {
        var model = Model(maximum: 2); model.AddFiles([]); Assert.Empty(model.Jobs);
        var one = Source("one.png"); model.AddFiles([one, one]); Assert.Empty(model.Jobs);
        model.AddFiles([one]); model.AddFiles([one]); Assert.Single(model.Jobs);
        model.AddFiles([Source("two.png"), Source("three.png")]); Assert.Single(model.Jobs);
        model.AddFiles([At("two.png")]); Assert.Equal(2, model.Jobs.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Model(maximum: 0));
    }
    [Fact]
    public void WindowsCaseInsensitiveDestinations_AndExistingOutputsAreRefused()
    {
        var model = Model(); var source = Source("one.png"); model.AddFiles([source]);
        var folder = Directory.CreateDirectory(At("other")).FullName; File.WriteAllText(Path.Combine(folder, "ONE.jpg"), "data");
        model.AddFiles([Path.Combine(folder, "ONE.jpg")]); Assert.Single(model.Jobs);
        File.WriteAllText(At("exists-orbitconvert.png"), "keep"); model.AddFiles([Source("exists.jpg")]); Assert.Single(model.Jobs);
        Assert.Equal("keep", File.ReadAllText(At("exists-orbitconvert.png")));
    }
    [Fact]
    public async Task UnicodePaths_AndImmutableSettingsRemainThroughLanguageChanges()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var completion = new TaskCompletionSource<BatchOutcome>();
        var model = new BatchViewModel([], new DelegateExecutor((_, _, _) => completion.Task), localization);
        model.SetDestinationFolder(_root); model.AddFiles([Source("مرحبا mixed ' &.png")]); var job = model.Jobs.Single(); var id = job.Id; var request = job.Request;
        var run = model.StartCommand.ExecuteAsync();
        localization.ChangeLanguage(AppLanguage.Arabic); Assert.Equal("جارٍ الفحص", job.StateLabel);
        model.PresetIndex = 6; model.SetDestinationFolder("ignored");
        Assert.Same(request, job.Request); Assert.Equal(id, job.Id); Assert.Equal(0, model.PresetIndex); Assert.Equal(_root, model.DestinationFolder);
        completion.SetResult(new(OutputPath: request.Destination)); await run;
        localization.ChangeLanguage(AppLanguage.English); Assert.Equal("Completed", job.StateLabel); Assert.Equal(id, job.Id);
    }
    [Fact]
    public async Task CancelPending_DoesNotStartItOrCancelOtherJobs()
    {
        var calls = 0; var model = Model(new DelegateExecutor((r, _, _) => { calls++; return Task.FromResult(new BatchOutcome(OutputPath: r.Destination)); }));
        model.AddFiles([Source("one.png"), Source("two.png")]); model.CancelJob(model.Jobs[0]);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(1, calls);
        Assert.Equal(BatchState.Cancelled, model.Jobs[0].State); Assert.Equal(BatchState.Completed, model.Jobs[1].State);
        Assert.Empty(model.Jobs[0].CancellationText);
    }
    [Fact]
    public async Task CancelRunning_WaitsForCleanupAndThenContinues()
    {
        var completion = new TaskCompletionSource<BatchOutcome>(); var calls = 0; CancellationToken active = default;
        var model = Model(new DelegateExecutor((r, _, token) => { calls++; active = token; return calls == 1 ? completion.Task : Task.FromResult(new BatchOutcome(OutputPath: r.Destination)); }));
        model.AddFiles([Source("one.png"), Source("two.png")]); var run = model.StartCommand.ExecuteAsync(); var idle = model.WaitForIdleAsync();
        model.CancelJob(model.Jobs[0]); Assert.True(active.IsCancellationRequested); Assert.False(idle.IsCompleted); Assert.True(model.IsBusy); Assert.Equal(1, calls);
        completion.SetResult(new(Cancelled: true)); await run; await idle;
        Assert.Equal(2, calls); Assert.Equal(BatchState.Cancelled, model.Jobs[0].State); Assert.Equal(BatchState.Completed, model.Jobs[1].State);
    }
    [Fact]
    public async Task CancelAll_StopsSchedulingImmediatelyAndWaitsForActiveCleanup()
    {
        var completion = new TaskCompletionSource<BatchOutcome>(); var calls = 0;
        var model = Model(new DelegateExecutor((_, _, _) => { calls++; return completion.Task; }));
        model.AddFiles([Source("one.png"), Source("two.png"), Source("three.png")]); var run = model.StartCommand.ExecuteAsync();
        model.RequestCancellation(); Assert.Equal(1, calls); Assert.All(model.Jobs.Skip(1), x => Assert.Equal(BatchState.Cancelled, x.State));
        Assert.False(model.WaitForIdleAsync().IsCompleted); completion.SetResult(new(Cancelled: true)); await run;
        Assert.All(model.Jobs, x => Assert.Equal(BatchState.Cancelled, x.State)); Assert.False(model.IsBusy);
    }
    [Fact]
    public async Task DuplicateStart_AndLateProgress_DoNotExecuteOrChangeFinishedJob()
    {
        var completion = new TaskCompletionSource<BatchOutcome>(); IProgress<BatchProgress>? reports = null; var calls = 0;
        var model = Model(new DelegateExecutor((_, p, _) => { calls++; reports = p; return completion.Task; }));
        model.AddFiles([Source("one.png")]); var run = model.StartCommand.ExecuteAsync(); await model.StartCommand.ExecuteAsync(); Assert.Equal(1, calls);
        reports!.Report(new(BatchState.Running, 0.5)); completion.SetResult(new(OutputPath: model.Jobs[0].Destination)); await run;
        reports.Report(new(BatchState.Inspecting, 0)); await Task.Delay(50);
        Assert.Equal(BatchState.Completed, model.Jobs[0].State); Assert.Equal(100, model.Jobs[0].ProgressPercent);
    }
    [Fact]
    public async Task RetryFailed_RequiresExplicitRevalidationAndPreservesIdentity()
    {
        var calls = 0; var model = Model(new DelegateExecutor((r, _, _) => Task.FromResult(++calls == 1 ? new BatchOutcome("BatchUnreadableInput") : new BatchOutcome(OutputPath: r.Destination))));
        var source = Source("one.png"); model.AddFiles([source]); var id = model.Jobs[0].Id;
        await model.StartCommand.ExecuteAsync(); Assert.Equal(BatchState.Failed, model.Jobs[0].State); Assert.Equal(1, calls);
        File.WriteAllText(source, "updated original"); model.RetryFailed(); Assert.Equal(BatchState.Pending, model.Jobs[0].State);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(2, calls); Assert.Equal(id, model.Jobs[0].Id); Assert.Equal(BatchState.Completed, model.Jobs[0].State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedOrChangedSource_IsNotExecuted_AndNextJobContinues(bool delete)
    {
        var calls = 0; var model = Model(new DelegateExecutor((r, _, _) => { calls++; return Task.FromResult(new BatchOutcome(OutputPath: r.Destination)); }));
        var source = Source("one.png"); model.AddFiles([source, Source("two.png")]);
        if (delete) { File.Delete(source); } else { File.AppendAllText(source, "changed"); }
        await model.StartCommand.ExecuteAsync(); Assert.Equal(1, calls); Assert.Equal(BatchState.Failed, model.Jobs[0].State); Assert.Equal(BatchState.Completed, model.Jobs[1].State);
    }
    [Fact]
    public async Task SourceChangedWhileEarlierJobRuns_IsRecheckedBeforeOwnExecution()
    {
        var source = Source("second.png"); var completion = new TaskCompletionSource<BatchOutcome>(); var calls = 0;
        var model = Model(new DelegateExecutor((_, _, _) => { calls++; return completion.Task; }));
        model.AddFiles([Source("first.png"), source]); var run = model.StartCommand.ExecuteAsync();
        File.AppendAllText(source, "modified"); completion.SetResult(new(OutputPath: model.Jobs[0].Destination)); await run;
        Assert.Equal(1, calls); Assert.Equal("BatchSourceChanged", model.Jobs[1].ErrorKey);
    }
    [Fact]
    public async Task DestinationAppearsAfterQueueing_IsPreservedAndNotExecuted()
    {
        var calls = 0; var model = Model(new DelegateExecutor((_, _, _) => { calls++; return Task.FromResult(new BatchOutcome()); }));
        model.AddFiles([Source("one.png")]); File.WriteAllText(model.Jobs[0].Destination, "winner"); await model.StartCommand.ExecuteAsync();
        Assert.Equal(0, calls); Assert.Equal("BatchDestinationExists", model.Jobs[0].ErrorKey); Assert.Equal("winner", File.ReadAllText(model.Jobs[0].Destination));
    }
    [Theory]
    [InlineData("VideoErrorProcessTerminationFailed")]
    [InlineData("DocumentErrorOutputFailure")]
    public async Task UnconfirmedTerminationOrCleanupFailure_StopsQueueAndBlocksRetry(string error)
    {
        var calls = 0; var model = Model(new DelegateExecutor((_, _, _) => { calls++; return Task.FromResult(new BatchOutcome(error, CleanupWarning: true)); }));
        model.AddFiles([Source("one.png"), Source("two.png")]); await model.StartCommand.ExecuteAsync();
        Assert.Equal(1, calls); Assert.True(model.HasCleanupWarning); Assert.Equal(BatchState.Pending, model.Jobs[1].State);
        Assert.False(model.StartCommand.CanExecute(null)); model.RetryFailed(); Assert.Equal(BatchState.Failed, model.Jobs[0].State);
        await model.ClearErrorsCommand.ExecuteAsync(); Assert.True(model.HasCleanupWarning); Assert.False(model.StartCommand.CanExecute(null));
    }
    [Fact]
    public async Task CompletionThatWinsCancellationRace_IsReportedAsCompleted()
    {
        var completion = new TaskCompletionSource<BatchOutcome>(); var model = Model(new DelegateExecutor((_, _, _) => completion.Task));
        model.AddFiles([Source("one.png")]); var run = model.StartCommand.ExecuteAsync(); model.RequestCancellation();
        completion.SetResult(new(OutputPath: model.Jobs[0].Destination)); await run; Assert.Equal(BatchState.Completed, model.Jobs[0].State);
    }
    [Fact]
    public async Task OtherPanelsAndThemeNavigation_AreCoordinatedWithoutResettingQueue()
    {
        var completion = new TaskCompletionSource<BatchOutcome>();
        var model = new MainViewModel(new FileMetadataProvider(), new LocalizationService(new TestLanguagePreferenceStore()), batchExecutor: new DelegateExecutor((_, _, _) => completion.Task));
        model.Batch.SetDestinationFolder(_root); model.Batch.AddFiles([Source("one.png")]); var id = model.Batch.Jobs[0].Id;
        var run = model.Batch.StartCommand.ExecuteAsync();
        Assert.False(model.CanUseImages); Assert.False(model.CanUseVideo); Assert.False(model.CanUseAudio); Assert.False(model.CanUseDocuments); Assert.False(model.CanUsePdf); Assert.False(model.CanUseArchives);
        model.SetThemeCommand.Execute(AppTheme.Light); model.SetLanguageCommand.Execute(AppLanguage.Arabic); model.NavigateCommand.Execute(AppSection.Settings);
        Assert.Equal(id, model.Batch.Jobs[0].Id); Assert.True(model.Batch.IsBusy); Assert.Equal(AppTheme.Light, model.SelectedTheme);
        // Close -> No leaves the running queue unchanged. Close -> Yes uses these exact APIs.
        Assert.False(model.Batch.WaitForIdleAsync().IsCompleted); model.Batch.RequestCancellation(); completion.SetResult(new(Cancelled: true));
        await model.Batch.WaitForIdleAsync(); await run; Assert.True(model.CanUseImages); Assert.True(model.CanUseArchives);
    }
    [Fact]
    public async Task CleanupUncertainty_DoesNotReenableIndividualEnginesAfterQueueBecomesIdle()
    {
        var model = new MainViewModel(new FileMetadataProvider(), new LocalizationService(new TestLanguagePreferenceStore()),
            batchExecutor: new DelegateExecutor((_, _, _) => Task.FromResult(new BatchOutcome("BatchUnexpectedFailure", CleanupWarning: true))));
        model.Batch.SetDestinationFolder(_root); model.Batch.AddFiles([Source("one.png")]);
        await model.Batch.StartCommand.ExecuteAsync();
        await model.Batch.ClearErrorsCommand.ExecuteAsync();
        Assert.True(model.Batch.IsIdle); Assert.True(model.Batch.HasCleanupWarning);
        Assert.False(model.CanUseImages); Assert.False(model.CanUseVideo); Assert.False(model.CanUseAudio);
        Assert.False(model.CanUseDocuments); Assert.False(model.CanUsePdf); Assert.False(model.CanUseArchives);
    }
    [Fact]
    public void EveryPresetAndJobState_HasBothTranslations_AndUnsupportedMatrixIsRefused()
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Arabic })
        {
            var localization = new LocalizationService(new TestLanguagePreferenceStore()); localization.ChangeLanguage(language);
            foreach (var preset in BatchPreset.All) { Assert.NotEqual("BatchMode" + preset.Key, localization["BatchMode" + preset.Key]); }
            foreach (var state in Enum.GetValues<BatchState>()) { Assert.NotEqual("BatchState" + state, localization["BatchState" + state]); }
        }
        Assert.False(BatchPreset.All.Single(x => x.Key == "DocumentDOCX").Accepts("source.docx"));
        Assert.True(BatchPreset.All.Single(x => x.Key == "DocumentDOCX").Accepts("source.md"));
        var model = Model(); model.AddFiles([Source("unsupported.pdf")]); Assert.Empty(model.Jobs);
    }
    internal sealed class DelegateExecutor(Func<BatchRequest, IProgress<BatchProgress>, CancellationToken, Task<BatchOutcome>> run) : IBatchExecutor
    { public Task<BatchOutcome> ExecuteAsync(BatchRequest request, IProgress<BatchProgress> progress, CancellationToken token) => run(request, progress, token); }
}
