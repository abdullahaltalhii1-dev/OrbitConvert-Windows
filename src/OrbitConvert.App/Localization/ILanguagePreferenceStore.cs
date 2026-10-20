namespace OrbitConvert.App.Localization;

public interface ILanguagePreferenceStore
{
    string? LoadLanguageCode();

    void SaveLanguageCode(string languageCode);
}
