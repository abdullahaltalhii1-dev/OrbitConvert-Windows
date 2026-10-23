using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Archives;

namespace OrbitConvert.App.ViewModels;

public sealed class ArchiveViewModel : INotifyPropertyChanged
{
    private readonly IArchiveService _service;
    private readonly LocalizationService _localization;
    private SelectedFileItem? _source, _creationSelection;
    private ArchiveInspection? _inspection;
    private int _mode, _generation;
    private bool _busy, _cleanupWarning;
    private string _folder = "", _destination = "", _completed = "", _status = "ArchiveIdle", _stage = "Inspecting";
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    public ArchiveViewModel(ObservableCollection<SelectedFileItem> files, IArchiveService service, LocalizationService localization)
    {
        Files = files; _service = service; _localization = localization;
        ModeOptions = new[] { "Extract", "CreateZip", "CreateTar", "RepackZip" }.Select(x => new Choice(localization, "ArchiveMode" + x)).ToArray();
        InspectCommand = new(InspectAsync, () => IsIdle && !IsCreate && SelectedFile is not null);
        StartCommand = new(StartAsync, () => CanChooseDestination && _destination.Length > 0 && _completed.Length == 0);
        CancelCommand = new(() => { RequestCancellation(); return Task.CompletedTask; }, () => IsBusy && _cancellation is { IsCancellationRequested: false });
        AddCommand = new(_ =>
        {
            if (IsIdle && IsCreate && SelectedFile is { } source && !CreationFiles.Any(x => x.FullPath.Equals(source.FullPath, StringComparison.OrdinalIgnoreCase)))
            { CreationFiles.Add(source); CreationSelection = source; ChangedInputs(); }
        });
        RemoveCommand = new(_ => { if (IsIdle && CreationSelection is { } selected) { CreationFiles.Remove(selected); _creationSelection = CreationFiles.FirstOrDefault(); ChangedInputs(); } });
        ClearCommand = new(_ => { if (IsIdle) { CreationFiles.Clear(); _creationSelection = null; _folder = ""; ChangedInputs(); } });
        files.CollectionChanged += (_, _) => { if (IsIdle) { Reconcile(); } };
        localization.LanguageChanged += (_, _) => { foreach (var choice in ModeOptions) { choice.Refresh(); } Refresh(); };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public ObservableCollection<SelectedFileItem> CreationFiles { get; } = [];
    public IReadOnlyList<Choice> ModeOptions { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public int ModeIndex { get => _mode; set { if (IsIdle && value is >= 0 and <= 3 && value != _mode) { _mode = value; ChangedInputs(); } } }
    public SelectedFileItem? SelectedFile { get => _source; set { if (IsIdle && value != _source) { _source = value; ChangedInputs(); } } }
    public SelectedFileItem? CreationSelection { get => _creationSelection; set { if (IsIdle) { _creationSelection = value; Refresh(); } } }
    public ArchiveOperation Operation => _mode switch { 1 => ArchiveOperation.CreateZip, 2 => ArchiveOperation.CreateTar, 3 => ArchiveOperation.RepackZip, _ => ArchiveOperation.Extract };
    public bool IsCreate => _mode is 1 or 2;
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy;
    public bool HasCleanupWarning => _cleanupWarning;
    public bool HasInspection => _inspection is not null;
    public bool CanChooseDestination => IsIdle && (IsCreate || HasInspection);
    public bool DirectoryOutput => Operation == ArchiveOperation.Extract;
    public string OutputExtension => Operation == ArchiveOperation.CreateTar ? ".tar" : ".zip";
    public string FolderSource => _folder;
    public string DestinationPath => _destination;
    public string CompletedPath => _completed;
    public string StatusMessage => _localization[_status] + (_cleanupWarning ? "\n" + _localization["ArchiveCleanupFailed"] : "");
    public string StageText => _localization["ArchiveStage" + _stage];
    public string DestinationLabel => _localization[DirectoryOutput ? "ArchiveNewFolder" : "ArchiveDestination"];
    public string SelectionSummary => _inspection is { } inspection
        ? _localization.Format("ArchiveSummary", inspection.Format, inspection.Entries.Length, inspection.InputBytes.ToString("N0", CultureInfo.InvariantCulture), inspection.ExpandedBytes.ToString("N0", CultureInfo.InvariantCulture))
        : _localization.Format("ArchiveCreationCount", CreationFiles.Count);
    public string[] ContentRows => _inspection?.Entries.Select(x => x.Path + "  |  " + (x.IsDirectory ? _localization["ArchiveDirectory"] : _localization.Format("ArchiveBytes", x.Size.ToString("N0", CultureInfo.InvariantCulture)))).ToArray() ?? [];
    public void SetFolder(string path) { if (IsIdle && IsCreate) { _folder = path; ChangedInputs(); } }
    public void SetDestination(string path) { if (CanChooseDestination) { _destination = path; _completed = ""; _status = "ArchiveReady"; Refresh(); } }
    public void RequestCancellation() { if (IsBusy && _cancellation is { IsCancellationRequested: false }) { _status = "ArchiveCancelling"; _cancellation.Cancel(); Refresh(); } }
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    private void ChangedInputs() { _inspection = null; _destination = _completed = ""; _status = "ArchiveIdle"; Refresh(); }
    private async Task InspectAsync()
    {
        var source = _source!.FullPath; Begin(); _inspection = null; _destination = "";
        try
        {
            var result = await _service.InspectAsync(source, _cancellation!.Token);
            _cleanupWarning = result.CleanupFailed; _inspection = result.IsSuccess ? result.Inspection : null;
            _status = result.IsSuccess && _inspection is not null ? "ArchiveReady" : "ArchiveError" + (result.IsSuccess ? ArchiveError.OutputValidationFailed : result.Error);
        }
        catch (OperationCanceledException) { _status = "ArchiveErrorCancelled"; }
        catch (Exception) { _status = "ArchiveUnexpectedError"; }
        finally { End(); }
    }
    private async Task StartAsync()
    {
        var request = new ArchiveRequest(Operation, IsCreate ? CreationFiles.Select(x => x.FullPath).ToArray() : [_source!.FullPath], _destination, IsCreate && _folder.Length > 0 ? _folder : null);
        Begin(); var generation = _generation;
        try
        {
            var progress = new Progress<string>(stage => { if (generation == _generation && IsBusy) { _stage = stage; Notify(nameof(StageText)); } });
            var result = await _service.ExecuteAsync(request, progress, _cancellation!.Token);
            _status = result.IsSuccess ? "ArchiveSuccess" : "ArchiveError" + result.Error;
            _cleanupWarning = result.CleanupFailed; _completed = result.IsSuccess ? result.OutputPath : "";
            if (result.Inspection is not null) { _inspection = result.Inspection; }
        }
        catch (OperationCanceledException) { _status = "ArchiveErrorCancelled"; }
        catch (Exception) { _status = "ArchiveUnexpectedError"; }
        finally { End(); }
    }
    private void Begin()
    {
        _cancellation = new(); _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _busy = true; _cleanupWarning = false; _completed = ""; _status = "ArchiveWorking"; _stage = "Inspecting"; _generation++; Refresh();
    }
    private void End() { _busy = false; _cancellation?.Dispose(); _cancellation = null; Reconcile(); Refresh(); _idle!.TrySetResult(); }
    private void Reconcile()
    {
        var changed = false;
        if (_source is not null && !Files.Contains(_source)) { _source = null; changed = true; }
        foreach (var file in CreationFiles.Where(x => !Files.Contains(x)).ToArray()) { CreationFiles.Remove(file); changed = true; }
        if (_creationSelection is not null && !CreationFiles.Contains(_creationSelection)) { _creationSelection = CreationFiles.FirstOrDefault(); }
        if (changed) { _inspection = null; _destination = ""; }
        Refresh();
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(ModeIndex), nameof(SelectedFile), nameof(CreationSelection), nameof(IsCreate), nameof(IsBusy), nameof(IsIdle), nameof(HasInspection),
            nameof(HasCleanupWarning), nameof(CanChooseDestination), nameof(DirectoryOutput), nameof(FolderSource), nameof(DestinationPath), nameof(CompletedPath),
            nameof(StatusMessage), nameof(StageText), nameof(DestinationLabel), nameof(SelectionSummary), nameof(ContentRows) }) { Notify(name); }
        InspectCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public sealed class Choice(LocalizationService localization, string key) : INotifyPropertyChanged
    {
        public string Label => localization[key];
        public override string ToString() => Label;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}
