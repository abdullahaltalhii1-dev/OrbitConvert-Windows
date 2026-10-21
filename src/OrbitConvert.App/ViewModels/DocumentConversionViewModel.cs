using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Documents;

namespace OrbitConvert.App.ViewModels;

public sealed class DocumentConversionViewModel : INotifyPropertyChanged
{
    private readonly IDocumentConversionService _service;
    private readonly LocalizationService _localization;
    private SelectedFileItem? _selected;
    private DocumentInspection? _inspection;
    private DocumentFormat _format;
    private string _destination = "", _completed = "", _status = "DocumentIdle";
    private bool _busy, _cleanupFailed;
    private DocumentStage _stage;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private int _operation;
    public DocumentConversionViewModel(ObservableCollection<SelectedFileItem> files, IDocumentConversionService service, LocalizationService localization)
    {
        Files = files; _service = service; _localization = localization;
        InspectCommand = new(InspectAsync, () => IsIdle && SelectedFile is not null);
        ConvertCommand = new(ConvertAsync, () => CanChooseDestination && DestinationPath.Length > 0 && CompletedPath.Length == 0);
        CancelCommand = new(() => { RequestCancellation(); return Task.CompletedTask; }, () => IsBusy && _cancellation is { IsCancellationRequested: false });
        files.CollectionChanged += (_, _) => { if (IsIdle && SelectedFile is not null && !Files.Contains(SelectedFile)) { SelectedFile = null; } };
        localization.LanguageChanged += (_, _) => Refresh();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand ConvertCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public SelectedFileItem? SelectedFile
    {
        get => _selected;
        set
        {
            if (IsBusy || Equals(_selected, value)) { return; }
            _selected = value; _inspection = null; _destination = ""; SetStatus("DocumentIdle"); Refresh();
        }
    }
    public bool IsBusy => _busy;
    public bool IsIdle => !IsBusy;
    public bool CanChooseDestination => IsIdle && _inspection is not null && Outputs.Contains(Format);
    public bool HasCleanupWarning => _cleanupFailed;
    public IReadOnlyList<DocumentFormat> Outputs => _inspection is { } input ? DocumentFormats.Outputs(input.Format) : [];
    public DocumentFormat Format
    {
        get => _format;
        set { if (!IsBusy && Outputs.Contains(value) && value != _format) { _format = value; _destination = ""; SetStatus("DocumentReady"); Refresh(); } }
    }
    public string SourceFormat => _inspection?.Format.ToString() ?? "—";
    public string Size => _inspection is { } input ? input.SizeBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " B" : SelectedFile?.DisplaySize ?? "—";
    public string Structure => _inspection is { } input ? _localization.Format("DocumentStructure", input.Headings, input.Tables, input.Lists) : "—";
    public string DestinationPath => _destination;
    public string CompletedPath => _completed;
    public string StatusMessage => _localization[_status] + (_cleanupFailed ? "\n" + _localization["DocumentCleanupFailed"] : "");
    public string StageText => _localization["DocumentStage" + _stage];
    public void SetDestination(string path) { if (!CanChooseDestination) { return; } _destination = path; SetStatus("DocumentReady"); Refresh(); }
    public void RequestCancellation()
    {
        if (!IsBusy || _cancellation is not { IsCancellationRequested: false }) { return; }
        _cancellation.Cancel(); _status = "DocumentCancelling"; Refresh();
    }
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    private async Task InspectAsync()
    {
        var path = SelectedFile!.FullPath;
        Begin(); _inspection = null; _destination = "";
        try
        {
            var result = await _service.InspectAsync(path, _cancellation!.Token);
            _inspection = result.IsSuccess ? result.Inspection : null;
            if (Outputs.Count > 0) { _format = Outputs[0]; }
            SetStatus(result.IsSuccess ? "DocumentReady" : "DocumentError" + result.Error);
            _cleanupFailed = result.CleanupFailed;
        }
        catch (OperationCanceledException) { SetStatus("DocumentErrorCancelled"); }
        catch (Exception) { SetStatus("DocumentUnexpectedError"); }
        finally { End(); }
    }
    private async Task ConvertAsync()
    {
        var path = SelectedFile!.FullPath; var destination = DestinationPath; var format = Format;
        Begin(); var operation = _operation;
        var progress = new Progress<DocumentStage>(stage => { if (operation == _operation && IsBusy) { _stage = stage; Notify(nameof(StageText)); } });
        try
        {
            var result = await _service.ConvertAsync(path, destination, format, progress, _cancellation!.Token);
            SetStatus(result.IsSuccess ? "DocumentSuccess" : "DocumentError" + result.Error);
            _completed = result.IsSuccess ? result.OutputPath! : ""; _cleanupFailed = result.CleanupFailed;
        }
        catch (OperationCanceledException) { SetStatus("DocumentErrorCancelled"); }
        catch (Exception) { SetStatus("DocumentUnexpectedError"); }
        finally { End(); }
    }
    private void Begin()
    {
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _cancellation = new(); _busy = true; _stage = DocumentStage.Inspecting; _operation++;
        SetStatus("DocumentWorking"); Refresh();
    }
    private void End()
    {
        _busy = false; _cancellation?.Dispose(); _cancellation = null;
        if (SelectedFile is not null && !Files.Contains(SelectedFile)) { _selected = null; _inspection = null; _destination = ""; }
        Refresh(); _idle!.TrySetResult();
    }
    private void SetStatus(string key) { _status = key; _completed = ""; _cleanupFailed = false; }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(SelectedFile), nameof(IsBusy), nameof(IsIdle), nameof(CanChooseDestination), nameof(HasCleanupWarning), nameof(Outputs), nameof(Format), nameof(SourceFormat), nameof(Size), nameof(Structure), nameof(DestinationPath), nameof(CompletedPath), nameof(StatusMessage), nameof(StageText) }) { Notify(name); }
        InspectCommand.NotifyCanExecuteChanged(); ConvertCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
