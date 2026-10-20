using OrbitConvert.App.Localization;
using OrbitConvert.App.Services;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class FileIntakeViewModelTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "OrbitConvert.Tests",
        Guid.NewGuid().ToString("N"));

    public FileIntakeViewModelTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void AddFilesCommand_AddsSingleFileWithMetadata()
    {
        var filePath = CreateFile("sample.txt", [1, 2, 3]);
        var viewModel = CreateViewModel();

        viewModel.AddFilesCommand.Execute(new[] { filePath });

        var file = Assert.Single(viewModel.SelectedFiles);
        Assert.Equal("sample.txt", file.FileName);
        Assert.Equal("TXT", file.FileType);
        Assert.Equal(3, file.SizeBytes);
        Assert.Equal("3 B", file.DisplaySize);
        Assert.Equal("Added 1 file.", viewModel.StatusMessage);
    }

    [Fact]
    public void AddFiles_AddsMultipleFilesAndUpdatesCount()
    {
        var firstPath = CreateFile("first.txt", [1]);
        var secondPath = CreateFile("second.bin", [1, 2]);
        var viewModel = CreateViewModel();

        viewModel.AddFiles([firstPath, secondPath]);

        Assert.Equal(2, viewModel.SelectedFileCount);
        Assert.Equal("2 files selected", viewModel.SelectedFileCountText);
        Assert.True(viewModel.HasSelectedFiles);
    }

    [Fact]
    public void AddFiles_PreventsDuplicateWindowsPaths()
    {
        var filePath = CreateFile("Duplicate.txt", [1]);
        var viewModel = CreateViewModel();

        viewModel.AddFiles([filePath, filePath.ToUpperInvariant()]);

        Assert.Single(viewModel.SelectedFiles);
        Assert.Contains("duplicate was already selected", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveFileCommand_RemovesSelectedItem()
    {
        var filePath = CreateFile("remove-me.txt", [1]);
        var viewModel = CreateViewModel();
        viewModel.AddFiles([filePath]);

        viewModel.RemoveFileCommand.Execute(viewModel.SelectedFiles[0]);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal(0, viewModel.SelectedFileCount);
        Assert.False(viewModel.HasSelectedFiles);
    }

    [Fact]
    public void ClearFilesCommand_ClearsEntireSelection()
    {
        var firstPath = CreateFile("first.txt", [1]);
        var secondPath = CreateFile("second.txt", [2]);
        var viewModel = CreateViewModel();
        viewModel.AddFiles([firstPath, secondPath]);

        viewModel.ClearFilesCommand.Execute(null);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal("No files selected", viewModel.SelectedFileCountText);
        Assert.Equal("Selection cleared.", viewModel.StatusMessage);
    }

    [Fact]
    public void AddFiles_WithEmptySelection_LeavesStateUnchanged()
    {
        var viewModel = CreateViewModel();

        viewModel.AddFiles([]);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal(string.Empty, viewModel.StatusMessage);
        Assert.Equal("No files selected", viewModel.SelectedFileCountText);
    }

    [Fact]
    public void AddFiles_PreservesArabicUnicodeAndSpacesInFilename()
    {
        const string fileName = "تقرير تجريبي 你好.txt";
        var filePath = CreateFile(fileName, []);
        var viewModel = CreateViewModel();

        viewModel.AddFiles([filePath]);

        var file = Assert.Single(viewModel.SelectedFiles);
        Assert.Equal(fileName, file.FileName);
        Assert.Equal("0 B", file.DisplaySize);
        Assert.Equal(Path.GetFullPath(filePath), file.FullPath);
    }

    [Fact]
    public void AddFiles_MissingFileShowsFriendlyMessage()
    {
        var missingPath = Path.Combine(_temporaryDirectory, "missing.txt");
        var viewModel = CreateViewModel();

        viewModel.AddFiles([missingPath]);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal("1 file could not be found.", viewModel.StatusMessage);
    }

    [Fact]
    public void AddFiles_InaccessibleFileShowsFriendlyMessage()
    {
        var provider = new StubMetadataProvider(_ =>
            FileMetadataResult.Failure(FileMetadataError.Inaccessible));
        var viewModel = new FileIntakeViewModel(provider, CreateLocalization());

        viewModel.AddFiles([@"C:\protected\private.txt"]);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal("1 file could not be accessed.", viewModel.StatusMessage);
    }

    [Fact]
    public void AddFiles_RejectsDirectoryWithoutScanningIt()
    {
        var folderPath = Path.Combine(_temporaryDirectory, "folder");
        Directory.CreateDirectory(folderPath);
        CreateFile(Path.Combine("folder", "nested.txt"), [1]);
        var viewModel = CreateViewModel();

        viewModel.AddFiles([folderPath]);

        Assert.Empty(viewModel.SelectedFiles);
        Assert.Equal("1 folder was skipped; folders aren’t supported yet.", viewModel.StatusMessage);
    }

    [Fact]
    public void LanguageChange_LocalizesExistingFileCountAndStatus()
    {
        var firstPath = CreateFile("الأول.txt", [1]);
        var secondPath = CreateFile("الثاني.txt", [2]);
        var localization = CreateLocalization();
        var viewModel = new FileIntakeViewModel(new FileMetadataProvider(), localization);
        viewModel.AddFiles([firstPath, secondPath]);

        localization.ChangeLanguage(AppLanguage.Arabic);

        Assert.Equal("تم تحديد ملفين", viewModel.SelectedFileCountText);
        Assert.Equal("تمت إضافة ملفين.", viewModel.StatusMessage);
    }

    [Fact]
    public void MissingAndDirectoryErrors_AreLocalizedInArabic()
    {
        var missingPath = Path.Combine(_temporaryDirectory, "مفقود.txt");
        var folderPath = Path.Combine(_temporaryDirectory, "مجلد");
        Directory.CreateDirectory(folderPath);
        var localization = CreateLocalization();
        localization.ChangeLanguage(AppLanguage.Arabic);
        var viewModel = new FileIntakeViewModel(new FileMetadataProvider(), localization);

        viewModel.AddFiles([missingPath, folderPath]);

        Assert.Equal("تم تجاوز مجلد واحد؛ المجلدات غير مدعومة حاليًا. تعذّر العثور على ملف واحد.", viewModel.StatusMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private FileIntakeViewModel CreateViewModel()
    {
        return new FileIntakeViewModel(new FileMetadataProvider(), CreateLocalization());
    }

    private static LocalizationService CreateLocalization()
    {
        return new LocalizationService(new TestLanguagePreferenceStore());
    }

    private string CreateFile(string relativePath, byte[] contents)
    {
        var filePath = Path.Combine(_temporaryDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, contents);
        return filePath;
    }

    private sealed class StubMetadataProvider(Func<string, FileMetadataResult> getMetadata)
        : IFileMetadataProvider
    {
        public FileMetadataResult GetMetadata(string path) => getMetadata(path);
    }
}
