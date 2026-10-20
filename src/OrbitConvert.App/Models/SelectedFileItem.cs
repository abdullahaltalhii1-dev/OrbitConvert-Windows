namespace OrbitConvert.App.Models;

public sealed record SelectedFileItem(
    string FullPath,
    string FileName,
    string FileType,
    long SizeBytes,
    string DisplaySize);
