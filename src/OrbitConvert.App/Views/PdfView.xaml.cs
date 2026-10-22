using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class PdfView : UserControl
{
    public PdfView() => InitializeComponent();
    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main || !main.Pdf.CanChooseDestination) { return; }
        var model = main.Pdf;
        var folder = model.DirectoryOutput;
        var extension = model.OutputExtension;
        var dialog = new SaveFileDialog
        {
            Title = model.DestinationLabel,
            FileName = folder ? "OrbitConvert-PDF-results" : "OrbitConvert-result" + extension,
            Filter = folder ? main.Localization["PdfFolderFilter"] + "|*.*" : main.Localization["PdfOutputFilter"] + $" (*{extension})|*{extension}",
            AddExtension = !folder,
            DefaultExt = folder ? "" : extension,
            CheckPathExists = true,
            CheckFileExists = false,
            CreateTestFile = false,
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { model.SetDestination(dialog.FileName); }
    }
}
