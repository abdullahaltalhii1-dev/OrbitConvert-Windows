using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.Models;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class ImagesView : UserControl
{
    public ImagesView() => InitializeComponent();

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || !model.ImageConversion.CanChooseDestination) { return; }
        var conversion = model.ImageConversion;
        var extension = ImageConversionOptions.Extension(conversion.OutputFormat);
        var dialog = new SaveFileDialog
        {
            Title = model.Localization["ImageChooseDestination"],
            Filter = conversion.OutputFormat == "JPEG" ? "JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg" : $"{conversion.OutputFormat} (*{extension})|*{extension}",
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(conversion.SelectedFile!.FileName) + "_converted" + extension,
            AddExtension = true,
            OverwritePrompt = false, // Existing files are always rejected by the conversion service.
            CheckPathExists = true,
            // The dialog's write-test file can survive (e.g. when sync or antivirus holds it) and then block the
            // conversion as an existing destination. The service verifies writability with its own temporary file.
            CreateTestFile = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { conversion.SetDestination(dialog.FileName); }
    }
}
