using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class DocumentViewModelTests
{
    private static readonly SelectedFileItem Source = new(@"C:\مستندات خاصة\test.md", "test.md", "MD", 100, "100 B");
    private static readonly DocumentInspection Inspection = new(DocumentFormat.Markdown, 100, "عربي English", 1, 1, 1, true);
    [Fact]
    public async Task InspectionRestrictsMatrixAndChangingFormatClearsDestination()
    {
        var model = Create(new Stub());
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.SelectedFile = Source; await model.InspectCommand.ExecuteAsync();
        Assert.Equal(DocumentFormat.DOCX, model.Format);
        Assert.DoesNotContain(DocumentFormat.ODT, model.Outputs);
        model.SetDestination(@"C:\new.docx");
        Assert.True(model.ConvertCommand.CanExecute(null));
        model.Format = DocumentFormat.HTML;
        Assert.Empty(model.DestinationPath);
        model.SetDestination(@"C:\new.html");
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal(@"C:\new.html", model.CompletedPath);
        Assert.Contains("saved and verified", model.StatusMessage);
        Assert.False(model.ConvertCommand.CanExecute(null));
    }
    [Fact]
    public async Task BusyGuardsFreezeStateAndCancellationWaitsForActualCleanup()
    {
        var completion = new TaskCompletionSource<DocumentConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Stub { Conversion = (_, _) => completion.Task };
        var main = new MainViewModel(new FileMetadataProvider(), new(new TestLanguagePreferenceStore()), documentService: service);
        main.FileIntake.SelectedFiles.Add(Source);
        var model = main.DocumentConversion;
        model.SelectedFile = Source; await model.InspectCommand.ExecuteAsync(); model.SetDestination(@"C:\new.docx");
        var running = model.ConvertCommand.ExecuteAsync();
        Assert.False(main.CanUseImages); Assert.False(main.CanUseAudio); Assert.False(main.CanUseVideo);
        Assert.False(model.CanChooseDestination);
        model.SelectedFile = null; model.Format = DocumentFormat.HTML;
        Assert.Equal(Source, model.SelectedFile); Assert.Equal(DocumentFormat.DOCX, model.Format);
        await model.ConvertCommand.ExecuteAsync(); Assert.Equal(1, service.Calls);
        var idle = model.WaitForIdleAsync(); model.RequestCancellation();
        Assert.True(service.Token.IsCancellationRequested); Assert.False(idle.IsCompleted);
        completion.SetResult(new(DocumentError.Cancelled)); await running; await idle;
        Assert.True(main.CanUseImages); Assert.Contains("cancelled", model.StatusMessage);
    }
    [Fact]
    public async Task ErrorsAndCleanupWarningsRefreshInArabic()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = Create(new Stub { Conversion = (_, _) => Task.FromResult(new DocumentConversionResult(DocumentError.ProcessTerminationFailed, CleanupFailed: true)) }, localization);
        model.SelectedFile = Source; await model.InspectCommand.ExecuteAsync(); model.SetDestination(@"C:\new.docx");
        await model.ConvertCommand.ExecuteAsync();
        Assert.True(model.HasCleanupWarning);
        localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("توقف", model.StatusMessage); Assert.Contains("تنظيف", model.StatusMessage);
        foreach (var error in Enum.GetValues<DocumentError>().Where(e => e != DocumentError.None))
        {
            Assert.True(localization.HasTranslation("DocumentError" + error, AppLanguage.Arabic));
            Assert.True(localization.HasTranslation("DocumentError" + error, AppLanguage.English));
        }
    }
    [Fact]
    public async Task InspectionFailuresAndUnexpectedFaultsDoNotEnableConversion()
    {
        var model = Create(new Stub { Error = DocumentError.InvalidDocument }); model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync(); Assert.False(model.CanChooseDestination); Assert.Empty(model.Outputs);
        var fault = Create(new Stub { Conversion = (_, _) => throw new IOException("test") }); fault.SelectedFile = Source;
        await fault.InspectCommand.ExecuteAsync(); fault.SetDestination(@"C:\new.docx"); await fault.ConvertCommand.ExecuteAsync();
        Assert.True(fault.IsIdle); Assert.Contains("could not be completed", fault.StatusMessage); await fault.WaitForIdleAsync();
    }
    private static DocumentConversionViewModel Create(Stub service, LocalizationService? localization = null) => new(new ObservableCollection<SelectedFileItem> { Source }, service, localization ?? new(new TestLanguagePreferenceStore()));
    private sealed class Stub : IDocumentConversionService
    {
        public DocumentError Error { get; init; }
        public Func<IProgress<DocumentStage>?, CancellationToken, Task<DocumentConversionResult>>? Conversion { get; init; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<DocumentInspectionResult> InspectAsync(string path, CancellationToken token = default) => Task.FromResult(new DocumentInspectionResult(Error == DocumentError.None ? Inspection : null, Error));
        public Task<DocumentConversionResult> ConvertAsync(string source, string destination, DocumentFormat target, IProgress<DocumentStage>? progress = null, CancellationToken token = default)
        { Calls++; Token = token; return Conversion?.Invoke(progress, token) ?? Task.FromResult(new DocumentConversionResult(DocumentError.None, destination)); }
    }
}
