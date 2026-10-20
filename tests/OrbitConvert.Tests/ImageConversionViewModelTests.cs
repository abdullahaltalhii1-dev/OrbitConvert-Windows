using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class ImageConversionViewModelTests
{
    private static readonly SelectedFileItem Source = new(@"C:\صوري\image.png", "image.png", "PNG", 100, "100 B");

    [Fact]
    public async Task InspectThenChooseDestination_EnablesOnlyTheSupportedDirection()
    {
        var model = Create(new ControlledService());
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal("JPEG", model.OutputFormat);
        Assert.Equal("2 × 3", model.Dimensions);
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.SetDestination(@"C:\صوري\output.jpg");
        Assert.True(model.ConvertCommand.CanExecute(null));
        model.SelectedFile = null;
        Assert.Equal(string.Empty, model.DestinationPath);
        Assert.False(model.ConvertCommand.CanExecute(null));
    }

    [Fact]
    public async Task Convert_BusyStatePreventsDuplicateActionAndFreezesSelection()
    {
        var completion = new TaskCompletionSource<ImageConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ControlledService { PendingConversion = completion.Task };
        var model = Create(service);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\صوري\output.jpg");

        var running = model.ConvertCommand.ExecuteAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.ConvertCommand.CanExecute(null));
        Assert.False(model.InspectCommand.CanExecute(null));
        Assert.False(model.CanChooseDestination);
        model.SelectedFile = null;
        Assert.Equal(Source, model.SelectedFile);
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal(1, service.Calls);
        model.Files.Clear();
        completion.SetResult(new(ImageInspectionError.None, @"C:\صوري\output.jpg"));
        await running;
        Assert.False(model.IsBusy);
        Assert.Contains("saved and verified", model.StatusMessage);
        Assert.Null(model.SelectedFile);
        Assert.Equal(@"C:\صوري\output.jpg", model.CompletedPath);
        Assert.False(model.ConvertCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailureAndSuccessMessages_RefreshInCurrentLanguage()
    {
        var service = new ControlledService();
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = Create(service, localization);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\صوري\output.jpg");
        await model.ConvertCommand.ExecuteAsync();
        Assert.Contains("saved and verified", model.StatusMessage);
        localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("تم حفظ الصورة", model.StatusMessage);
        Assert.Equal(@"C:\صوري\output.jpg", model.CompletedPath);

        service.InspectionError = ImageInspectionError.UnsupportedBitDepth;
        await model.InspectCommand.ExecuteAsync();
        Assert.False(model.IsBusy);
        Assert.Contains("غير مدعومة", model.StatusMessage);
        Assert.False(model.CanChooseDestination);
        localization.ChangeLanguage(AppLanguage.English);
        Assert.Contains("8 bits", model.StatusMessage);
    }

    [Fact]
    public async Task UnexpectedServiceFailure_ResetsBusyAndShowsFriendlyError()
    {
        var service = new ControlledService { PendingConversion = Task.FromException<ImageConversionResult>(new IOException()) };
        var model = Create(service);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\صوري\output.jpg");
        await model.ConvertCommand.ExecuteAsync();
        Assert.False(model.IsBusy);
        Assert.Equal("The operation could not be completed. Please try again.", model.StatusMessage);
    }

    [Fact]
    public async Task RemovingActiveFile_ResetsConversionState()
    {
        var model = Create(new ControlledService());
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.Files.Remove(Source);
        Assert.Null(model.SelectedFile);
        Assert.False(model.CanChooseDestination);
    }

    [Fact]
    public async Task TargetQualityAndResize_ArePassedToService_AndFormatChangeClearsDestination()
    {
        var service = new ControlledService();
        var model = Create(service);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal(90, model.Quality);
        Assert.True(model.PreserveAspectRatio);
        model.SetDestination(@"C:\صوري\output.jpg");
        model.OutputFormat = "WebP";
        Assert.Empty(model.DestinationPath);
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.Quality = 73;
        model.ResizeEnabled = true;
        model.ResizeWidth = "10";
        model.ResizeHeight = "10";
        Assert.Equal("7 × 10", model.ExpectedDimensions);
        model.SetDestination(@"C:\صوري\output.webp");
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal("WebP", service.LastFormat);
        Assert.Equal(new ImageConversionOptions(73, true, 10, 10, true), service.LastOptions);
        model.OutputFormat = "BMP";
        Assert.False(model.IsLossyFormat);
        model.OutputFormat = "PNG";
        Assert.False(model.IsLossyFormat);
        model.OutputFormat = "JPEG";
        Assert.True(model.IsLossyFormat);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9999999999999999999999999999")]
    [InlineData("1.5")]
    [InlineData("8193")]
    [InlineData("")]
    public async Task InvalidDimensionText_DisablesConversionAndLocalizesError(string text)
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = Create(new ControlledService(), localization);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\صوري\output.jpg");
        model.ResizeEnabled = true;
        model.ResizeWidth = text;
        Assert.False(model.CanChooseDestination);
        Assert.False(model.ConvertCommand.CanExecute(null));
        Assert.Equal("—", model.ExpectedDimensions);
        Assert.Contains("positive dimensions", model.SettingsError);
        localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("أبعادًا موجبة", model.SettingsError);
        model.ResizeEnabled = false;
        Assert.Equal("2 × 3", model.ExpectedDimensions);
        Assert.True(model.ConvertCommand.CanExecute(null));
    }

    private static ImageConversionViewModel Create(ControlledService service, LocalizationService? localization = null) =>
        new(new ObservableCollection<SelectedFileItem> { Source }, service,
            localization ?? new LocalizationService(new TestLanguagePreferenceStore()));

    private sealed class ControlledService : IImageConversionService
    {
        public Task<ImageConversionResult>? PendingConversion { get; init; }
        public ImageInspectionError InspectionError { get; set; }
        public int Calls { get; private set; }
        public ImageConversionOptions? LastOptions { get; private set; }
        public string? LastFormat { get; private set; }
        public Task<ImageInspectionResult> InspectAsync(string path) => Task.FromResult(InspectionError == ImageInspectionError.None
            ? ImageInspectionResult.Success(new(2, 3, 8, "PNG", true)) : ImageInspectionResult.Failure(InspectionError));
        public Task<ImageConversionResult> ConvertAsync(string source, string destination, string format, ImageConversionOptions? options = null)
        {
            Calls++;
            LastOptions = options;
            LastFormat = format;
            return PendingConversion ?? Task.FromResult(new ImageConversionResult(ImageInspectionError.None, destination));
        }
    }
}
