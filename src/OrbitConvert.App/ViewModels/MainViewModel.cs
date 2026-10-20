using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OrbitConvert.App.Services;

namespace OrbitConvert.App.ViewModels;

public enum AppSection
{
    Workspace,
    Images,
    Video,
    Audio,
    Documents,
    Settings,
}

public enum AppTheme
{
    Dark,
    Light,
}

public enum AccentChoice
{
    Blue,
    Violet,
    Teal,
}

public enum ImageOutputFormat
{
    WebP,
    Jpg,
    Png,
}

/// <summary>
/// Provides presentation state for the application shell and Phase 1.2 file intake.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private AppSection _currentSection = AppSection.Workspace;
    private AppTheme _selectedTheme = AppTheme.Dark;
    private AccentChoice _selectedAccent = AccentChoice.Blue;
    private ImageOutputFormat _selectedOutputFormat = ImageOutputFormat.WebP;
    private double _previewQuality = 82;

    public MainViewModel()
        : this(new FileMetadataProvider())
    {
    }

    public MainViewModel(IFileMetadataProvider metadataProvider)
    {
        FileIntake = new FileIntakeViewModel(metadataProvider);

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppSection section)
            {
                CurrentSection = section;
            }
        });

        SetThemeCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppTheme theme)
            {
                SelectedTheme = theme;
            }
        });

        SetAccentCommand = new RelayCommand(parameter =>
        {
            if (parameter is AccentChoice accent)
            {
                SelectedAccent = accent;
            }
        });

        SetOutputFormatCommand = new RelayCommand(parameter =>
        {
            if (parameter is ImageOutputFormat format)
            {
                SelectedOutputFormat = format;
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AppTitle => "OrbitConvert";

    public AppSection CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (!SetField(ref _currentSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsWorkspaceSelected));
            OnPropertyChanged(nameof(IsImagesSelected));
            OnPropertyChanged(nameof(IsVideoSelected));
            OnPropertyChanged(nameof(IsAudioSelected));
            OnPropertyChanged(nameof(IsDocumentsSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
        }
    }

    public AppTheme SelectedTheme
    {
        get => _selectedTheme;
        private set
        {
            if (!SetField(ref _selectedTheme, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
        }
    }

    public AccentChoice SelectedAccent
    {
        get => _selectedAccent;
        private set
        {
            if (!SetField(ref _selectedAccent, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsBlueAccent));
            OnPropertyChanged(nameof(IsVioletAccent));
            OnPropertyChanged(nameof(IsTealAccent));
        }
    }

    public ImageOutputFormat SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        private set
        {
            if (!SetField(ref _selectedOutputFormat, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsWebPSelected));
            OnPropertyChanged(nameof(IsJpgSelected));
            OnPropertyChanged(nameof(IsPngSelected));
            OnPropertyChanged(nameof(IsQualityEnabled));
            OnPropertyChanged(nameof(OutputPreviewFileName));
            OnPropertyChanged(nameof(OutputPreviewSummary));
        }
    }

    public double PreviewQuality
    {
        get => _previewQuality;
        set
        {
            var clampedValue = Math.Clamp(value, 25, 100);
            if (!SetField(ref _previewQuality, clampedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(PreviewQualityText));
            OnPropertyChanged(nameof(OutputPreviewSummary));
        }
    }

    public ICommand NavigateCommand { get; }

    public ICommand SetThemeCommand { get; }

    public ICommand SetAccentCommand { get; }

    public ICommand SetOutputFormatCommand { get; }

    public FileIntakeViewModel FileIntake { get; }

    public bool IsWorkspaceSelected => CurrentSection == AppSection.Workspace;

    public bool IsImagesSelected => CurrentSection == AppSection.Images;

    public bool IsVideoSelected => CurrentSection == AppSection.Video;

    public bool IsAudioSelected => CurrentSection == AppSection.Audio;

    public bool IsDocumentsSelected => CurrentSection == AppSection.Documents;

    public bool IsSettingsSelected => CurrentSection == AppSection.Settings;

    public bool IsDarkTheme => SelectedTheme == AppTheme.Dark;

    public bool IsLightTheme => SelectedTheme == AppTheme.Light;

    public bool IsBlueAccent => SelectedAccent == AccentChoice.Blue;

    public bool IsVioletAccent => SelectedAccent == AccentChoice.Violet;

    public bool IsTealAccent => SelectedAccent == AccentChoice.Teal;

    public bool IsWebPSelected => SelectedOutputFormat == ImageOutputFormat.WebP;

    public bool IsJpgSelected => SelectedOutputFormat == ImageOutputFormat.Jpg;

    public bool IsPngSelected => SelectedOutputFormat == ImageOutputFormat.Png;

    public bool IsQualityEnabled => SelectedOutputFormat != ImageOutputFormat.Png;

    public string PreviewQualityText => $"{Math.Round(PreviewQuality):0}%";

    public string OutputPreviewFileName => SelectedOutputFormat switch
    {
        ImageOutputFormat.WebP => "landscape_demo.webp",
        ImageOutputFormat.Jpg => "landscape_demo.jpg",
        ImageOutputFormat.Png => "landscape_demo.png",
        _ => "landscape_demo.preview",
    };

    public string OutputPreviewSummary => SelectedOutputFormat == ImageOutputFormat.Png
        ? "Lossless preview · Quality control not applicable"
        : $"Preview only · Quality {Math.Round(PreviewQuality):0}%";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
