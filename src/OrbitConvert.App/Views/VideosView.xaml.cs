using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class VideosView : UserControl
{
    public VideosView() => InitializeComponent();

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || !model.VideoConversion.CanChooseDestination) { return; }
        var conversion = model.VideoConversion;
        var dialog = new SaveFileDialog
        {
            Title = model.Localization["VideoChooseDestination"],
            Filter = "MP4 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            FileName = Path.GetFileNameWithoutExtension(conversion.SelectedFile!.FileName) + "_converted.mp4",
            AddExtension = true,
            OverwritePrompt = false, // The service always rejects existing destinations.
            CheckPathExists = true,
            CreateTestFile = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { conversion.SetDestination(dialog.FileName); }
    }
}
