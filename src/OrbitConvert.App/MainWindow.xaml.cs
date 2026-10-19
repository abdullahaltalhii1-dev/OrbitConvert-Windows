using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ApplyVisualTheme(viewModel);
        }

        Closed += MainWindow_Closed;
    }

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
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
