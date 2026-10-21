using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App;

public partial class MainWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private bool _closingAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += MainWindow_SourceInitialized;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ApplyVisualTheme(viewModel);
        }

        Closing += async (_, e) =>
        {
            if (_closingAfterCancellation) { e.Cancel = true; return; }
            if (DataContext is MainViewModel model && model.ImageConversion.IsBusy)
            {
                e.Cancel = true;
                MessageBox.Show(this, model.Localization["ImageWaitToClose"], model.AppTitle);
            }
            else if (DataContext is MainViewModel mediaModel && (mediaModel.VideoConversion.IsBusy || mediaModel.AudioConversion.IsBusy))
            {
                e.Cancel = true;
                var audio = mediaModel.AudioConversion.IsBusy;
                if (MessageBox.Show(this, mediaModel.Localization[audio ? "AudioCancelToClose" : "VideoCancelToClose"], mediaModel.AppTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
                _closingAfterCancellation = true;
                if (audio) { mediaModel.AudioConversion.RequestCancellation(); await mediaModel.AudioConversion.WaitForIdleAsync(); }
                else { mediaModel.VideoConversion.RequestCancellation(); await mediaModel.VideoConversion.WaitForIdleAsync(); }
                _closingAfterCancellation = false;
                Close();
            }
        };
        Closed += MainWindow_Closed;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel viewModel &&
            e.PropertyName is nameof(MainViewModel.SelectedTheme) or nameof(MainViewModel.SelectedAccent))
        {
            ApplyVisualTheme(viewModel);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SourceInitialized -= MainWindow_SourceInitialized;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            ApplyNativeTitleBarTheme(viewModel.SelectedTheme);
        }
    }

    private void ApplyVisualTheme(MainViewModel viewModel)
    {
        var themePrefix = viewModel.SelectedTheme == AppTheme.Dark ? "Dark" : "Light";

        SetBrush("WindowBackgroundBrush", $"{themePrefix}WindowBackgroundColor");
        SetBrush("HeaderBackgroundBrush", $"{themePrefix}HeaderBackgroundColor");
        SetBrush("SidebarBackgroundBrush", $"{themePrefix}SidebarBackgroundColor");
        SetBrush("SurfaceBrush", $"{themePrefix}SurfaceColor");
        SetBrush("SurfaceElevatedBrush", $"{themePrefix}SurfaceElevatedColor");
        SetBrush("SurfaceHoverBrush", $"{themePrefix}SurfaceHoverColor");
        SetBrush("BorderBrush", $"{themePrefix}BorderColor");
        SetBrush("PrimaryTextBrush", $"{themePrefix}PrimaryTextColor");
        SetBrush("SecondaryTextBrush", $"{themePrefix}SecondaryTextColor");
        SetBrush("MutedTextBrush", $"{themePrefix}MutedTextColor");
        SetBrush("InputBackgroundBrush", $"{themePrefix}InputBackgroundColor");

        var accentColorKey = viewModel.SelectedAccent switch
        {
            AccentChoice.Violet => "VioletAccentColor",
            AccentChoice.Teal => "TealAccentColor",
            _ => "BlueAccentColor",
        };

        var accentColor = (Color)FindResource(accentColorKey);
        Application.Current.Resources["AccentBrush"] = CreateBrush(accentColor);
        Application.Current.Resources["AccentSoftBrush"] = CreateBrush(Color.FromArgb(
            0x24,
            accentColor.R,
            accentColor.G,
            accentColor.B));
        Application.Current.Resources["AccentBorderBrush"] = CreateBrush(Color.FromArgb(
            0x80,
            accentColor.R,
            accentColor.G,
            accentColor.B));

        ApplyNativeTitleBarTheme(viewModel.SelectedTheme);
    }

    private void ApplyNativeTitleBarTheme(AppTheme theme)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = theme == AppTheme.Dark ? 1 : 0;

        try
        {
            var result = DwmSetWindowAttribute(
                windowHandle,
                DwmUseImmersiveDarkMode,
                ref useDarkMode,
                sizeof(int));

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    windowHandle,
                    DwmUseImmersiveDarkModeBefore20H1,
                    ref useDarkMode,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Unsupported Windows environments retain their default native title bar.
        }
        catch (EntryPointNotFoundException)
        {
            // Unsupported Windows environments retain their default native title bar.
        }
    }

    private void SetBrush(string brushKey, string colorKey)
    {
        Application.Current.Resources[brushKey] = CreateBrush((Color)FindResource(colorKey));
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
