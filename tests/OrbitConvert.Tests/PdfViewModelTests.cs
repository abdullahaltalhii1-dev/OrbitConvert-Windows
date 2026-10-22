using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Pdf;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class PdfViewModelTests
{
    private static readonly SelectedFileItem First = new(@"C:\عربي\first.pdf", "first.pdf", "PDF", 100, "100 B");
    private static readonly SelectedFileItem Second = new(@"C:\عربي\second.pdf", "second.pdf", "PDF", 200, "200 B");
    private static readonly PdfInspection Inspection = new("test.pdf", 100, "1.7", false, "عنوان", "Author", [new(1, 200, 300, 0, "A"), new(2, 200, 300, 0, "B"), new(3, 200, 300, 0, "C")]);
    [Fact]
    public async Task MergeOrderDuplicatePreventionAndTotalCount_AreActualState()
    {
        var service = new Stub(); var model = Create(service);
        model.SelectedFile = First; model.AddMergeCommand.Execute(null); model.AddMergeCommand.Execute(null);
        model.SelectedFile = Second; model.AddMergeCommand.Execute(null);
        Assert.Equal(2, model.MergeFiles.Count);
        await model.InspectCommand.ExecuteAsync(); Assert.Equal(6, model.PageCount);
        model.MergeSelection = Second; model.MoveUpCommand.Execute(null);
        Assert.Equal(Second, model.MergeFiles[0]);
        model.SetDestination(@"C:\new.pdf"); await model.StartCommand.ExecuteAsync();
        Assert.Equal(new[] { Second.FullPath, First.FullPath }, service.Request!.Sources);
        Assert.Equal(PdfOperation.Merge, service.Request.Operation); Assert.Contains("saved and verified", model.StatusMessage);
        Assert.False(model.StartCommand.CanExecute(null));
    }
    [Fact]
    public async Task SelectionBoundariesAndDirectoryDestinations_AreValidated()
    {
        var model = Create(new Stub()); model.ModeIndex = 1; model.SelectedFile = First;
        await model.InspectCommand.ExecuteAsync(); Assert.True(model.CanChooseDestination);
        model.PagesText = "4"; Assert.False(model.CanChooseDestination);
        model.PagesText = "3,1"; Assert.True(model.CanChooseDestination);
        model.SetDestination(@"C:\new.pdf"); model.SplitModeIndex = 2;
        Assert.False(model.CanChooseDestination); Assert.Empty(model.DestinationPath);
        model.PagesText = "1,2"; Assert.True(model.CanChooseDestination); Assert.True(model.DirectoryOutput);
        model.ModeIndex = 3; Assert.False(model.HasInspection);
        await model.InspectCommand.ExecuteAsync(); model.PagesText = "2"; Assert.False(model.DirectoryOutput);
        model.PagesText = "1-3"; Assert.True(model.DirectoryOutput);
    }
    [Fact]
    public async Task BusyStateFreezesOptions_BlocksEveryOtherEngine_AndWaitsForCleanup()
    {
        var completion = new TaskCompletionSource<PdfResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Stub { Run = (_, _) => completion.Task };
        var main = new MainViewModel(new FileMetadataProvider(), new(new TestLanguagePreferenceStore()), pdfService: service);
        main.FileIntake.SelectedFiles.Add(First);
        var model = main.Pdf; model.ModeIndex = 2; model.SelectedFile = First;
        await model.InspectCommand.ExecuteAsync(); model.PagesText = "3,1"; model.SetDestination(@"C:\new.pdf");
        var running = model.StartCommand.ExecuteAsync();
        Assert.False(main.CanUseImages); Assert.False(main.CanUseVideo); Assert.False(main.CanUseAudio); Assert.False(main.CanUseDocuments);
        model.ModeIndex = 4; model.PagesText = "1"; model.Dpi = 300; model.SelectedFile = null;
        Assert.Equal(2, model.ModeIndex); Assert.Equal("3,1", model.PagesText); Assert.Equal(150, model.Dpi); Assert.Equal(First, model.SelectedFile);
        await model.StartCommand.ExecuteAsync(); Assert.Equal(1, service.Calls);
        var idle = model.WaitForIdleAsync(); model.RequestCancellation();
        Assert.True(service.Token.IsCancellationRequested); Assert.False(idle.IsCompleted);
        completion.SetResult(new(PdfError.Cancelled)); await running; await idle;
        Assert.True(main.CanUseImages); Assert.True(model.IsIdle); Assert.Contains("cancelled", model.StatusMessage);
    }
    [Fact]
    public async Task BilingualErrorsCleanupAndModes_HaveCompleteResources()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var service = new Stub { Run = (_, _) => Task.FromResult(new PdfResult(PdfError.ProcessTerminationFailed, CleanupFailed: true)) };
        var model = Create(service, localization); model.ModeIndex = 4; model.SelectedFile = First;
        await model.InspectCommand.ExecuteAsync(); model.SetDestination(@"C:\new.txt"); await model.StartCommand.ExecuteAsync();
        Assert.True(model.HasCleanupWarning); localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("توقف", model.StatusMessage); Assert.Contains("تنظيف", model.StatusMessage);
        Assert.Contains("دمج", model.ModeLabels[0]);
        foreach (var error in Enum.GetValues<PdfError>().Where(x => x != PdfError.None))
        { Assert.True(localization.HasTranslation("PdfError" + error, AppLanguage.Arabic)); Assert.True(localization.HasTranslation("PdfError" + error, AppLanguage.English)); }
    }
    private static PdfViewModel Create(Stub service, LocalizationService? localization = null) => new(new ObservableCollection<SelectedFileItem> { First, Second }, service, localization ?? new(new TestLanguagePreferenceStore()));
    private sealed class Stub : IPdfService
    {
        public Func<PdfRequest, CancellationToken, Task<PdfResult>>? Run { get; init; }
        public PdfRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<PdfInspectionResult> InspectAsync(string path, CancellationToken token) => Task.FromResult(new PdfInspectionResult(Inspection, PdfError.None));
        public Task<PdfResult> ExecuteAsync(PdfRequest request, IProgress<PdfProgress>? progress, CancellationToken token)
        { Request = request; Token = token; Calls++; return Run?.Invoke(request, token) ?? Task.FromResult(new PdfResult(PdfError.None, [request.Destination])); }
    }
}
