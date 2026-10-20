using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows;

namespace OrbitConvert.App.Localization;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new(
        "OrbitConvert.App.Resources.Strings",
        typeof(LocalizationService).Assembly);

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo ArabicCulture = CultureInfo.GetCultureInfo("ar");

    private readonly ILanguagePreferenceStore _preferenceStore;
    private AppLanguage _currentLanguage;

    public LocalizationService(ILanguagePreferenceStore preferenceStore)
    {
        _preferenceStore = preferenceStore ?? throw new ArgumentNullException(nameof(preferenceStore));
        _currentLanguage = ParseLanguageCode(preferenceStore.LoadLanguageCode()) ?? AppLanguage.English;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public AppLanguage CurrentLanguage => _currentLanguage;

    public CultureInfo CurrentCulture => CurrentLanguage == AppLanguage.Arabic ? ArabicCulture : EnglishCulture;

    public FlowDirection FlowDirection => CurrentLanguage == AppLanguage.Arabic
        ? FlowDirection.RightToLeft
        : FlowDirection.LeftToRight;

    public string this[string key] => GetString(key);

    public bool ChangeLanguage(AppLanguage language)
    {
        if (_currentLanguage == language)
        {
            return false;
        }

        _currentLanguage = language;
        _preferenceStore.SaveLanguageCode(GetLanguageCode(language));

        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(CurrentCulture));
        OnPropertyChanged(nameof(FlowDirection));
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return GetDefinedString(key, CurrentLanguage)
            ?? GetDefinedString(key, AppLanguage.English)
            ?? key;
    }

    public string Format(string key, params object[] arguments)
    {
        return string.Format(CurrentCulture, GetString(key), arguments);
    }

    public bool HasTranslation(string key, AppLanguage language)
    {
        return GetDefinedString(key, language) is not null;
    }

    public static string GetLanguageCode(AppLanguage language)
    {
        return language == AppLanguage.Arabic ? "ar" : "en";
    }

    private static AppLanguage? ParseLanguageCode(string? languageCode)
    {
        return languageCode?.Trim().ToLowerInvariant() switch
        {
            "en" => AppLanguage.English,
            "ar" => AppLanguage.Arabic,
            _ => null,
        };
    }

    private static string? GetDefinedString(string key, AppLanguage language)
    {
        var culture = language == AppLanguage.Arabic ? ArabicCulture : CultureInfo.InvariantCulture;
        var resourceSet = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        return resourceSet?.GetString(key, ignoreCase: false);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
