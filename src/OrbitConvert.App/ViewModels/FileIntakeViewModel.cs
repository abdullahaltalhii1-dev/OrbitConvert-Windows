using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;

namespace OrbitConvert.App.ViewModels;

public sealed class FileIntakeViewModel : INotifyPropertyChanged
{
    private readonly IFileMetadataProvider _metadataProvider;
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _statusMessage = string.Empty;

    public FileIntakeViewModel(IFileMetadataProvider metadataProvider)
    {
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));
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

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string SelectedFileCountText => SelectedFileCount switch
    {
        0 => "No files selected",
        1 => "1 file selected",
        _ => $"{SelectedFileCount} files selected",
    };

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

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

        StatusMessage = BuildStatusMessage(added, duplicates, directories, missing, inaccessible, invalid);
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
        StatusMessage = $"Removed {file.FileName}.";
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
        StatusMessage = "Selection cleared.";
    }

    public void ReportFolderDropRejected()
    {
        StatusMessage = "Folders aren’t supported yet. Drop individual files instead.";
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

    private static string BuildStatusMessage(
        int added,
        int duplicates,
        int directories,
        int missing,
        int inaccessible,
        int invalid)
    {
        var messages = new List<string>();

        if (added > 0)
        {
            messages.Add(added == 1 ? "Added 1 file." : $"Added {added} files.");
        }

        if (duplicates > 0)
        {
            messages.Add(duplicates == 1
                ? "1 duplicate was already selected."
                : $"{duplicates} duplicates were already selected.");
        }

        if (directories > 0)
        {
            messages.Add(directories == 1
                ? "1 folder was skipped; folders aren’t supported yet."
                : $"{directories} folders were skipped; folders aren’t supported yet.");
        }

        if (missing > 0)
        {
            messages.Add(missing == 1 ? "1 file could not be found." : $"{missing} files could not be found.");
        }

        if (inaccessible > 0)
        {
            messages.Add(inaccessible == 1
                ? "1 file could not be accessed."
                : $"{inaccessible} files could not be accessed.");
        }

        if (invalid > 0)
        {
            messages.Add(invalid == 1 ? "1 file path was not valid." : $"{invalid} file paths were not valid.");
        }

        return string.Join(" ", messages);
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
}
