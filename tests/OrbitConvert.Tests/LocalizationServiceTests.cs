using System.Windows;
using OrbitConvert.App.Localization;

namespace OrbitConvert.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void FreshPreference_DefaultsToEnglishAndLeftToRight()
    {
        var service = new LocalizationService(new TestLanguagePreferenceStore());

        Assert.Equal(AppLanguage.English, service.CurrentLanguage);
        Assert.Equal("en", service.CurrentCulture.TwoLetterISOLanguageName);
        Assert.Equal(FlowDirection.LeftToRight, service.FlowDirection);
        Assert.Equal("Workspace", service["NavWorkspace"]);
    }

    [Fact]
    public void SwitchingLanguage_UpdatesCultureDirectionAndResourcesBothWays()
    {
        var service = new LocalizationService(new TestLanguagePreferenceStore());

        Assert.True(service.ChangeLanguage(AppLanguage.Arabic));
        Assert.Equal("ar", service.CurrentCulture.TwoLetterISOLanguageName);
        Assert.Equal(FlowDirection.RightToLeft, service.FlowDirection);
        Assert.Equal("مساحة العمل", service["NavWorkspace"]);

        Assert.True(service.ChangeLanguage(AppLanguage.English));
        Assert.Equal(FlowDirection.LeftToRight, service.FlowDirection);
        Assert.Equal("Workspace", service["NavWorkspace"]);
    }

    [Fact]
    public void SelectedLanguage_PersistsAcrossServiceInstances()
    {
        var preference = new TestLanguagePreferenceStore();
        var firstService = new LocalizationService(preference);
        firstService.ChangeLanguage(AppLanguage.Arabic);

        var restartedService = new LocalizationService(preference);

        Assert.Equal("ar", preference.LanguageCode);
        Assert.Equal(AppLanguage.Arabic, restartedService.CurrentLanguage);
        Assert.Equal(FlowDirection.RightToLeft, restartedService.FlowDirection);
    }

    [Fact]
    public void FilePreferenceStore_RoundTripsOnlyLanguageCode()
    {
        var preferencePath = Path.Combine(
            Path.GetTempPath(),
            "OrbitConvert.Tests",
            Guid.NewGuid().ToString("N"),
            "language.txt");

        try
        {
            var firstService = new LocalizationService(new FileLanguagePreferenceStore(preferencePath));
            firstService.ChangeLanguage(AppLanguage.Arabic);

            var restartedService = new LocalizationService(new FileLanguagePreferenceStore(preferencePath));

            Assert.Equal("ar", File.ReadAllText(preferencePath));
            Assert.Equal(AppLanguage.Arabic, restartedService.CurrentLanguage);
        }
        finally
        {
            var preferenceDirectory = Path.GetDirectoryName(preferencePath)!;
            if (Directory.Exists(preferenceDirectory))
            {
                Directory.Delete(preferenceDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("fr")]
    public void InvalidPreference_RecoversToEnglish(string invalidCode)
    {
        var service = new LocalizationService(new TestLanguagePreferenceStore(invalidCode));

        Assert.Equal(AppLanguage.English, service.CurrentLanguage);
        Assert.Equal(FlowDirection.LeftToRight, service.FlowDirection);
    }

    [Fact]
    public void RequiredKeys_ExistInEnglishAndArabic()
    {
        var service = new LocalizationService(new TestLanguagePreferenceStore());

        foreach (var key in LocalizationKeys.RequiredKeys)
        {
            Assert.True(service.HasTranslation(key, AppLanguage.English), $"Missing English resource: {key}");
            Assert.True(service.HasTranslation(key, AppLanguage.Arabic), $"Missing Arabic resource: {key}");
        }
    }

    [Fact]
    public void MissingArabicTranslation_FallsBackToEnglish()
    {
        var service = new LocalizationService(new TestLanguagePreferenceStore("ar"));

        Assert.Equal("English fallback text", service["FallbackProbe"]);
        Assert.Equal("UnknownResourceKey", service["UnknownResourceKey"]);
    }
}
