using OrbitConvert.App.Localization;
using OrbitConvert.App.Services;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void AppTitle_ReturnsProductName()
    {
        var viewModel = CreateViewModel();

        Assert.Equal("OrbitConvert", viewModel.AppTitle);
    }

    [Fact]
    public void Defaults_DescribeDarkWorkspacePreview()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(AppSection.Workspace, viewModel.CurrentSection);
        Assert.Equal(AppTheme.Dark, viewModel.SelectedTheme);
        Assert.Equal(AccentChoice.Blue, viewModel.SelectedAccent);
        Assert.True(viewModel.IsWorkspaceSelected);
        Assert.True(viewModel.IsDarkTheme);
        Assert.True(viewModel.IsBlueAccent);
    }

    [Fact]
    public void NavigateCommand_ChangesSelectedPreviewSection()
    {
        var viewModel = CreateViewModel();

        viewModel.NavigateCommand.Execute(AppSection.Images);

        Assert.Equal(AppSection.Images, viewModel.CurrentSection);
        Assert.True(viewModel.IsImagesSelected);
        Assert.False(viewModel.IsWorkspaceSelected);
    }

    [Fact]
    public void AppearanceCommands_UpdateSessionState()
    {
        var viewModel = CreateViewModel();

        viewModel.SetThemeCommand.Execute(AppTheme.Light);
        viewModel.SetAccentCommand.Execute(AccentChoice.Teal);

        Assert.Equal(AppTheme.Light, viewModel.SelectedTheme);
        Assert.Equal(AccentChoice.Teal, viewModel.SelectedAccent);
        Assert.True(viewModel.IsLightTheme);
        Assert.True(viewModel.IsTealAccent);
    }

    [Fact]
    public void ImagePreviewSettings_UpdateTextOnlyOutputPreview()
    {
        var viewModel = CreateViewModel();

        viewModel.SetOutputFormatCommand.Execute(ImageOutputFormat.Jpg);
        viewModel.PreviewQuality = 75;

        Assert.Equal("landscape_demo.jpg", viewModel.OutputPreviewFileName);
        Assert.Equal("Preview only · Quality 75%", viewModel.OutputPreviewSummary);
        Assert.True(viewModel.IsQualityEnabled);

        viewModel.SetOutputFormatCommand.Execute(ImageOutputFormat.Png);

        Assert.Equal("landscape_demo.png", viewModel.OutputPreviewFileName);
        Assert.Equal("Lossless preview · Quality control not applicable", viewModel.OutputPreviewSummary);
        Assert.False(viewModel.IsQualityEnabled);
    }

    [Fact]
    public void SetLanguageCommand_SwitchesDirectionAndPreviewText()
    {
        var viewModel = CreateViewModel();
        viewModel.SetOutputFormatCommand.Execute(ImageOutputFormat.Jpg);

        viewModel.SetLanguageCommand.Execute(AppLanguage.Arabic);

        Assert.True(viewModel.IsArabicLanguage);
        Assert.False(viewModel.IsEnglishLanguage);
        Assert.Equal(System.Windows.FlowDirection.RightToLeft, viewModel.Localization.FlowDirection);
        Assert.Equal("معاينة فقط · الجودة 82%", viewModel.OutputPreviewSummary);

        viewModel.SetLanguageCommand.Execute(AppLanguage.English);

        Assert.True(viewModel.IsEnglishLanguage);
        Assert.Equal(System.Windows.FlowDirection.LeftToRight, viewModel.Localization.FlowDirection);
    }

    private static MainViewModel CreateViewModel()
    {
        return new MainViewModel(
            new FileMetadataProvider(),
            new LocalizationService(new TestLanguagePreferenceStore()));
    }
}
