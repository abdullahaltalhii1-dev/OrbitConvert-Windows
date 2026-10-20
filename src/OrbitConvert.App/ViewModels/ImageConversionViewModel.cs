using System.Collections.ObjectModel;
using System.ComponentModel;
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
        localization.LanguageChanged += (_, _) => Notify(nameof(StatusMessage));
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
            _destinationPath = string.Empty;
            SetStatus("ImageIdle");
            Refresh();
        }
    }

    public bool IsBusy => _isBusy;
    public bool IsIdle => !IsBusy;
    public bool CanChooseDestination => !IsBusy && _inspection is not null && SelectedFile is not null;
    public string DestinationPath => _destinationPath;
    public string SourceFormat => _inspection?.Format ?? "—";
    public string Dimensions => _inspection is { } image ? $"{image.Width} × {image.Height}" : "—";
    public string OutputFormat => _inspection is null ? "—" : _inspection.Format == "PNG" ? "JPEG" : "PNG";
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
        BeginOperation("ImageConverting");
        try
        {
            var result = await _service.ConvertAsync(sourcePath, destination, format);
            SetStatus(result.IsSuccess ? "ImageSuccess" : "ImageError" + result.Error);
            _completedPath = result.OutputPath;
            _cleanupFailed = result.CleanupFailed;
        }
        catch (Exception) { SetStatus("ImageUnexpectedError"); }
        finally { EndOperation(); }
    }

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
            nameof(DestinationPath), nameof(SourceFormat), nameof(Dimensions), nameof(OutputFormat), nameof(StatusMessage), nameof(CompletedPath) })
        {
            Notify(name);
        }
        InspectCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
