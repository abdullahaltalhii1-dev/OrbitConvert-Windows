using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.App.ViewModels;

public sealed class ImageConversionViewModel : INotifyPropertyChanged
{
    private readonly IImageConversionService _service;
    private readonly LocalizationService _localization;
    private SelectedFileItem? _selectedFile;
    private ImageInspection? _inspection;
    private bool _isBusy;
    private string _destinationPath = string.Empty;
    private string _statusKey = "ImageIdle";
    private string? _completedPath;
    private bool _cleanupFailed;
    private string _outputFormat = "JPEG";
    private int _quality = 90;
    private bool _resizeEnabled;
    private bool _preserveAspectRatio = true;
    private string _resizeWidth = string.Empty;
    private string _resizeHeight = string.Empty;

    public ImageConversionViewModel(ObservableCollection<SelectedFileItem> files,
        IImageConversionService service, LocalizationService localization)
    {
        Files = files;
        _service = service;
        _localization = localization;
        InspectCommand = new AsyncCommand(InspectAsync, () => SelectedFile is not null && !IsBusy);
        ConvertCommand = new AsyncCommand(ConvertAsync, () => CanChooseDestination && DestinationPath.Length > 0 && _completedPath is null);
        files.CollectionChanged += (_, _) =>
        {
            if (!IsBusy && SelectedFile is not null && !files.Contains(SelectedFile)) { SelectedFile = null; }
        };
        localization.LanguageChanged += (_, _) => { Notify(nameof(StatusMessage)); Notify(nameof(SettingsError)); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SelectedFileItem> Files { get; }
    public AsyncCommand InspectCommand { get; }
    public AsyncCommand ConvertCommand { get; }

    public SelectedFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (IsBusy || Equals(_selectedFile, value)) { return; }
            _selectedFile = value;
            _inspection = null;
            _resizeEnabled = false;
            _resizeWidth = _resizeHeight = string.Empty;
            _destinationPath = string.Empty;
            SetStatus("ImageIdle");
            Refresh();
        }
    }

    public bool IsBusy => _isBusy;
    public bool IsIdle => !IsBusy;
    public bool CanEditSettings => !IsBusy && _inspection is not null && SelectedFile is not null;
    public bool CanChooseDestination => CanEditSettings && TryGetOptions(out _);
    public string DestinationPath => _destinationPath;
    public string SourceFormat => _inspection?.Format ?? "—";
    public string Dimensions => _inspection is { } image ? $"{image.Width} × {image.Height}" : "—";
    public IReadOnlyList<string> OutputFormats => ImageConversionOptions.Formats;
    public string OutputFormat
    {
        get => _outputFormat;
        set
        {
            if (!CanEditSettings || !OutputFormats.Contains(value) || value == _outputFormat) { return; }
            _outputFormat = value;
            _destinationPath = string.Empty; // A destination chosen for another encoder is no longer valid.
            SettingsChanged();
        }
    }
    public bool IsLossyFormat => OutputFormat is "JPEG" or "WebP";
    public int Quality { get => _quality; set => SetOption(ref _quality, value); }
    public bool ResizeEnabled { get => _resizeEnabled; set => SetOption(ref _resizeEnabled, value); }
    public bool PreserveAspectRatio { get => _preserveAspectRatio; set => SetOption(ref _preserveAspectRatio, value); }
    public string ResizeWidth { get => _resizeWidth; set => SetOption(ref _resizeWidth, value); }
    public string ResizeHeight { get => _resizeHeight; set => SetOption(ref _resizeHeight, value); }
    public string ExpectedDimensions => _inspection is { } image && TryGetOptions(out var options)
        ? FormatSize(options.OutputSize(image.Width, image.Height)) : "—";
    public string SettingsError => _inspection is not null && !TryGetOptions(out _) ? _localization["ImageErrorInvalidSettings"] : string.Empty;
    public string CompletedPath => _completedPath ?? string.Empty;
    public string StatusMessage => _localization[_statusKey] +
        (_cleanupFailed ? "\n" + _localization["ImageCleanupFailed"] : string.Empty);

