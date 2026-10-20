using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class ImagesView : UserControl
{
    public ImagesView() => InitializeComponent();

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || !model.ImageConversion.CanChooseDestination) { return; }
        var conversion = model.ImageConversion;
        var png = conversion.OutputFormat == "PNG";
        var dialog = new SaveFileDialog
        {
            Title = model.Localization["ImageChooseDestination"],
            Filter = png ? "PNG (*.png)|*.png" : "JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            DefaultExt = png ? ".png" : ".jpg",
            FileName = Path.GetFileNameWithoutExtension(conversion.SelectedFile!.FileName) + "_converted" + (png ? ".png" : ".jpg"),
            AddExtension = true,
            OverwritePrompt = false, // Existing files are always rejected by the conversion service.
            CheckPathExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { conversion.SetDestination(dialog.FileName); }
    }
}
