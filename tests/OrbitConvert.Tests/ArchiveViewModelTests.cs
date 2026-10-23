using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Archives;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class ArchiveViewModelTests
{
    private static readonly SelectedFileItem Source = new(@"C:\عربي\sample.zip", "sample.zip", "ZIP", 100, "100 B");
    private static readonly ArchiveInspection Inspection = new("ZIP", 100, 12, [new("مجلد/نص.txt", false, 12, 8, new string('A', 64))]);
    [Fact]
    public async Task SelectionDoesNotProcess_InspectionIsExplicit_AndLanguageKeepsModesAndContents()
    {
        var service = new Stub(); var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = new ArchiveViewModel(new([Source]), service, localization);
        model.SelectedFile = Source; Assert.Equal(0, service.Inspections); Assert.False(model.CanChooseDestination);
        await model.InspectCommand.ExecuteAsync(); Assert.Equal(1, service.Inspections); Assert.True(model.HasInspection);
        model.SetDestination(@"C:\new-folder"); Assert.True(model.StartCommand.CanExecute(null));
        var option = model.ModeOptions[0]; localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Same(option, model.ModeOptions[0]); Assert.Contains("استخراج", option.Label); Assert.Contains("نص.txt", model.ContentRows.Single());
        Assert.Equal(@"C:\new-folder", model.DestinationPath);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(ArchiveOperation.Extract, service.Request!.Operation);
        Assert.Contains("حُفظت", model.StatusMessage); Assert.False(model.StartCommand.CanExecute(null));
    }
    [Fact]
    public async Task CreationSelectionDuplicatesAndFolder_AreExplicitAndInvalidateDestination()
    {
        var service = new Stub(); var files = new ObservableCollection<SelectedFileItem> { Source };
        var model = new ArchiveViewModel(files, service, new(new TestLanguagePreferenceStore())) { ModeIndex = 1, SelectedFile = Source };
        model.AddCommand.Execute(null); model.AddCommand.Execute(null); Assert.Single(model.CreationFiles);
        Assert.False(model.InspectCommand.CanExecute(null)); model.SetDestination(@"C:\new.zip"); model.SetFolder(@"C:\مجلد");
        Assert.Empty(model.DestinationPath); model.SetDestination(@"C:\new.zip"); await model.StartCommand.ExecuteAsync();
        Assert.Equal(new[] { Source.FullPath }, service.Request!.Sources); Assert.Equal(@"C:\مجلد", service.Request.FolderSource);
        model.ModeIndex = 2; Assert.Empty(model.DestinationPath); Assert.Equal(".tar", model.OutputExtension);
        model.CreationSelection = Source; model.RemoveCommand.Execute(null); Assert.Empty(model.CreationFiles);
        model.ClearCommand.Execute(null); Assert.Empty(model.FolderSource);
    }
    [Fact]
    public async Task ArchiveBusyState_DisablesOtherEnginesAndWaitsForCancellationCleanup()
    {
        var completion = new TaskCompletionSource<ArchiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Stub { Run = (_, _) => completion.Task };
        var main = new MainViewModel(new FileMetadataProvider(), new(new TestLanguagePreferenceStore()), archiveService: service);
        main.FileIntake.SelectedFiles.Add(Source); var model = main.Archives; model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync(); model.SetDestination(@"C:\new-folder"); var running = model.StartCommand.ExecuteAsync();
        Assert.False(main.CanUseImages); Assert.False(main.CanUseVideo); Assert.False(main.CanUseAudio); Assert.False(main.CanUseDocuments); Assert.False(main.CanUsePdf);
        model.ModeIndex = 1; model.SelectedFile = null; model.SetFolder(@"C:\ignored"); Assert.Equal(0, model.ModeIndex); Assert.Equal(Source, model.SelectedFile);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(1, service.Calls);
        var waiting = model.WaitForIdleAsync(); model.RequestCancellation(); Assert.True(service.Token.IsCancellationRequested); Assert.False(waiting.IsCompleted);
        completion.SetResult(new(ArchiveError.Cancelled, CleanupFailed: true)); await running; await waiting;
        Assert.True(model.IsIdle); Assert.True(model.HasCleanupWarning); Assert.True(main.CanUseImages); Assert.True(main.CanUsePdf);
        main.Localization.ChangeLanguage(AppLanguage.Arabic); Assert.Contains("تنظيف", model.StatusMessage);
    }
    [Fact]
    public void InlineCancellationCompletion_KeepsFinalStatus()
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var completion = new TaskCompletionSource<ArchiveResult>();
            var service = new Stub { Run = (_, token) => { token.Register(() => completion.SetCanceled(token)); return completion.Task; } };
            var model = new ArchiveViewModel(new([Source]), service, new(new TestLanguagePreferenceStore())) { ModeIndex = 1 };
            model.SetDestination(@"C:\new.zip");
            var running = model.StartCommand.ExecuteAsync();
            model.RequestCancellation();
            Assert.True(running.IsCompletedSuccessfully);
            Assert.True(model.IsIdle);
            Assert.Contains("cancelled", model.StatusMessage);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [Fact]
    public void ArchiveErrorsAndStages_HaveBothLanguageResources()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        foreach (var error in Enum.GetValues<ArchiveError>().Where(x => x != ArchiveError.None))
        {
            Assert.True(localization.HasTranslation("ArchiveError" + error, AppLanguage.English));
            Assert.True(localization.HasTranslation("ArchiveError" + error, AppLanguage.Arabic));
        }
    }
    private sealed class Stub : IArchiveService
    {
        public Func<ArchiveRequest, CancellationToken, Task<ArchiveResult>>? Run { get; init; }
        public ArchiveRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public int Inspections { get; private set; }
        public Task<ArchiveResult> InspectAsync(string path, CancellationToken token) { Inspections++; return Task.FromResult(new ArchiveResult(ArchiveError.None, Inspection)); }
        public Task<ArchiveResult> ExecuteAsync(ArchiveRequest request, IProgress<string>? progress, CancellationToken token)
        { Request = request; Calls++; Token = token; return Run?.Invoke(request, token) ?? Task.FromResult(new ArchiveResult(ArchiveError.None, Inspection, request.Destination)); }
    }
}
