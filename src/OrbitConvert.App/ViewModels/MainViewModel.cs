using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Video;

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

/// <summary>
/// Provides presentation state for the application shell, file intake, localization, and single-image conversion.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private AppSection _currentSection = AppSection.Workspace;
    private AppTheme _selectedTheme = AppTheme.Dark;
    private AccentChoice _selectedAccent = AccentChoice.Blue;

    public MainViewModel()
        : this(
            new FileMetadataProvider(),
            new LocalizationService(new FileLanguagePreferenceStore()))
    {
    }

    public MainViewModel(IFileMetadataProvider metadataProvider)
        : this(
            metadataProvider,
            new LocalizationService(new FileLanguagePreferenceStore()))
    {
    }

    public MainViewModel(
        IFileMetadataProvider metadataProvider,
        LocalizationService localization)
    {
        Localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEnglishLanguage));
            OnPropertyChanged(nameof(IsArabicLanguage));
        };
        FileIntake = new FileIntakeViewModel(metadataProvider, Localization);
        ImageConversion = new ImageConversionViewModel(FileIntake.SelectedFiles,
            new ImageConversionService(new MagickImageEngine()), Localization);
        VideoConversion = new VideoConversionViewModel(FileIntake.SelectedFiles, new VideoConversionService(), Localization);

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

        SetLanguageCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppLanguage language)
            {
                Localization.ChangeLanguage(language);
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

    public ICommand NavigateCommand { get; }

    public ICommand SetThemeCommand { get; }

    public ICommand SetAccentCommand { get; }

    public ICommand SetLanguageCommand { get; }

    public LocalizationService Localization { get; }

    public FileIntakeViewModel FileIntake { get; }

    public ImageConversionViewModel ImageConversion { get; }

    public VideoConversionViewModel VideoConversion { get; }

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

    public bool IsEnglishLanguage => Localization.CurrentLanguage == AppLanguage.English;

    public bool IsArabicLanguage => Localization.CurrentLanguage == AppLanguage.Arabic;

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
