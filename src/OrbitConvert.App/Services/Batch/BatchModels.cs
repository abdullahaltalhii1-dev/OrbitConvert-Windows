using System.IO;
using OrbitConvert.App.Models;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.Services.Video;

namespace OrbitConvert.App.Services.Batch;

public enum BatchEngine { Image, Video, Audio, Document }
public enum BatchState { Pending, Inspecting, Running, Verifying, Completed, Failed, Cancelled }

/// <summary>Immutable, reviewed preset. Engine options are snapshotted per job, not read from another panel.</summary>
public sealed record BatchPreset(string Key, BatchEngine Engine, string Extension,
    string? ImageFormat = null, AudioFormat? AudioFormat = null, DocumentFormat? DocumentFormat = null)
{
    public static IReadOnlyList<BatchPreset> All { get; } = Array.AsReadOnly(new BatchPreset[]
    {
        new("ImagePNG", BatchEngine.Image, ".png", "PNG"), new("ImageJPEG", BatchEngine.Image, ".jpg", "JPEG"),
        new("ImageWebP", BatchEngine.Image, ".webp", "WebP"), new("ImageBMP", BatchEngine.Image, ".bmp", "BMP"),
        new("VideoMP4", BatchEngine.Video, ".mp4"),
        new("AudioMP3", BatchEngine.Audio, ".mp3", AudioFormat: Services.Audio.AudioFormat.MP3),
        new("AudioWAV", BatchEngine.Audio, ".wav", AudioFormat: Services.Audio.AudioFormat.WAV),
        new("AudioFLAC", BatchEngine.Audio, ".flac", AudioFormat: Services.Audio.AudioFormat.FLAC),
        new("DocumentDOCX", BatchEngine.Document, ".docx", DocumentFormat: Services.Documents.DocumentFormat.DOCX),
        new("DocumentODT", BatchEngine.Document, ".odt", DocumentFormat: Services.Documents.DocumentFormat.ODT),
        new("DocumentTXT", BatchEngine.Document, ".txt", DocumentFormat: Services.Documents.DocumentFormat.TXT),
        new("DocumentMarkdown", BatchEngine.Document, ".md", DocumentFormat: Services.Documents.DocumentFormat.Markdown),
        new("DocumentHTML", BatchEngine.Document, ".html", DocumentFormat: Services.Documents.DocumentFormat.HTML),
    });

    public bool Accepts(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        return Engine switch
        {
            BatchEngine.Image => extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp",
            BatchEngine.Video => extension is ".mp4" or ".mov" or ".m4v" or ".mkv" or ".webm" or ".avi",
            BatchEngine.Audio => extension is ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".opus" or ".m4a" or ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi",
            BatchEngine.Document => DocumentSource(extension) is { } format && DocumentFormats.Outputs(format).Contains(DocumentFormat!.Value),
            _ => false,
        };
    }
    private static DocumentFormat? DocumentSource(string extension) => extension switch
    {
        ".docx" => Services.Documents.DocumentFormat.DOCX,
        ".odt" => Services.Documents.DocumentFormat.ODT,
        ".rtf" => Services.Documents.DocumentFormat.RTF,
        ".txt" => Services.Documents.DocumentFormat.TXT,
        ".md" => Services.Documents.DocumentFormat.Markdown,
        ".html" or ".htm" => Services.Documents.DocumentFormat.HTML,
        _ => null,
    };
}

public sealed record BatchSourceStamp(long Length, DateTime LastWriteUtc)
{
    public static BatchSourceStamp Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { throw new IOException(); }
        return new(file.Length, file.LastWriteTimeUtc);
    }
}

public sealed record BatchRequest(string Source, string Destination, BatchPreset Preset, BatchSourceStamp SourceStamp)
{
    public ImageConversionOptions ImageOptions { get; init; } = new();
    public VideoConversionOptions VideoOptions { get; init; } = new();
    public AudioConversionOptions AudioOptions => new(Preset.AudioFormat ?? AudioFormat.MP3);
}
public sealed record BatchProgress(BatchState State, double? Fraction = null);
public sealed record BatchOutcome(string? ErrorKey = null, string? OutputPath = null, bool Cancelled = false, bool CleanupWarning = false)
{
    public bool IsSuccess => ErrorKey is null && OutputPath is not null && !Cancelled;
}
public interface IBatchExecutor
{
    Task<BatchOutcome> ExecuteAsync(BatchRequest request, IProgress<BatchProgress> progress, CancellationToken token);
}
