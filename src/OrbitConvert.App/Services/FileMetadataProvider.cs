using System.IO;
using System.Security;
using OrbitConvert.App.Models;

namespace OrbitConvert.App.Services;

public sealed class FileMetadataProvider : IFileMetadataProvider
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public FileMetadataResult GetMetadata(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileMetadataResult.Failure(FileMetadataError.InvalidPath);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var attributes = File.GetAttributes(fullPath);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return FileMetadataResult.Failure(FileMetadataError.Directory);
            }

            var fileInfo = new FileInfo(fullPath);
            var fileType = string.IsNullOrEmpty(fileInfo.Extension)
                ? "FILE"
                : fileInfo.Extension.TrimStart('.').ToUpperInvariant();

            return FileMetadataResult.Success(new SelectedFileItem(
                fileInfo.FullName,
                fileInfo.Name,
                fileType,
                fileInfo.Length,
                FormatSize(fileInfo.Length)));
        }
        catch (FileNotFoundException)
        {
            return FileMetadataResult.Failure(FileMetadataError.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return FileMetadataResult.Failure(FileMetadataError.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return FileMetadataResult.Failure(FileMetadataError.Inaccessible);
        }
        catch (SecurityException)
        {
            return FileMetadataResult.Failure(FileMetadataError.Inaccessible);
        }
        catch (ArgumentException)
        {
            return FileMetadataResult.Failure(FileMetadataError.InvalidPath);
        }
        catch (NotSupportedException)
        {
            return FileMetadataResult.Failure(FileMetadataError.InvalidPath);
        }
        catch (PathTooLongException)
        {
            return FileMetadataResult.Failure(FileMetadataError.InvalidPath);
        }
        catch (IOException)
        {
            return FileMetadataResult.Failure(FileMetadataError.Inaccessible);
        }
    }

    private static string FormatSize(long bytes)
    {
        var size = (double)bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {SizeUnits[unitIndex]}"
            : $"{size:0.#} {SizeUnits[unitIndex]}";
    }
}
