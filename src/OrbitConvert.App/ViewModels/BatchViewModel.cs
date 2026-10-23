using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Batch;

namespace OrbitConvert.App.ViewModels;

/// <summary>Session-only FIFO orchestration on the presentation context. Exactly one awaited engine call at a time.</summary>
public sealed class BatchViewModel : INotifyPropertyChanged
{
    private readonly IBatchExecutor _executor;
    private readonly LocalizationService _localization;
    private readonly Func<bool> _otherEnginesIdle;
    private readonly ObservableCollection<BatchJob> _jobs = [];
    private CancellationTokenSource? _activeCancellation;
    private BatchJob? _active;
    private TaskCompletionSource? _idle;
    private bool _busy, _stop, _cleanupWarning;
    private int _presetIndex;
    private string _folder = "", _message = "BatchIdle";

    public BatchViewModel(ObservableCollection<SelectedFileItem> files, IBatchExecutor executor, LocalizationService localization,
        Func<bool>? otherEnginesIdle = null, int maximumJobs = 100)
    {
        if (maximumJobs is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(maximumJobs)); }
        Files = files; _executor = executor; _localization = localization; _otherEnginesIdle = otherEnginesIdle ?? (() => true); MaximumJobs = maximumJobs;
        Jobs = new(_jobs);
        PresetOptions = BatchPreset.All.Select(x => new BatchPresetChoice(x, localization)).ToArray();
        AddCommand = new(() => { AddFiles(Files.Select(x => x.FullPath)); return Task.CompletedTask; }, () => IsIdle && !HasCleanupWarning && Files.Count > 0 && DestinationFolder.Length > 0 && _otherEnginesIdle());
        StartCommand = new(RunAsync, () => IsIdle && !HasCleanupWarning && _otherEnginesIdle() && _jobs.Any(x => x.State == BatchState.Pending));
        CancelQueueCommand = new RelayCommand(_ => RequestCancellation());
        CancelJobCommand = new RelayCommand(job => { if (job is BatchJob item) { CancelJob(item); } });
        RetryCommand = new(() => { RetryFailed(); return Task.CompletedTask; }, () => IsIdle && !HasCleanupWarning && _otherEnginesIdle() && _jobs.Any(x => x.State == BatchState.Failed));
        ClearCompletedCommand = new(() => { RemoveFinished(false); return Task.CompletedTask; }, () => IsIdle && _jobs.Any(x => x.State == BatchState.Completed));
        ClearErrorsCommand = new(() => { RemoveFinished(true); return Task.CompletedTask; }, () => IsIdle && _jobs.Any(x => x.State is BatchState.Failed or BatchState.Cancelled));
        files.CollectionChanged += (_, _) => Refresh();
        localization.LanguageChanged += (_, _) => { foreach (var job in _jobs) { job.Refresh(); } foreach (var choice in PresetOptions) { choice.Refresh(); } Refresh(); };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public ReadOnlyObservableCollection<BatchJob> Jobs { get; }
    public int MaximumJobs { get; }
    public IReadOnlyList<BatchPresetChoice> PresetOptions { get; }
    public int PresetIndex { get => _presetIndex; set { if (IsIdle && value >= 0 && value < BatchPreset.All.Count) { _presetIndex = value; Refresh(); } } }
    public string DestinationFolder => _folder;
    public bool IsBusy => _busy;
    public bool IsIdle => !_busy;
    public bool HasCleanupWarning => _cleanupWarning;
    public string StatusMessage => _localization[_message] + (_cleanupWarning ? "\n" + _localization["BatchCleanupWarning"] : "");
    public string Summary => _localization.Format("BatchSummary", _jobs.Count(x => x.State == BatchState.Completed), _jobs.Count,
        _jobs.Count(x => x.IsActive), _jobs.Count(x => x.State == BatchState.Pending), _jobs.Count(x => x.State == BatchState.Failed), _jobs.Count(x => x.State == BatchState.Cancelled));
    public AsyncCommand AddCommand { get; }
    public AsyncCommand StartCommand { get; }
    public RelayCommand CancelJobCommand { get; }
    public RelayCommand CancelQueueCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public AsyncCommand ClearCompletedCommand { get; }
    public AsyncCommand ClearErrorsCommand { get; }
    public void NotifyExternalBusyChanged() => Refresh();
    public void SetDestinationFolder(string folder) { if (IsIdle) { _folder = folder; Refresh(); } }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (IsBusy || HasCleanupWarning || !_otherEnginesIdle()) { return; }
        var sources = paths.Take(MaximumJobs + 1).ToArray();
        if (_jobs.Count + sources.Length > MaximumJobs) { SetMessage("BatchLimit"); return; }
        if (sources.Length == 0) { SetMessage("BatchEmpty"); return; }
        var pending = new List<BatchRequest>(); var preset = BatchPreset.All[PresetIndex];
        try
        {
            var folder = SafePath(_folder);
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { SetMessage("BatchInvalidDestination"); return; }
            foreach (var sourcePath in sources)
            {
                var source = SafePath(sourcePath);
                if (!preset.Accepts(source)) { SetMessage("BatchUnsupported"); return; }
                var destination = SafePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + "-orbitconvert" + preset.Extension));
                var request = new BatchRequest(source, destination, preset, BatchSourceStamp.Read(source));
                var error = Validate(request, _jobs.Select(x => x.Request).Concat(pending));
                if (error is not null) { SetMessage(error); return; }
                pending.Add(request);
            }
        }
        catch (Exception error) when (IsPathError(error)) { SetMessage("BatchUnreadableInput"); return; }
        foreach (var request in pending) { _jobs.Add(new(request, _localization)); }
        SetMessage("BatchReady");
    }

    // All pending paths/settings are revalidated before the first engine is invoked, then
    // each source is checked again immediately before its own execution.
    private async Task RunAsync()
    {
        _busy = true; _stop = false; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); _message = "BatchWorking"; Refresh();
        try
        {
            foreach (var job in _jobs.Where(x => x.State == BatchState.Pending))
            {
                var error = Validate(job.Request, _jobs.Where(x => x.Id != job.Id && x.State != BatchState.Cancelled).Select(x => x.Request), checkStamp: true);
                if (error is not null) { Finish(job, new(error)); }
            }
            foreach (var job in _jobs.Where(x => x.State == BatchState.Pending))
            {
                if (_stop || HasCleanupWarning) { break; }
                _active = job; _activeCancellation = new(); var attempt = ++job.Attempt;
                try
                {
                    var error = Validate(job.Request, [], checkStamp: true);
                    if (error is not null) { Finish(job, new(error)); continue; }
                    job.State = BatchState.Inspecting; job.Refresh(); Refresh();
                    var progress = new Progress<BatchProgress>(p =>
                    {
                        if (_active != job || job.Attempt != attempt || !job.IsActive || job.CancellationRequested) { return; }
                        if (p.State is not (BatchState.Inspecting or BatchState.Running or BatchState.Verifying)) { return; }
                        // Stages never move backwards within one attempt.
                        if (p.State >= job.State) { job.State = p.State; job.Fraction = p.Fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null; job.Refresh(); Refresh(); }
                    });
                    var outcome = await _executor.ExecuteAsync(job.Request, progress, _activeCancellation.Token);
                    Finish(job, outcome);
                }
                catch (OperationCanceledException) { Finish(job, new(Cancelled: true)); }
                catch (Exception error) when (IsPathError(error)) { Finish(job, new("BatchUnreadableInput")); }
                catch (Exception) { Finish(job, new("BatchUnexpected", CleanupWarning: true)); }
                finally { job.Attempt++; _activeCancellation.Dispose(); _activeCancellation = null; _active = null; }
            }
        }
        finally
        {
            _busy = false; _message = HasCleanupWarning ? "BatchStopped" : _stop ? "BatchStopped" : "BatchFinished";
            Refresh(); _idle!.TrySetResult();
        }
    }
    public void CancelJob(BatchJob job)
    {
        if (!_jobs.Contains(job) || !job.CanCancel) { return; }
        job.CancellationRequested = true;
        if (job.State == BatchState.Pending) { job.State = BatchState.Cancelled; }
        job.Refresh(); Refresh();
        // Publish pending status before Cancel: cancellation can complete inline.
        if (_active == job) { _activeCancellation?.Cancel(); }
    }
    public void RequestCancellation()
    {
        _stop = true;
        foreach (var job in _jobs.Where(x => x.State == BatchState.Pending).ToArray()) { CancelJob(job); }
        if (_active is { } active) { CancelJob(active); }
    }
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;
    public void RetryFailed()
    {
        if (IsBusy || HasCleanupWarning || !_otherEnginesIdle()) { return; }
        foreach (var job in _jobs.Where(x => x.State == BatchState.Failed))
        {
            try
            {
                var request = job.Request with { SourceStamp = BatchSourceStamp.Read(job.Request.Source) };
                var error = Validate(request, _jobs.Where(x => x.Id != job.Id && x.State != BatchState.Cancelled).Select(x => x.Request));
                if (error is not null) { job.ErrorKey = error; job.Refresh(); continue; }
                job.Request = request; job.State = BatchState.Pending; job.ErrorKey = null; job.Fraction = null; job.CancellationRequested = false; job.Refresh();
            }
            catch (Exception error) when (IsPathError(error)) { job.ErrorKey = "BatchUnreadableInput"; job.Refresh(); }
        }
        Refresh();
    }
    private void RemoveFinished(bool errors)
    {
        if (IsBusy) { return; }
        foreach (var job in _jobs.Where(x => errors ? x.State is BatchState.Failed or BatchState.Cancelled : x.State == BatchState.Completed).ToArray()) { _jobs.Remove(job); }
        Refresh();
    }
    private void Finish(BatchJob job, BatchOutcome outcome)
    {
        job.State = outcome.IsSuccess ? BatchState.Completed : outcome.Cancelled ? BatchState.Cancelled : BatchState.Failed;
        job.ErrorKey = outcome.ErrorKey; job.ResultPath = outcome.IsSuccess ? outcome.OutputPath! : ""; job.Fraction = outcome.IsSuccess ? 1 : null;
        job.CleanupWarning = outcome.CleanupWarning; _cleanupWarning |= outcome.CleanupWarning; job.Refresh(); Refresh();
    }
    private static string? Validate(BatchRequest request, IEnumerable<BatchRequest> others, bool checkStamp = false)
    {
        try
        {
            var source = SafePath(request.Source); var destination = SafePath(request.Destination);
            if (!BatchPreset.All.Contains(request.Preset) || !request.Preset.Accepts(source) || !request.ImageOptions.IsValid || !request.VideoOptions.IsValid) { return "BatchUnsupported"; }
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) { return "BatchSamePath"; }
            var sourceStamp = BatchSourceStamp.Read(source);
            if (checkStamp && sourceStamp != request.SourceStamp) { return "BatchSourceChanged"; }
            if (!Directory.Exists(Path.GetDirectoryName(destination))) { return "BatchInvalidDestination"; }
            if (File.Exists(destination) || Directory.Exists(destination)) { return "BatchDestinationExists"; }
            foreach (var other in others)
            {
                if (string.Equals(other.Source, source, StringComparison.OrdinalIgnoreCase) && other.Preset == request.Preset) { return "BatchDuplicate"; }
                if (string.Equals(other.Destination, destination, StringComparison.OrdinalIgnoreCase) || string.Equals(other.Source, destination, StringComparison.OrdinalIgnoreCase) || string.Equals(source, other.Destination, StringComparison.OrdinalIgnoreCase)) { return "BatchCollision"; }
            }
            return null;
        }
        catch (Exception error) when (IsPathError(error)) { return "BatchUnreadableInput"; }
    }
    private static string SafePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length > 240 || path.AsSpan(2).Contains(':')) { throw new IOException(); }
        return Path.GetFullPath(path);
    }
    private static bool IsPathError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private void SetMessage(string key) { _message = key; Refresh(); }
    private void Refresh()
    {
        PropertyChanged?.Invoke(this, new(string.Empty));
        AddCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        ClearCompletedCommand.NotifyCanExecuteChanged(); ClearErrorsCommand.NotifyCanExecuteChanged();
    }
}

public sealed class BatchPresetChoice(BatchPreset preset, LocalizationService localization) : INotifyPropertyChanged
{
    public string Label => localization["BatchMode" + preset.Key];
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Label)));
}
