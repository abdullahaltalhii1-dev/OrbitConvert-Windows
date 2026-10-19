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
}
