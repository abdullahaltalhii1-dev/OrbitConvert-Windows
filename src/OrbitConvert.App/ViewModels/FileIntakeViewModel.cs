using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.App.ViewModels;

public sealed class FileIntakeViewModel : INotifyPropertyChanged
{
    private readonly IFileMetadataProvider _metadataProvider;
    private readonly LocalizationService _localization;
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileIntakeStatus _status;

    public FileIntakeViewModel(
        IFileMetadataProvider metadataProvider,
        LocalizationService localization)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _localization.LanguageChanged += Localization_LanguageChanged;
        AddFilesCommand = new RelayCommand(AddFilesFromParameter);
        RemoveFileCommand = new RelayCommand(parameter =>
        {
            if (parameter is SelectedFileItem file)
            {
                RemoveFile(file);
            }
        });
        ClearFilesCommand = new RelayCommand(_ => ClearFiles());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SelectedFileItem> SelectedFiles { get; } = [];

    public ICommand AddFilesCommand { get; }

    public ICommand RemoveFileCommand { get; }

    public ICommand ClearFilesCommand { get; }

    public int SelectedFileCount => SelectedFiles.Count;

    public bool HasSelectedFiles => SelectedFiles.Count > 0;

    public bool HasStatusMessage => _status.Kind != FileIntakeStatusKind.None;

    public string SelectedFileCountText => SelectedFileCount switch
    {
        0 => _localization[LocalizationKeys.FileCountNone],
        1 => _localization[LocalizationKeys.FileCountOne],
        2 => _localization[LocalizationKeys.FileCountTwo],
        _ => _localization.Format(LocalizationKeys.FileCountMany, SelectedFileCount),
    };

    public string StatusMessage => BuildStatusMessage(_status);

    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var candidates = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var added = 0;
        var duplicates = 0;
        var directories = 0;
        var missing = 0;
        var inaccessible = 0;
        var invalid = 0;

        foreach (var path in candidates)
        {
            var result = _metadataProvider.GetMetadata(path);
            if (result.File is { } file)
            {
                if (_selectedPaths.Add(file.FullPath))
                {
                    SelectedFiles.Add(file);
                    added++;
                }
                else
                {
                    duplicates++;
                }

                continue;
            }

            switch (result.Error)
            {
                case FileMetadataError.Directory:
                    directories++;
                    break;
                case FileMetadataError.Missing:
                    missing++;
                    break;
                case FileMetadataError.Inaccessible:
                    inaccessible++;
                    break;
                default:
                    invalid++;
                    break;
            }
        }

        if (added > 0)
        {
            NotifySelectionChanged();
        }

        SetStatus(new FileIntakeStatus(
            FileIntakeStatusKind.AddResult,
            Added: added,
            Duplicates: duplicates,
            Directories: directories,
            Missing: missing,
            Inaccessible: inaccessible,
            Invalid: invalid));
    }

    public void RemoveFile(SelectedFileItem file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!SelectedFiles.Remove(file))
        {
            return;
        }

        _selectedPaths.Remove(file.FullPath);
        NotifySelectionChanged();
        SetStatus(new FileIntakeStatus(FileIntakeStatusKind.Removed, FileName: file.FileName));
    }

    public void ClearFiles()
    {
        if (SelectedFiles.Count == 0)
        {
            return;
        }

        SelectedFiles.Clear();
        _selectedPaths.Clear();
        NotifySelectionChanged();
        SetStatus(new FileIntakeStatus(FileIntakeStatusKind.SelectionCleared));
    }

    public void ReportFolderDropRejected()
    {
        SetStatus(new FileIntakeStatus(FileIntakeStatusKind.FolderDropRejected));
    }

    private void AddFilesFromParameter(object? parameter)
    {
        switch (parameter)
        {
            case string path:
                AddFiles([path]);
                break;
            case IEnumerable<string> paths:
                AddFiles(paths);
                break;
        }
    }

    private string BuildStatusMessage(FileIntakeStatus status)
    {
        if (status.Kind == FileIntakeStatusKind.None)
        {
            return string.Empty;
        }

        if (status.Kind == FileIntakeStatusKind.Removed)
        {
            return _localization.Format(LocalizationKeys.IntakeRemoved, status.FileName ?? string.Empty);
        }

        if (status.Kind == FileIntakeStatusKind.SelectionCleared)
        {
            return _localization[LocalizationKeys.IntakeSelectionCleared];
        }

        if (status.Kind == FileIntakeStatusKind.FolderDropRejected)
        {
            return _localization[LocalizationKeys.IntakeFolderDropRejected];
        }

        var messages = new List<string>();

        if (status.Added > 0)
        {
            messages.Add(BuildCountMessage(
                status.Added,
                LocalizationKeys.IntakeAddedOne,
                LocalizationKeys.IntakeAddedTwo,
                LocalizationKeys.IntakeAddedMany));
        }

        if (status.Duplicates > 0)
        {
            messages.Add(BuildCountMessage(
                status.Duplicates,
                LocalizationKeys.IntakeDuplicateOne,
                LocalizationKeys.IntakeDuplicateTwo,
                LocalizationKeys.IntakeDuplicateMany));
        }

        if (status.Directories > 0)
        {
            messages.Add(BuildCountMessage(
                status.Directories,
                LocalizationKeys.IntakeFolderOne,
                LocalizationKeys.IntakeFolderTwo,
                LocalizationKeys.IntakeFolderMany));
        }

        if (status.Missing > 0)
        {
            messages.Add(BuildCountMessage(
                status.Missing,
                LocalizationKeys.IntakeMissingOne,
                LocalizationKeys.IntakeMissingTwo,
                LocalizationKeys.IntakeMissingMany));
        }

        if (status.Inaccessible > 0)
        {
            messages.Add(BuildCountMessage(
                status.Inaccessible,
                LocalizationKeys.IntakeInaccessibleOne,
                LocalizationKeys.IntakeInaccessibleTwo,
                LocalizationKeys.IntakeInaccessibleMany));
        }

        if (status.Invalid > 0)
        {
            messages.Add(BuildCountMessage(
                status.Invalid,
                LocalizationKeys.IntakeInvalidOne,
                LocalizationKeys.IntakeInvalidTwo,
                LocalizationKeys.IntakeInvalidMany));
        }

        return string.Join(" ", messages);
    }

    private string BuildCountMessage(int count, string oneKey, string twoKey, string manyKey)
    {
        return count switch
        {
            1 => _localization[oneKey],
            2 => _localization[twoKey],
            _ => _localization.Format(manyKey, count),
        };
    }

    private void SetStatus(FileIntakeStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedFileCountText));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedFileCountText));
        OnPropertyChanged(nameof(HasSelectedFiles));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private enum FileIntakeStatusKind
    {
        None,
        AddResult,
        Removed,
        SelectionCleared,
        FolderDropRejected,
    }

    private readonly record struct FileIntakeStatus(
        FileIntakeStatusKind Kind,
        int Added = 0,
        int Duplicates = 0,
        int Directories = 0,
        int Missing = 0,
        int Inaccessible = 0,
        int Invalid = 0,
        string? FileName = null);
}
