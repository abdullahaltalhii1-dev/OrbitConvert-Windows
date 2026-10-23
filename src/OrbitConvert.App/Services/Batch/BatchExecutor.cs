using System.IO;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.App.Services.Batch;

/// <summary>Only adapts existing services and their verified publication contracts; contains no encoder or file writer.</summary>
public sealed class BatchExecutor : IBatchExecutor
{
    private readonly ImageConversionService _images;
    private readonly IVideoConversionService _video;
    private readonly IAudioConversionService _audio;
    private readonly IDocumentConversionService _documents;
    public BatchExecutor(ImageConversionService? images = null, IVideoConversionService? video = null,
        IAudioConversionService? audio = null, IDocumentConversionService? documents = null)
    {
        _images = images ?? new(new MagickImageEngine()); _video = video ?? new VideoConversionService();
        _audio = audio ?? new AudioConversionService(); _documents = documents ?? new DocumentConversionService();
    }
    public async Task<BatchOutcome> ExecuteAsync(BatchRequest request, IProgress<BatchProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!BatchPreset.All.Contains(request.Preset) || !request.Preset.Accepts(request.Source) || !request.ImageOptions.IsValid || !request.VideoOptions.IsValid)
        { return new("BatchUnsupported"); }
        using var sourcePin = new FileStream(request.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (BatchSourceStamp.Read(request.Source) != request.SourceStamp) { return new("BatchSourceChanged"); }
        progress.Report(new(BatchState.Inspecting));
        switch (request.Preset.Engine)
        {
            case BatchEngine.Image:
                // Native calls cannot be interrupted mid-call. The service observes cancellation
                // between bounded stages and before publication, and returns only after cleanup.
                progress.Report(new(BatchState.Running));
                var image = await _images.ConvertAsync(request.Source, request.Destination, request.Preset.ImageFormat!, request.ImageOptions, token);
                return new(image.IsSuccess ? null : "ImageError" + image.Error, image.OutputPath, image.Error == ImageInspectionError.Cancelled, image.CleanupFailed);
            case BatchEngine.Video:
                var videoInspection = await _video.InspectAsync(request.Source, token);
                if (!videoInspection.IsSuccess) { return new("VideoError" + videoInspection.Error, Cancelled: videoInspection.Error == VideoError.Cancelled, CleanupWarning: videoInspection.Error == VideoError.ProcessTerminationFailed); }
                token.ThrowIfCancellationRequested(); progress.Report(new(BatchState.Running));
                var video = await _video.ConvertAsync(request.Source, request.Destination, request.VideoOptions,
                    new ForwardProgress<VideoProgress>(p => progress.Report(new(p.Stage == VideoStage.Verifying ? BatchState.Verifying : BatchState.Running, p.Fraction))), token);
                return new(video.IsSuccess ? null : "VideoError" + video.Error, video.OutputPath, video.Error == VideoError.Cancelled, video.CleanupFailed || video.Error == VideoError.ProcessTerminationFailed);
            case BatchEngine.Audio:
                var audioInspection = await _audio.InspectAsync(request.Source, token);
                if (!audioInspection.IsSuccess) { return new("AudioError" + audioInspection.Error, Cancelled: audioInspection.Error == AudioError.Cancelled, CleanupWarning: audioInspection.Error == AudioError.ProcessTerminationFailed); }
                var settingsError = request.AudioOptions.Validate(audioInspection.Inspection!);
                if (settingsError != AudioError.None) { return new("AudioError" + settingsError); }
                token.ThrowIfCancellationRequested(); progress.Report(new(BatchState.Running));
                var audio = await _audio.ConvertAsync(request.Source, request.Destination, request.AudioOptions,
                    new ForwardProgress<AudioProgress>(p => progress.Report(new(p.Stage == AudioStage.Verifying ? BatchState.Verifying : BatchState.Running, p.Fraction))), token);
                return new(audio.IsSuccess ? null : "AudioError" + audio.Error, audio.OutputPath, audio.Error == AudioError.Cancelled, audio.CleanupFailed || audio.Error == AudioError.ProcessTerminationFailed);
            case BatchEngine.Document:
                var documentInspection = await _documents.InspectAsync(request.Source, token);
                if (!documentInspection.IsSuccess) { return new("DocumentError" + documentInspection.Error, Cancelled: documentInspection.Error == DocumentError.Cancelled, CleanupWarning: documentInspection.CleanupFailed || documentInspection.Error == DocumentError.ProcessTerminationFailed); }
                if (!DocumentFormats.Outputs(documentInspection.Inspection!.Format).Contains(request.Preset.DocumentFormat!.Value)) { return new("BatchUnsupported"); }
                token.ThrowIfCancellationRequested(); progress.Report(new(BatchState.Running));
                var document = await _documents.ConvertAsync(request.Source, request.Destination, request.Preset.DocumentFormat.Value,
                    new ForwardProgress<DocumentStage>(p => progress.Report(new(p == DocumentStage.Verifying ? BatchState.Verifying : p == DocumentStage.Inspecting ? BatchState.Inspecting : BatchState.Running))), token);
                return new(document.IsSuccess ? null : "DocumentError" + document.Error, document.OutputPath, document.Error == DocumentError.Cancelled, document.CleanupFailed || document.Error == DocumentError.ProcessTerminationFailed);
            default: return new("BatchUnsupported");
        }
    }
    private sealed class ForwardProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
