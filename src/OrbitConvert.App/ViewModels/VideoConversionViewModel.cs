using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.App.ViewModels;

/// <summary>One explicitly selected video; dialogs and Windows events remain in the view.</summary>
public sealed class VideoConversionViewModel : INotifyPropertyChanged
{
    private readonly IVideoConversionService _service;
    private readonly LocalizationService _localization;
    private SelectedFileItem? _selectedFile;
    private VideoInspection? _inspection;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private readonly Stopwatch _clock = new();
    private string _destinationPath = string.Empty;
    private string _statusKey = "VideoIdle";
    private string _completedPath = string.Empty;
    private bool _cleanupFailed;
    private bool _isBusy;
    private bool _isConverting;
    private VideoQuality _quality = VideoQuality.Balanced;
    private int _maxHeight;
    private VideoProgress? _progress;
    private int _operationId;

    public VideoConversionViewModel(ObservableCollection<SelectedFileItem> files,
        IVideoConversionService service, LocalizationService localization)
    {
        Files = files;
        _service = service;
        _localization = localization;
        InspectCommand = new AsyncCommand(InspectAsync, () => SelectedFile is not null && !IsBusy);
        ConvertCommand = new AsyncCommand(ConvertAsync, () => CanChooseDestination && DestinationPath.Length > 0 && CompletedPath.Length == 0);
        CancelCommand = new AsyncCommand(() => { RequestCancellation(); return Task.CompletedTask; },
            () => IsBusy && _cancellation is { IsCancellationRequested: false });
        files.CollectionChanged += (_, _) =>
        {
            if (!IsBusy && SelectedFile is not null && !files.Contains(SelectedFile)) { SelectedFile = null; }
        };
        localization.LanguageChanged += (_, _) => Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand ConvertCommand { get; }
    public AsyncCommand CancelCommand { get; }

    public SelectedFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (IsBusy || Equals(value, _selectedFile)) { return; }
            _selectedFile = value;
            _inspection = null;
            _destinationPath = string.Empty;
            _progress = null;
            SetStatus("VideoIdle");
            Refresh();
        }
    }

    public bool IsBusy => _isBusy;
    public bool IsIdle => !IsBusy;
    public bool CanEditSettings => IsIdle && _inspection is not null && SelectedFile is not null;
    public bool CanChooseDestination => CanEditSettings;
    public string DestinationPath => _destinationPath;
    public string CompletedPath => _completedPath;
    public string Container => _inspection?.Container ?? "—";
    public string Dimensions => _inspection is { } input ? $"{input.Video.DisplayWidth} × {input.Video.DisplayHeight}" : "—";
    public string Duration => _inspection?.Duration is { } duration ? FormatTime(duration) : _localization["VideoUnknown"];
    public string SourceSize => SelectedFile?.DisplaySize ?? "—";
    public string VideoCodec => _inspection?.Video.Codec ?? "—";
    public string AudioCodec => _inspection is { } input ? input.Audio?.Codec ?? _localization["VideoNoAudio"] : "—";
    public string Streams => _inspection is { } input
        ? _localization.Format("VideoStreamCounts", input.VideoStreamCount, input.AudioStreamCount, input.OtherStreamCount) : "—";
    public string ExpectedDimensions => _inspection is { } input
        ? FormatSize(new VideoConversionOptions(Quality, MaxHeight).OutputSize(input.Video.DisplayWidth, input.Video.DisplayHeight)) : "—";
    public VideoQuality Quality
    {
        get => _quality;
        set
        {
            if (!CanEditSettings || !Enum.IsDefined(value) || value == _quality) { return; }
            _quality = value;
            SettingsChanged();
        }
    }
    public int MaxHeight
    {
        get => _maxHeight;
        set
        {
            if (!CanEditSettings || !VideoConversionOptions.MaxHeights.Contains(value) || value == _maxHeight) { return; }
            _maxHeight = value;
            SettingsChanged();
        }
    }
    public bool ShowProgress => IsBusy && _isConverting;
    public bool IsProgressIndeterminate => _progress?.Fraction is null;
    public double ProgressPercent => (_progress?.Fraction ?? 0) * 100;
    public string ProgressText => _progress is { } progress
        ? _localization[progress.Stage == VideoStage.Converting ? "VideoEncoding" : "VideoVerifying"] + " · " +
            (progress.Fraction is { } fraction ? fraction.ToString("P0", _localization.CurrentCulture) : _localization["VideoUnknown"])
        : _localization["VideoStarting"];
    public string Elapsed => _localization.Format("VideoElapsed", FormatTime(_clock.Elapsed));
    public string StatusMessage => _localization[_statusKey] + (_cleanupFailed ? "\n" + _localization["VideoCleanupFailed"] : string.Empty);

    public void SetDestination(string path)
    {
        if (!CanChooseDestination) { return; }
        _destinationPath = path;
        SetStatus("VideoReady");
        Refresh();
    }

    public void RequestCancellation()
    {
        if (!IsBusy || _cancellation is not { IsCancellationRequested: false }) { return; }
        _statusKey = "VideoCancelling";
        // Cancellation may complete the operation inline; never overwrite its final status.
        _cancellation.Cancel();
        Refresh();
    }

    /// <summary>Window closing waits for termination and temporary-file cleanup, not just a cancellation request.</summary>
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    private async Task InspectAsync()
    {
        var path = SelectedFile!.FullPath;
        BeginOperation("VideoInspecting", converting: false);
        _inspection = null;
        _destinationPath = string.Empty;
        try
        {
            var result = await _service.InspectAsync(path, _cancellation!.Token);
            _inspection = result.IsSuccess ? result.Inspection : null;
            SetStatus(result.IsSuccess ? "VideoReady" : "VideoError" + result.Error);
        }
        catch (OperationCanceledException) { SetStatus("VideoErrorCancelled"); }
        catch (Exception) { SetStatus("VideoUnexpectedError"); } // Last UI boundary: do not crash WPF.
        finally { EndOperation(); }
    }

    private async Task ConvertAsync()
    {
        var source = SelectedFile!.FullPath;
        var destination = DestinationPath;
        var options = new VideoConversionOptions(Quality, MaxHeight);
        BeginOperation("VideoConverting", converting: true);
        var operation = _operationId;
        // Progress<T> posts to the WPF context; queued reports from a finished operation cannot revive stale UI.
        var progress = new Progress<VideoProgress>(report =>
        {
            if (operation != _operationId || !IsBusy || !_isConverting) { return; }
            _progress = report;
            RefreshProgress();
        });
        try
        {
            var result = await _service.ConvertAsync(source, destination, options, progress, _cancellation!.Token);
            SetStatus(result.IsSuccess ? "VideoSuccess" : "VideoError" + result.Error);
            _completedPath = result.IsSuccess ? result.OutputPath! : string.Empty;
            _cleanupFailed = result.CleanupFailed;
        }
        catch (OperationCanceledException) { SetStatus("VideoErrorCancelled"); }
        catch (Exception) { SetStatus("VideoUnexpectedError"); }
        finally { EndOperation(); }
    }

    private void BeginOperation(string key, bool converting)
    {
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellation = new();
        _isBusy = true;
        _isConverting = converting;
        _progress = null;
        _operationId++;
        _clock.Restart();
        SetStatus(key);
        Refresh();
    }

    private void EndOperation()
    {
        _clock.Stop();
        _isBusy = false;
        _isConverting = false;
        _cancellation?.Dispose();
        _cancellation = null;
        if (SelectedFile is not null && !Files.Contains(SelectedFile))
        {
            _selectedFile = null;
            _inspection = null;
            _destinationPath = string.Empty;
        }
        Refresh();
        _idle!.TrySetResult();
    }

    private void SettingsChanged() { SetStatus("VideoReady"); Refresh(); }
    private void SetStatus(string key)
    {
        _statusKey = key;
        _completedPath = string.Empty;
        _cleanupFailed = false;
    }
    private static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    private static string FormatSize((int Width, int Height) size) => $"{size.Width} × {size.Height}";

    private void RefreshProgress()
    {
        foreach (var name in new[] { nameof(ShowProgress), nameof(IsProgressIndeterminate), nameof(ProgressPercent), nameof(ProgressText), nameof(Elapsed) }) { Notify(name); }
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(SelectedFile), nameof(IsBusy), nameof(IsIdle), nameof(CanEditSettings), nameof(CanChooseDestination),
            nameof(DestinationPath), nameof(CompletedPath), nameof(Container), nameof(Dimensions), nameof(Duration), nameof(SourceSize),
            nameof(VideoCodec), nameof(AudioCodec), nameof(Streams), nameof(ExpectedDimensions), nameof(Quality), nameof(MaxHeight), nameof(StatusMessage) }) { Notify(name); }
        RefreshProgress();
        InspectCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
