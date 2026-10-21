using System.Collections.ObjectModel;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class AudioConversionViewModelTests
{
    private static readonly SelectedFileItem Source = new(@"C:\أصوات خاصة\ملف & test.mkv", "ملف & test.mkv", "MKV", 100, "100 B");
    private static readonly AudioInspection Input = new("matroska,webm", TimeSpan.FromSeconds(10), 100,
        new(2, "flac", 2, "stereo", 48000, "s16", 16, null), 2, 1, 0);

    [Fact]
    public async Task InspectAndSettings_EnableOnlyAValidDestinationAndClearFormatMismatches()
    {
        var service = new ControlledService();
        var model = Create(service);
        Assert.False(model.InspectCommand.CanExecute(null));
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal("flac", model.Codec);
        Assert.Equal("Unknown", model.Bitrate);
        Assert.Contains("#2", model.Streams);
        Assert.Contains("Extract", model.ModeText);
        model.SetDestination(@"C:\أصوات خاصة\out.mp3");
        Assert.True(model.ConvertCommand.CanExecute(null));
        model.Format = AudioFormat.WAV;
        Assert.Empty(model.DestinationPath);
        Assert.False(model.IsMp3);
        Assert.False(model.IsFlac);
        Assert.Contains("16-bit", model.FormatHelp);
        model.Format = AudioFormat.FLAC;
        model.FlacCompression = 8;
        model.SampleRate = 44100;
        model.SetDestination(@"C:\أصوات خاصة\ناتج.flac");
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal(new AudioConversionOptions(AudioFormat.FLAC, SampleRate: 44100, FlacCompression: 8), service.Options);
        Assert.Equal(Source.FullPath, service.SourcePath);
        Assert.Contains("saved and verified", model.StatusMessage);
        Assert.False(model.ConvertCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyState_BlocksDuplicatesSelectionAndConflictingEnginePanels()
    {
        var completion = new TaskCompletionSource<AudioConversionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ControlledService { Convert = (_, _) => completion.Task };
        var main = new MainViewModel(new FileMetadataProvider(), new(new TestLanguagePreferenceStore()), service);
        main.FileIntake.SelectedFiles.Add(Source);
        var model = main.AudioConversion;
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\out.mp3");
        var running = model.ConvertCommand.ExecuteAsync();
        var idle = model.WaitForIdleAsync();
        Assert.False(main.CanUseImages);
        Assert.False(main.CanUseVideo);
        Assert.True(model.IsBusy);
        Assert.False(model.CanEditSettings);
        model.SelectedFile = null;
        model.BitrateKbps = 320;
        Assert.Equal(Source, model.SelectedFile);
        Assert.Equal(192, model.BitrateKbps);
        await model.ConvertCommand.ExecuteAsync();
        Assert.Equal(1, service.Calls);
        model.RequestCancellation();
        Assert.True(service.Token.IsCancellationRequested);
        Assert.False(idle.IsCompleted);
        Assert.False(model.CancelCommand.CanExecute(null));
        main.FileIntake.SelectedFiles.Clear();
        completion.SetResult(new(AudioError.Cancelled));
        await running;
        await idle;
        Assert.Null(model.SelectedFile);
        Assert.True(main.CanUseImages);
        Assert.True(main.CanUseVideo);
        Assert.Contains("cancelled", model.StatusMessage);
    }

    [Fact]
    public async Task IncompatibleSourceRate_RequiresAnExplicitResampleChoice()
    {
        var model = Create(new ControlledService { Inspection = Input with { Audio = Input.Audio with { SampleRate = 96000 } } });
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.False(model.CanChooseDestination);
        Assert.Contains("44.1", model.SettingsError);
        model.SampleRate = 48000;
        Assert.True(model.CanChooseDestination);
        model.BitrateKbps = 999;
        model.FlacCompression = 999;
        Assert.Equal(192, model.BitrateKbps);
        Assert.Equal(5, model.FlacCompression);
    }

    [Fact]
    public async Task Localization_RefreshesCompletedStateAndCleanupErrors()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        var model = Create(new ControlledService { Convert = (_, _) => Task.FromResult(new AudioConversionResult(AudioError.OutputFailure, CleanupFailed: true)) }, localization);
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\out.mp3");
        await model.ConvertCommand.ExecuteAsync();
        Assert.Contains("could not be saved", model.StatusMessage);
        localization.ChangeLanguage(AppLanguage.Arabic);
        Assert.Contains("تعذر حفظ", model.StatusMessage);
        Assert.Contains("تنظيف", model.StatusMessage);
        Assert.Contains("المسار", model.Streams);
    }

    [Fact]
    public async Task UnexpectedFault_ResetsBusyWithoutEscapingToWpf()
    {
        var model = Create(new ControlledService { Convert = (_, _) => throw new IOException("test") });
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        model.SetDestination(@"C:\out.mp3");
        await model.ConvertCommand.ExecuteAsync();
        Assert.True(model.IsIdle);
        Assert.Contains("could not be completed", model.StatusMessage);
        await model.WaitForIdleAsync();
    }

    [Fact]
    public async Task UnknownDurationAndFailedInspection_DoNotPretendToBeReady()
    {
        var model = Create(new ControlledService { Inspection = Input with { Duration = null } });
        model.SelectedFile = Source;
        await model.InspectCommand.ExecuteAsync();
        Assert.Equal("Unknown", model.Duration);
        var rejected = Create(new ControlledService { InspectionError = AudioError.NoAudioStream });
        rejected.SelectedFile = Source;
        await rejected.InspectCommand.ExecuteAsync();
        Assert.False(rejected.CanChooseDestination);
        Assert.Contains("No audio", rejected.StatusMessage);
    }

    [Fact]
    public void EveryAudioError_HasTwoTranslations()
    {
        var localization = new LocalizationService(new TestLanguagePreferenceStore());
        foreach (var error in Enum.GetValues<AudioError>().Where(error => error != AudioError.None))
        {
            Assert.True(localization.HasTranslation("AudioError" + error, AppLanguage.English));
            Assert.True(localization.HasTranslation("AudioError" + error, AppLanguage.Arabic));
        }
    }

    private static AudioConversionViewModel Create(ControlledService service, LocalizationService? localization = null) =>
        new(new ObservableCollection<SelectedFileItem> { Source }, service, localization ?? new(new TestLanguagePreferenceStore()));

    private sealed class ControlledService : IAudioConversionService
    {
        public AudioInspection Inspection { get; init; } = Input;
        public AudioError InspectionError { get; init; }
        public Func<IProgress<AudioProgress>?, CancellationToken, Task<AudioConversionResult>> Convert { get; init; } = (_, _) => Task.FromResult(new AudioConversionResult(AudioError.None, @"C:\out.flac"));
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public AudioConversionOptions? Options { get; private set; }
        public string? SourcePath { get; private set; }
        public Task<AudioInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(InspectionError == AudioError.None ? new(Inspection, AudioError.None) : AudioInspectionResult.Failure(InspectionError));
        public Task<AudioConversionResult> ConvertAsync(string sourcePath, string destinationPath, AudioConversionOptions options,
            IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            Options = options;
            SourcePath = sourcePath;
            return Convert(progress, cancellationToken);
        }
    }
}
