using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class ArchivesView : UserControl
{
    public ArchivesView() => InitializeComponent();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main || !main.CanUseArchives || !main.Archives.IsIdle) { return; }
        var dialog = new OpenFileDialog { Title = main.Localization["ArchiveBrowse"], Multiselect = main.Archives.IsCreate, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) { return; }
        main.FileIntake.AddFiles(dialog.FileNames);
        foreach (var path in dialog.FileNames)
        {
            main.Archives.SelectedFile = main.FileIntake.SelectedFiles.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (main.Archives.IsCreate) { main.Archives.AddCommand.Execute(null); }
        }
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main || !main.CanUseArchives || !main.Archives.IsIdle || !main.Archives.IsCreate) { return; }
        var dialog = new OpenFolderDialog { Title = main.Localization["ArchiveChooseFolder"], Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { main.Archives.SetFolder(dialog.FolderName); }
    }
    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main || !main.CanUseArchives || !main.Archives.CanChooseDestination) { return; }
        var model = main.Archives;
        var dialog = new SaveFileDialog
        {
            Title = model.DestinationLabel,
            FileName = model.DirectoryOutput ? "OrbitConvert-extracted" : "OrbitConvert-archive" + model.OutputExtension,
            Filter = model.DirectoryOutput ? main.Localization["ArchiveFolderFilter"] + "|*.*" : main.Localization["ArchiveOutputFilter"] + $" (*{model.OutputExtension})|*{model.OutputExtension}",
            AddExtension = !model.DirectoryOutput,
            DefaultExt = model.DirectoryOutput ? "" : model.OutputExtension,
            CheckPathExists = true,
            CheckFileExists = false,
            CreateTestFile = false,
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { model.SetDestination(dialog.FileName); }
    }
}
