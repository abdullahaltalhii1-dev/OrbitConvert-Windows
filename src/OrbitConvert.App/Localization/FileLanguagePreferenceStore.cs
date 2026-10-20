using System.IO;
using System.Security;

namespace OrbitConvert.App.Localization;

public sealed class FileLanguagePreferenceStore : ILanguagePreferenceStore
{
    private readonly string _filePath;

    public FileLanguagePreferenceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitConvert",
            "language.txt"))
    {
    }

    public FileLanguagePreferenceStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string? LoadLanguageCode()
    {
        try
        {
            return File.Exists(_filePath) ? File.ReadAllText(_filePath).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public void SaveLanguageCode(string languageCode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, languageCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A read-only or unavailable profile must not prevent the application from running.
        }
    }
}
