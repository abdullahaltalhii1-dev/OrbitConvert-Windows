using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void AppTitle_ReturnsProductName()
    {
        var viewModel = new MainViewModel();

        Assert.Equal("OrbitConvert", viewModel.AppTitle);
    }

    [Fact]
    public void Defaults_DescribeDarkWorkspacePreview()
    {
        var viewModel = new MainViewModel();

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
        var viewModel = new MainViewModel();

        viewModel.NavigateCommand.Execute(AppSection.Images);

        Assert.Equal(AppSection.Images, viewModel.CurrentSection);
        Assert.True(viewModel.IsImagesSelected);
        Assert.False(viewModel.IsWorkspaceSelected);
    }

    [Fact]
    public void AppearanceCommands_UpdateSessionState()
    {
        var viewModel = new MainViewModel();

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
        var viewModel = new MainViewModel();

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
}
