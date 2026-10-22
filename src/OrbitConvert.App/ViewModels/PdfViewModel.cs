using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Pdf;

namespace OrbitConvert.App.ViewModels;

public sealed class PdfViewModel : INotifyPropertyChanged
{
    private readonly IPdfService _service;
    private readonly LocalizationService _localization;
    private readonly Dictionary<string, PdfInspection> _inspections = new(StringComparer.OrdinalIgnoreCase);
    private SelectedFileItem? _source, _mergeSelection;
    private int _mode, _splitMode, _dpi = 150;
    private string _pages = "", _destination = "", _completed = "", _preview = "", _status = "PdfIdle", _stage = "Inspecting";
    private bool _busy, _cleanupFailed;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private int _operation;

    public PdfViewModel(ObservableCollection<SelectedFileItem> files, IPdfService service, LocalizationService localization)
    {
        Files = files; _service = service; _localization = localization;
        ModeOptions = new[] { "Merge", "Split", "Reorder", "Render", "Text" }.Select(x => new Choice(localization, "PdfMode" + x)).ToArray();
        SplitOptions = new[] { "Extract", "Individual", "Boundaries" }.Select(x => new Choice(localization, "PdfSplit" + x)).ToArray();
        InspectCommand = new(InspectAsync, () => IsIdle && Sources().Length > 0 && (!IsMerge || MergeFiles.Count >= 2));
        StartCommand = new(StartAsync, () => CanChooseDestination && _destination.Length > 0 && _completed.Length == 0);
        CancelCommand = new(() => { RequestCancellation(); return Task.CompletedTask; }, () => IsBusy && _cancellation is { IsCancellationRequested: false });
        AddMergeCommand = new(_ =>
        {
            if (!IsIdle || SelectedFile is null || MergeFiles.Any(x => x.FullPath.Equals(SelectedFile.FullPath, StringComparison.OrdinalIgnoreCase))) { return; }
            MergeFiles.Add(SelectedFile); MergeSelection = SelectedFile; ChangedInputs();
        });
        RemoveMergeCommand = new(_ => { if (IsIdle && MergeSelection is { } item) { MergeFiles.Remove(item); MergeSelection = MergeFiles.FirstOrDefault(); ChangedInputs(); } });
        MoveUpCommand = new(_ => Move(-1)); MoveDownCommand = new(_ => Move(1));
        files.CollectionChanged += (_, _) => { if (IsIdle) { Reconcile(); } };
        localization.LanguageChanged += (_, _) => { foreach (var choice in ModeOptions.Concat(SplitOptions)) { choice.Refresh(); } Refresh(); };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public ObservableCollection<SelectedFileItem> MergeFiles { get; } = [];
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public RelayCommand AddMergeCommand { get; }
    public RelayCommand RemoveMergeCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public SelectedFileItem? SelectedFile
    {
        get => _source;
        set { if (IsBusy || Equals(value, _source)) { return; } _source = value; if (!IsMerge) { ChangedInputs(); } else { Refresh(); } }
    }
    public SelectedFileItem? MergeSelection { get => _mergeSelection; set { if (IsIdle) { _mergeSelection = value; Refresh(); } } }
    public int ModeIndex { get => _mode; set { if (IsIdle && value is >= 0 and <= 4 && value != _mode) { _mode = value; _pages = ""; ChangedInputs(); } } }
    public int SplitModeIndex { get => _splitMode; set { if (IsIdle && value is >= 0 and <= 2 && value != _splitMode) { _splitMode = value; _pages = ""; ChangedOptions(); } } }
    public int Dpi { get => _dpi; set { if (IsIdle && value is 150 or 300 && value != _dpi) { _dpi = value; ChangedOptions(); } } }
    public int[] DpiOptions { get; } = [150, 300];
    public string PagesText { get => _pages; set { if (IsIdle && value != _pages) { _pages = value; ChangedOptions(); } } }
    public IReadOnlyList<Choice> ModeOptions { get; }
    public IReadOnlyList<Choice> SplitOptions { get; }
    public bool IsMerge => _mode == 0;
    public bool IsSplit => _mode == 1;
    public bool IsRender => _mode == 3;
    public bool ShowsPages => !IsMerge;
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy;
    public bool HasCleanupWarning => _cleanupFailed;
    public string DestinationPath => _destination;
    public string CompletedPath => _completed;
    public string TextPreview => _preview;
    public string StatusMessage => _localization[_status] + (_cleanupFailed ? "\n" + _localization["PdfCleanupFailed"] : "");
    public string StageText => _localization["PdfStage" + _stage];
    public bool HasInspection => Sources() is { Length: > 0 } sources && sources.All(x => _inspections.ContainsKey(x.FullPath));
    public int PageCount => HasInspection ? Sources().Sum(x => _inspections[x.FullPath].Pages.Length) : 0;
    public string TotalPages => _localization.Format("PdfPageCount", PageCount);
    public string SettingsError
    {
        get
        {
            if (!HasInspection || IsMerge) { return ""; }
            try
            {
                var pages = PdfPageSelection.Parse(_pages, PageCount);
                if (Operation == PdfOperation.Boundaries && (string.IsNullOrWhiteSpace(_pages) || pages[^1] >= PageCount || !pages.SequenceEqual(pages.Order()))) { throw new ArgumentException(); }
                if (IsRender && pages.Length > PdfLimits.OutputFiles || Operation == PdfOperation.IndividualPages && pages.Length > PdfLimits.OutputFiles) { throw new ArgumentException(); }
                return "";
            }
            catch (ArgumentException) { return _localization["PdfErrorInvalidSelection"]; }
        }
    }
    public bool CanChooseDestination => IsIdle && HasInspection && PageCount <= PdfLimits.Pages && SettingsError.Length == 0 && (!IsMerge || MergeFiles.Count >= 2);
    public PdfOperation Operation => _mode switch { 0 => PdfOperation.Merge, 1 => _splitMode switch { 1 => PdfOperation.IndividualPages, 2 => PdfOperation.Boundaries, _ => PdfOperation.Extract }, 2 => PdfOperation.Reorder, 3 => PdfOperation.Render, _ => PdfOperation.Text };
    public bool DirectoryOutput => Operation is PdfOperation.IndividualPages or PdfOperation.Boundaries || IsRender && HasInspection && SettingsError.Length == 0 && PdfPageSelection.Parse(_pages, PageCount).Length > 1;
    public string OutputExtension => IsRender ? ".png" : _mode == 4 ? ".txt" : ".pdf";
    public string DestinationLabel => _localization[DirectoryOutput ? "PdfNewFolder" : "PdfDestination"];
    public string SelectionHelp => _localization[Operation == PdfOperation.Boundaries ? "PdfBoundariesHelp" : _mode == 2 ? "PdfSequenceHelp" : "PdfPagesHelp"];
    public string[] InspectionDetails => Sources().Where(x => _inspections.ContainsKey(x.FullPath)).SelectMany(file =>
    {
        var input = _inspections[file.FullPath];
        return new[] { file.FileName, _localization.Format("PdfMetadata", input.Version, input.SizeBytes.ToString("N0", CultureInfo.InvariantCulture), input.Title, input.Author), _localization["PdfNotEncrypted"] }
            .Concat(input.Pages.Select(page => _localization.Format("PdfPageInfo", page.Number,
                page.Width.ToString("0.#", CultureInfo.InvariantCulture), page.Height.ToString("0.#", CultureInfo.InvariantCulture),
                page.Rotation, _localization[page.Width > page.Height ? "PdfLandscape" : page.Width < page.Height ? "PdfPortrait" : "PdfSquare"])));
    }).ToArray();
    public void SetDestination(string path) { if (CanChooseDestination) { _destination = path; _completed = ""; _status = "PdfReady"; Refresh(); } }
    public void RequestCancellation() { if (IsBusy && _cancellation is { IsCancellationRequested: false }) { _cancellation.Cancel(); _status = "PdfCancelling"; Refresh(); } }
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    private SelectedFileItem[] Sources() => IsMerge ? MergeFiles.ToArray() : SelectedFile is { } source ? [source] : [];
    private void Move(int offset)
    {
        if (!IsIdle || MergeSelection is null) { return; }
        var index = MergeFiles.IndexOf(MergeSelection);
        if (index < 0 || index + offset < 0 || index + offset >= MergeFiles.Count) { return; }
        MergeFiles.Move(index, index + offset); _destination = ""; _completed = ""; Refresh();
    }
    private void ChangedInputs() { _inspections.Clear(); _destination = _completed = _preview = ""; _status = "PdfIdle"; Refresh(); }
    private void ChangedOptions() { _destination = _completed = _preview = ""; _status = "PdfReady"; Refresh(); }
    private async Task InspectAsync()
    {
        var sources = Sources(); Begin(); _inspections.Clear(); _destination = "";
        try
        {
            foreach (var source in sources)
            {
                var result = await _service.InspectAsync(source.FullPath, _cancellation!.Token);
                _cleanupFailed |= result.CleanupFailed;
                if (!result.IsSuccess) { _status = "PdfError" + result.Error; _inspections.Clear(); return; }
                _inspections[source.FullPath] = result.Inspection!;
            }
            _status = PageCount > PdfLimits.Pages ? "PdfErrorTooLarge" : "PdfReady";
        }
        catch (OperationCanceledException) { _status = "PdfErrorCancelled"; _inspections.Clear(); }
        catch (Exception) { _status = "PdfUnexpectedError"; _inspections.Clear(); }
        finally { End(); }
    }
    private async Task StartAsync()
    {
        var request = new PdfRequest(Operation, Sources().Select(x => x.FullPath).ToArray(), _destination, IsMerge ? "" : _pages, _dpi);
        Begin(); var operation = _operation;
        try
        {
            var progress = new Progress<PdfProgress>(value => { if (operation == _operation && IsBusy) { _stage = value.Stage; Notify(nameof(StageText)); } });
            var result = await _service.ExecuteAsync(request, progress, _cancellation!.Token);
            _status = result.IsSuccess ? request.Operation == PdfOperation.Text ? "PdfTextSavedReview" : "PdfSuccess" : "PdfError" + result.Error;
            _cleanupFailed = result.CleanupFailed; _preview = result.TextPreview;
            _completed = result.IsSuccess ? string.Join(Environment.NewLine, result.Outputs!) : "";
        }
        catch (OperationCanceledException) { _status = "PdfErrorCancelled"; }
        catch (Exception) { _status = "PdfUnexpectedError"; }
        finally { End(); }
    }
    private void Begin()
    {
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _cancellation = new(); _busy = true; _cleanupFailed = false;
        _completed = _preview = ""; _status = "PdfWorking"; _stage = "Inspecting"; _operation++; Refresh();
    }
    private void End() { _busy = false; _cancellation?.Dispose(); _cancellation = null; Reconcile(); Refresh(); _idle!.TrySetResult(); }
    private void Reconcile()
    {
        var changed = false;
        foreach (var item in MergeFiles.Where(x => !Files.Contains(x)).ToArray()) { MergeFiles.Remove(item); changed = true; }
        if (_source is not null && !Files.Contains(_source)) { _source = null; changed = true; }
        if (changed) { _inspections.Clear(); _destination = ""; }
        Refresh();
    }
    private void Refresh()
    {
        foreach (var property in new[] { nameof(SelectedFile), nameof(MergeSelection), nameof(ModeIndex), nameof(SplitModeIndex), nameof(Dpi), nameof(PagesText),
            nameof(IsMerge), nameof(IsSplit), nameof(IsRender), nameof(ShowsPages), nameof(IsBusy), nameof(IsIdle), nameof(HasCleanupWarning), nameof(DestinationPath), nameof(CompletedPath), nameof(TextPreview),
            nameof(StatusMessage), nameof(StageText), nameof(HasInspection), nameof(PageCount), nameof(TotalPages), nameof(SettingsError), nameof(CanChooseDestination), nameof(DirectoryOutput), nameof(DestinationLabel), nameof(SelectionHelp), nameof(InspectionDetails) }) { Notify(property); }
        InspectCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    // Keep option identities stable while the view is unloaded in Settings. Replacing ItemsSource
    // during language changes can leave WPF with SelectedIndex set but SelectedItem null.
    public sealed class Choice(LocalizationService localization, string key) : INotifyPropertyChanged
    {
        public string Label => localization[key];
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
}
