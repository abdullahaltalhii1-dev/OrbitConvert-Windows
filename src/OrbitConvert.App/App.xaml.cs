using System.Windows;

namespace OrbitConvert.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--pdf-worker")
        {
            Shutdown(Services.Pdf.PdfNativeWorker.Run(e.Args[1]));
            return;
        }
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }
}
