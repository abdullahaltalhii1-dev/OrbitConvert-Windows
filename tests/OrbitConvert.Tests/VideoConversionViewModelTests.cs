using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Video;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class VideoConversionViewModelTests
{
    private static readonly SelectedFileItem Source = new(@"C:\فيديو خاص\clip & 'نسخة'.mkv", "clip & 'نسخة'.mkv", "MKV", 100, "100 B");
    private static readonly VideoInspection Inspection = new("matroska,webm", TimeSpan.FromSeconds(10), 100,
        new(0, "h264", 1920, 1080, 90, 25, "yuv420p"), new(1, "opus", 2, 48000), 1, 1, 0);

    [Fact]
    public async Task InspectSettingsAndDestination_UseOneSelectedUnicodePath()
    {
        var service = new ControlledService();
        var model = Create(service);
        Assert.False(model.InspectCommand.CanExecute(null));
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.SelectedFile = Source;
        Assert.False(model.CanEditSettings);
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal(Source.FullPath, service.InspectedPath);
        Assert.Equal("1080 × 1920", model.Dimensions);
        Assert.Equal("00:00:10", model.Duration);
        Assert.Equal("opus", model.AudioCodec);
        Assert.Equal("100 B", model.SourceSize);
        model.Quality = VideoQuality.Small;
        model.MaxHeight = 480;
        Assert.Equal("270 × 480", model.ExpectedDimensions);
        model.SetDestination(@"C:\فيديو خاص\ناتج جديد.mp4");
        Assert.True(model.ConvertCommand.CanExecute(null));
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal((Source.FullPath, @"C:\فيديو خاص\ناتج جديد.mp4", new VideoConversionOptions(VideoQuality.Small, 480)), service.Request);
        Assert.Equal(@"C:\فيديو خاص\ناتج جديد.mp4", model.CompletedPath);
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.SelectedFile = null;
        Assert.Equal(string.Empty, model.DestinationPath);
        Assert.False(model.CanEditSettings);
    }

    [Fact]
    public async Task BusyConversion_FreezesSettingsPreventsDuplicatesAndWaitsForCleanup()
    {
        var completion = new TaskCompletionSource<VideoConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ControlledService { Conversion = (_, _) => completion.Task };
        var model = await Ready(service);
        var running = model.ConvertCommand.ExecuteAsync();
        var idle = model.WaitForIdleAsync();
        Assert.True(model.IsBusy);
        Assert.True(model.ShowProgress);
        Assert.False(model.CanChooseDestination);
        model.Quality = VideoQuality.High;
        model.MaxHeight = 480;
        model.SelectedFile = null;
        model.SetDestination("ignored.mp4");
        Assert.Equal(VideoQuality.Balanced, model.Quality);
        Assert.Equal(0, model.MaxHeight);
        Assert.Equal(Source, model.SelectedFile);
        await model.ConvertCommand.ExecuteAsync();
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal(1, service.Conversions);
        model.RequestCancellation();
        Assert.True(service.Token.IsCancellationRequested);
        Assert.False(idle.IsCompleted); // A request alone is not proof the encoder stopped or cleanup finished.
        Assert.False(model.CancelCommand.CanExecute(null));
        model.Files.Clear();
        completion.SetResult(new(VideoError.Cancelled));
        await running;
        await idle;
        Assert.False(model.IsBusy);
        Assert.False(model.ShowProgress);
        Assert.Null(model.SelectedFile);
        Assert.Empty(model.CompletedPath);
        Assert.Contains("cancelled", model.StatusMessage);
    }

    [Fact]
    public async Task Inspection_CanBeCancelledAndRemovedSelectionIsNotReused()
    {
        var service = new ControlledService
        {
            Inspect = async token => { await Task.Delay(Timeout.Infinite, token); return new(Inspection, VideoError.None); },
        };
        var model = Create(service);
        model.SelectedFile = Source;
        var running = model.InspectCommand.ExecuteAsync();
        Assert.True(model.CancelCommand.CanExecute(null));
        Assert.False(model.ConvertCommand.CanExecute(null));
        model.Files.Clear();
        await model.CancelCommand.ExecuteAsync();
        await running;
        Assert.Null(model.SelectedFile);
        Assert.False(model.CanEditSettings);
        Assert.Contains("cancelled", model.StatusMessage);
    }

    [Fact]
    public void InlineCancellationCompletion_KeepsFinalStatus()
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var completion = new TaskCompletionSource<VideoInspectionResult>();
            var model = Create(new ControlledService { Inspect = token => { token.Register(() => completion.SetCanceled(token)); return completion.Task; } });
            model.SelectedFile = Source;
            var running = model.InspectCommand.ExecuteAsync();
            model.RequestCancellation();
            Assert.True(running.IsCompletedSuccessfully);
            Assert.True(model.IsIdle);
            Assert.Contains("cancelled", model.StatusMessage);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Progress_UsesRealStageFractionAndIgnoresQueuedReportsAfterCompletion()
    {
        var completion = new TaskCompletionSource<VideoConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<VideoProgress>? progress = null;
        var model = await Ready(new ControlledService { Conversion = (sink, _) => { progress = sink; return completion.Task; } });
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task running;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            running = model.ConvertCommand.ExecuteAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.True(model.IsProgressIndeterminate);
        progress!.Report(new(VideoStage.Converting, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10), 0.4, TimeSpan.FromSeconds(2)));
        context.Drain();
        Assert.Equal(40, model.ProgressPercent);
        Assert.False(model.IsProgressIndeterminate);
        Assert.Contains("Encoding", model.ProgressText);
        progress.Report(new(VideoStage.Verifying, TimeSpan.Zero, null, null, TimeSpan.FromSeconds(3)));
        context.Drain();
        Assert.Contains("Verifying", model.ProgressText);
        Assert.True(model.IsProgressIndeterminate);
        // Complete on this deterministic context so async continuations also run without a real dispatcher.
        completion.SetResult(new(VideoError.None, @"C:\out.mp4"));
        for (var attempt = 0; attempt < 100 && !running.IsCompleted; attempt++)
        {
            context.Drain();
            await Task.Delay(5);
        }
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        progress.Report(new(VideoStage.Converting, TimeSpan.Zero, null, 0.1, TimeSpan.Zero));
        context.Drain();
        Assert.False(model.ShowProgress);
        Assert.Contains("saved and verified", model.StatusMessage);
    }

    [Fact]
    public async Task ErrorsCleanupAndMetadata_RefreshInBothLanguages()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = await Ready(new ControlledService { Conversion = (_, _) => Task.FromResult(new VideoConversionResult(VideoError.OutputFailure, CleanupFailed: true)) }, localization);
        await model.ConvertCommand.ExecuteAsync();
        Assert.Contains("could not be saved", model.StatusMessage);
        Assert.Contains("temporary-file cleanup", model.StatusMessage);
        localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("تعذر حفظ", model.StatusMessage);
        Assert.Contains("تنظيف", model.StatusMessage);
        Assert.Contains("المسارات", model.Streams);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task UnexpectedFault_DoesNotEscapeOrLeaveBusyState()
    {
        var model = await Ready(new ControlledService { Conversion = (_, _) => throw new IOException("test") });
        await model.ConvertCommand.ExecuteAsync();
        Assert.True(model.IsIdle);
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Contains("could not be completed", model.StatusMessage);
        await model.WaitForIdleAsync();
    }

    [Fact]
    public async Task UnknownDurationAndNoAudio_AreHonestAndSettingsRejectInvalidValues()
    {
        var model = Create(new ControlledService { Inspect = _ => Task.FromResult(new VideoInspectionResult(Inspection with { Duration = null, Audio = null }, VideoError.None)) });
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal("Unknown", model.Duration);
        Assert.Equal("No audio", model.AudioCodec);
        model.Quality = (VideoQuality)999;
        model.MaxHeight = 333;
        Assert.Equal(VideoQuality.Balanced, model.Quality);
        Assert.Equal(0, model.MaxHeight);
    }

    [Fact]
    public async Task InvalidInspection_DoesNotEnableConversionOrKeepOldMetadata()
    {
        var model = Create(new ControlledService { Inspect = _ => Task.FromResult(VideoInspectionResult.Failure(VideoError.UnsupportedFormat)) });
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\out.mp4");
        Assert.False(model.CanChooseDestination);
        Assert.False(model.ConvertCommand.CanExecute(null));
        Assert.Equal("—", model.VideoCodec);
        Assert.Contains("container is not supported", model.StatusMessage);
    }

    [Fact]
    public void EveryVideoError_HasEnglishAndArabicResources()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        foreach (var error in Enum.GetValues<VideoError>().Where(error => error != VideoError.None))
        {
            Assert.True(localization.HasTranslation("VideoError" + error, AppLanguage.English));
            Assert.True(localization.HasTranslation("VideoError" + error, AppLanguage.Arabic));
        }
    }

    private static VideoConversionViewModel Create(ControlledService service, LocalizationService? localization = null) =>
        new(new ObservableCollection<SelectedFileItem> { Source }, service, localization ?? new(new TestLanguagePreferenceStore()));

    private static async Task<VideoConversionViewModel> Ready(ControlledService service, LocalizationService? localization = null)
    {
        var model = Create(service, localization);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\out.mp4");
        return model;
    }

    private sealed class ControlledService : IVideoConversionService
    {
        public Func<CancellationToken, Task<VideoInspectionResult>> Inspect { get; init; } = _ => Task.FromResult(new VideoInspectionResult(Inspection, VideoError.None));
        public Func<IProgress<VideoProgress>?, CancellationToken, Task<VideoConversionResult>> Conversion { get; init; } = (_, _) => Task.FromResult(new VideoConversionResult(VideoError.None, @"C:\فيديو خاص\ناتج جديد.mp4"));
        public string? InspectedPath { get; private set; }
        public (string Source, string Destination, VideoConversionOptions Options)? Request { get; private set; }
        public int Conversions { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<VideoInspectionResult> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            InspectedPath = sourcePath;
            return Inspect(cancellationToken);
        }
        public Task<VideoConversionResult> ConvertAsync(string sourcePath, string destinationPath, VideoConversionOptions options,
            IProgress<VideoProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Conversions++;
            Request = (sourcePath, destinationPath, options);
            Token = cancellationToken;
            return Conversion(progress, cancellationToken);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _actions = new();
        public override void Post(SendOrPostCallback callback, object? state) => _actions.Enqueue(() => callback(state));
        public void Drain() { while (_actions.TryDequeue(out var action)) { action(); } }
    }
}
