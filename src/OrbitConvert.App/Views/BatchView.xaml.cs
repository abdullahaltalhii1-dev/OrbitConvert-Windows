using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class BatchView : UserControl
{
    public BatchView() => InitializeComponent();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || model.Batch.IsBusy) { return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = model.Localization["FileDialogTitle"], Filter = model.Localization["FileDialogFilter"] };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { model.FileIntake.AddFiles(dialog.FileNames); }
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || model.Batch.IsBusy) { return; }
        var dialog = new OpenFolderDialog { Title = model.Localization["BatchChooseFolder"], Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { model.Batch.SetDestinationFolder(dialog.FolderName); }
    }
}