    public void SetDestination(string path)
    {
        if (!CanChooseDestination) { return; }
        _destinationPath = path;
        SetStatus("ImageReady");
        Refresh();
    }

    private async Task InspectAsync()
    {
        var file = SelectedFile!;
        BeginOperation("ImageInspecting");
        _inspection = null;
        _destinationPath = string.Empty;
        try
        {
            var result = await _service.InspectAsync(file.FullPath);
            _inspection = result.Inspection;
            if (_inspection is { } image)
            {
                _outputFormat = image.Format == "PNG" ? "JPEG" : "PNG";
                _resizeWidth = image.Width.ToString(CultureInfo.InvariantCulture);
                _resizeHeight = image.Height.ToString(CultureInfo.InvariantCulture);
                _resizeEnabled = false;
            }
            SetStatus(result.IsSuccess ? "ImageReady" : "ImageError" + result.Error);
        }
        catch (Exception)
        {
            // Last UI boundary: unexpected faults must not escape an async command into WPF.
            SetStatus("ImageUnexpectedError");
        }
        finally { EndOperation(); }
    }

    private async Task ConvertAsync()
    {
        var sourcePath = SelectedFile!.FullPath;
        var destination = DestinationPath;
        var format = OutputFormat;
        if (!TryGetOptions(out var options)) { return; }
        BeginOperation("ImageConverting");
        try
        {
            var result = await _service.ConvertAsync(sourcePath, destination, format, options);
            SetStatus(result.IsSuccess ? "ImageSuccess" : "ImageError" + result.Error);
            _completedPath = result.OutputPath;
            _cleanupFailed = result.CleanupFailed;
        }
        catch (Exception) { SetStatus("ImageUnexpectedError"); }
        finally { EndOperation(); }
    }

    private bool TryGetOptions(out ImageConversionOptions options)
    {
        int width = 0, height = 0;
        var parsed = !ResizeEnabled || (int.TryParse(ResizeWidth, NumberStyles.None, CultureInfo.InvariantCulture, out width) &&
            int.TryParse(ResizeHeight, NumberStyles.None, CultureInfo.InvariantCulture, out height));
        options = new(Quality, ResizeEnabled, width, height, PreserveAspectRatio);
        return parsed && options.IsValid;
    }

    private void SetOption<T>(ref T field, T value)
    {
        if (!CanEditSettings || EqualityComparer<T>.Default.Equals(field, value)) { return; }
        field = value;
        SettingsChanged();
    }

    private void SettingsChanged()
    {
        SetStatus("ImageReady");
        Refresh();
    }

    private static string FormatSize((uint Width, uint Height) size) => $"{size.Width} × {size.Height}";

    private void BeginOperation(string key)
    {
        _isBusy = true;
        SetStatus(key);
        Refresh();
    }

    private void EndOperation()
    {
        _isBusy = false;
        if (SelectedFile is not null && !Files.Contains(SelectedFile))
        {
            // Intake may change via another page while work is running. Keep the completed result visible.
            _selectedFile = null;
            _inspection = null;
            _destinationPath = string.Empty;
        }
        Refresh();
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        _completedPath = null;
        _cleanupFailed = false;
        Notify(nameof(StatusMessage));
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(SelectedFile), nameof(IsBusy), nameof(IsIdle), nameof(CanChooseDestination),
            nameof(DestinationPath), nameof(SourceFormat), nameof(Dimensions), nameof(OutputFormat), nameof(StatusMessage), nameof(CompletedPath),
            nameof(CanEditSettings), nameof(IsLossyFormat), nameof(Quality), nameof(ResizeEnabled), nameof(PreserveAspectRatio),
            nameof(ResizeWidth), nameof(ResizeHeight), nameof(ExpectedDimensions), nameof(SettingsError) })
        {
            Notify(name);
        }
        InspectCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
