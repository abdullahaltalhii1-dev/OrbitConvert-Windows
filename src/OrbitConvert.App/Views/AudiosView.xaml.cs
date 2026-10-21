using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.Services.Audio;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class AudiosView : UserControl
{
    public AudiosView() => InitializeComponent();
    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || !model.AudioConversion.CanChooseDestination) { return; }
        var conversion = model.AudioConversion;
        var extension = new AudioConversionOptions(conversion.Format).Extension;
        var dialog = new SaveFileDialog
        {
            Title = model.Localization["AudioChooseDestination"],
            Filter = $"{conversion.Format} (*{extension})|*{extension}",
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(conversion.SelectedFile!.FileName) + "_audio" + extension,
            AddExtension = true,
            OverwritePrompt = false,
            CheckPathExists = true,
            CreateTestFile = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { conversion.SetDestination(dialog.FileName); }
    }
}
