using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public enum FileMetadataError
{
    None,
    Missing,
    Directory,
    Inaccessible,
    InvalidPath,
}

public sealed record FileMetadataResult(SelectedFileItem? File, FileMetadataError Error)
{
    public static FileMetadataResult Success(SelectedFileItem file) => new(file, FileMetadataError.None);

    public static FileMetadataResult Failure(FileMetadataError error) => new(null, error);
}
