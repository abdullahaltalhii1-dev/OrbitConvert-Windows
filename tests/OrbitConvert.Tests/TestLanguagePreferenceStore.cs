using OrbitConvert.App.Localization;

namespace OrbitConvert.Tests;

internal sealed class TestLanguagePreferenceStore(string? languageCode = null)
    : ILanguagePreferenceStore
{
    public string? LanguageCode { get; private set; } = languageCode;

    public string? LoadLanguageCode() => LanguageCode;

    public void SaveLanguageCode(string languageCode)
    {
        LanguageCode = languageCode;
    }
}
