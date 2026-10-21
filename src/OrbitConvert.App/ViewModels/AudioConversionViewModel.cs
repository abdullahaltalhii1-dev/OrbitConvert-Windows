using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Audio;

namespace OrbitConvert.App.ViewModels;

/// <summary>One explicitly selected audio/video source; native dialogs remain in the view.</summary>
public sealed class AudioConversionViewModel : INotifyPropertyChanged
{
    private readonly IAudioConversionService _service;
    private readonly LocalizationService _localization;
    private SelectedFileItem? _selectedFile;
    private AudioInspection? _inspection;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private readonly Stopwatch _clock = new();
    private string _destinationPath = string.Empty;
    private string _statusKey = "AudioIdle";
    private string _completedPath = string.Empty;
    private bool _cleanupFailed;
    private bool _isBusy;
    private bool _isConverting;
    private AudioFormat _format = AudioFormat.MP3;
    private int _bitrate = 192;
    private int _sampleRate;
    private int _flacCompression = 5;
    private AudioProgress? _progress;
    private int _operationId;

    public AudioConversionViewModel(ObservableCollection<SelectedFileItem> files,
        IAudioConversionService service, LocalizationService localization)
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
            SetStatus("AudioIdle");
            Refresh();
        }
    }

    public bool IsBusy => _isBusy;
    public bool IsIdle => !IsBusy;
    public bool CanEditSettings => IsIdle && _inspection is not null && SelectedFile is not null;
    public bool CanChooseDestination => CanEditSettings && SettingsError.Length == 0;
    public string DestinationPath => _destinationPath;
    public string CompletedPath => _completedPath;
    public string Container => _inspection?.Container ?? "—";
    public string Duration => _inspection?.Duration is { } duration ? FormatTime(duration) : _localization["AudioUnknown"];
    public string SourceSize => _inspection is { } input ? input.SizeBytes.ToString("N0", CultureInfo.InvariantCulture) + " B" : SelectedFile?.DisplaySize ?? "—";
    public string Codec => _inspection?.Audio.Codec ?? "—";
    public string SourceSampleRate => _inspection is { } input ? input.Audio.SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz" : "—";
    public string Channels => _inspection is { } input ? _localization.Format("AudioChannelCount", input.Audio.Channels) : "—";
    public string Bitrate => _inspection?.Audio.Bitrate is { } rate ? (rate / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + " kbps" : _localization["AudioUnknown"];
    public string Streams => _inspection is { } input ? _localization.Format("AudioStreamSummary", input.Audio.Index, input.AudioStreamCount, input.VideoStreamCount) : "—";
    public string ModeText => _inspection is { VideoStreamCount: > 0 } ? _localization["AudioExtractMode"] : _localization["AudioConvertMode"];
    public AudioFormat Format
    {
        get => _format;
        set
        {
            if (!CanEditSettings || !Enum.IsDefined(value) || value == _format) { return; }
            _format = value;
            _destinationPath = string.Empty;
            SettingsChanged();
        }
    }
    public int BitrateKbps { get => _bitrate; set { if (AudioConversionOptions.Bitrates.Contains(value)) { SetOption(ref _bitrate, value); } } }
    public int SampleRate { get => _sampleRate; set { if (AudioConversionOptions.SampleRates.Contains(value)) { SetOption(ref _sampleRate, value); } } }
    public int FlacCompression { get => _flacCompression; set { if (value is >= 0 and <= 8) { SetOption(ref _flacCompression, value); } } }
    public bool IsMp3 => Format == AudioFormat.MP3;
    public bool IsFlac => Format == AudioFormat.FLAC;
    public string FormatHelp => _localization["AudioHelp" + Format];
    public string SettingsError => _inspection is { } input && Options().Validate(input) is var error && error != AudioError.None
        ? _localization["AudioError" + error] : string.Empty;
    private AudioConversionOptions Options() => new(Format, BitrateKbps, SampleRate, FlacCompression);
    private void SetOption(ref int field, int value)
    {
        if (!CanEditSettings || field == value) { return; }
        field = value;
        SettingsChanged();
    }
    public bool ShowProgress => IsBusy && _isConverting;
    public bool IsProgressIndeterminate => _progress?.Fraction is null;
    public double ProgressPercent => (_progress?.Fraction ?? 0) * 100;
    public string ProgressText => _progress is { } progress
        ? _localization[progress.Stage == AudioStage.Encoding ? "AudioEncoding" : "AudioVerifying"] + " · " +
            (progress.Fraction is { } fraction ? fraction.ToString("P0", _localization.CurrentCulture) : _localization["AudioUnknown"])
        : _localization["AudioStarting"];
    public string Elapsed => _localization.Format("AudioElapsed", FormatTime(_clock.Elapsed));
    public string StatusMessage => _localization[_statusKey] + (_cleanupFailed ? "\n" + _localization["AudioCleanupFailed"] : string.Empty);

    public void SetDestination(string path)
    {
        if (!CanChooseDestination) { return; }
        _destinationPath = path;
        SetStatus("AudioReady");
        Refresh();
    }

    public void RequestCancellation()
    {
        if (!IsBusy || _cancellation is not { IsCancellationRequested: false }) { return; }
        _cancellation.Cancel();
        _statusKey = "AudioCancelling";
        Refresh();
    }

    /// <summary>Window closing waits for termination and temporary-file cleanup, not just a cancellation request.</summary>
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    private async Task InspectAsync()
    {
        var path = SelectedFile!.FullPath;
        BeginOperation("AudioInspecting", converting: false);
        _inspection = null;
        _destinationPath = string.Empty;
        try
        {
            var result = await _service.InspectAsync(path, _cancellation!.Token);
            _inspection = result.IsSuccess ? result.Inspection : null;
            SetStatus(result.IsSuccess ? "AudioReady" : "AudioError" + result.Error);
        }
        catch (OperationCanceledException) { SetStatus("AudioErrorCancelled"); }
        catch (Exception) { SetStatus("AudioUnexpectedError"); } // Last UI boundary: do not crash WPF.
        finally { EndOperation(); }
    }

    private async Task ConvertAsync()
    {
        var source = SelectedFile!.FullPath;
        var destination = DestinationPath;
        var options = Options();
        BeginOperation("AudioConverting", converting: true);
        var operation = _operationId;
        // Progress<T> posts to the WPF context; queued reports from a finished operation cannot revive stale UI.
        var progress = new Progress<AudioProgress>(report =>
        {
            if (operation != _operationId || !IsBusy || !_isConverting) { return; }
            _progress = report;
            RefreshProgress();
        });
        try
        {
            var result = await _service.ConvertAsync(source, destination, options, progress, _cancellation!.Token);
            SetStatus(result.IsSuccess ? "AudioSuccess" : "AudioError" + result.Error);
            _completedPath = result.IsSuccess ? result.OutputPath! : string.Empty;
            _cleanupFailed = result.CleanupFailed;
        }
        catch (OperationCanceledException) { SetStatus("AudioErrorCancelled"); }
        catch (Exception) { SetStatus("AudioUnexpectedError"); }
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

    private void SettingsChanged() { SetStatus("AudioReady"); Refresh(); }
    private void SetStatus(string key)
    {
        _statusKey = key;
        _completedPath = string.Empty;
        _cleanupFailed = false;
    }
    private static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private void RefreshProgress()
    {
        foreach (var name in new[] { nameof(ShowProgress), nameof(IsProgressIndeterminate), nameof(ProgressPercent), nameof(ProgressText), nameof(Elapsed) }) { Notify(name); }
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(SelectedFile), nameof(IsBusy), nameof(IsIdle), nameof(CanEditSettings), nameof(CanChooseDestination),
            nameof(DestinationPath), nameof(CompletedPath), nameof(Container), nameof(Duration), nameof(SourceSize), nameof(Codec),
            nameof(SourceSampleRate), nameof(Channels), nameof(Bitrate), nameof(Streams), nameof(ModeText), nameof(Format),
            nameof(BitrateKbps), nameof(SampleRate), nameof(FlacCompression), nameof(IsMp3), nameof(IsFlac), nameof(FormatHelp),
            nameof(SettingsError), nameof(StatusMessage) }) { Notify(name); }
        RefreshProgress();
        InspectCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
