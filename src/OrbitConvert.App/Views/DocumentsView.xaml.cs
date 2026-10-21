using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.Services.Documents;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView() => InitializeComponent();
    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || !model.DocumentConversion.CanChooseDestination) { return; }
        var conversion = model.DocumentConversion;
        var extension = conversion.Format.Extension();
        var dialog = new SaveFileDialog
        {
            Title = model.Localization["DocumentDestination"],
            Filter = $"{conversion.Format} (*{extension})|*{extension}",
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(conversion.SelectedFile!.FileName) + "_converted" + extension,
            AddExtension = true,
            OverwritePrompt = false,
            CheckPathExists = true,
            CreateTestFile = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { conversion.SetDestination(dialog.FileName); }
    }
}
