using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App.Views;

public partial class WorkspaceView : UserControl
{
    public static readonly DependencyProperty IsDragActiveProperty = DependencyProperty.Register(
        nameof(IsDragActive),
        typeof(bool),
        typeof(WorkspaceView),
        new PropertyMetadata(false));

    public WorkspaceView()
    {
        InitializeComponent();
    }

    public bool IsDragActive
    {
        get => (bool)GetValue(IsDragActiveProperty);
        set => SetValue(IsDragActiveProperty, value);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private FileIntakeViewModel? FileIntake => ViewModel?.FileIntake;

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = viewModel.Localization["FileDialogTitle"],
            Filter = viewModel.Localization["FileDialogFilter"],
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            viewModel.FileIntake.AddFilesCommand.Execute(dialog.FileNames);
        }
    }

    private void DropZone_PreviewDragEnter(object sender, DragEventArgs e)
    {
        UpdateDragState(e, reportRejectedFolders: true);
    }

    private void DropZone_PreviewDragOver(object sender, DragEventArgs e)
    {
        UpdateDragState(e, reportRejectedFolders: false);
    }

    private void DropZone_PreviewDragLeave(object sender, DragEventArgs e)
    {
        IsDragActive = false;
        e.Handled = true;
    }

    private void DropZone_PreviewDrop(object sender, DragEventArgs e)
    {
        IsDragActive = false;

        if (TryGetDroppedPaths(e, out var paths))
        {
            FileIntake?.AddFilesCommand.Execute(paths);
        }

        e.Handled = true;
    }

    private void UpdateDragState(DragEventArgs e, bool reportRejectedFolders)
    {
        if (!TryGetDroppedPaths(e, out var paths))
        {
            IsDragActive = false;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var containsFile = paths.Any(File.Exists);
        IsDragActive = containsFile;
        e.Effects = containsFile ? DragDropEffects.Copy : DragDropEffects.None;

        if (reportRejectedFolders && !containsFile && paths.Any(Directory.Exists))
        {
            FileIntake?.ReportFolderDropRejected();
        }

        e.Handled = true;
    }

    private static bool TryGetDroppedPaths(DragEventArgs e, out string[] paths)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            paths = droppedPaths;
            return true;
        }

        paths = [];
        return false;
    }
}
