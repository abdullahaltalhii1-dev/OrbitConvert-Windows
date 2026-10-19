using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrbitConvert.App.Views;

public partial class ComingSoonView : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(
        nameof(Eyebrow), typeof(string), typeof(ComingSoonView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ComingSoonView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(ComingSoonView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(ComingSoonView), new PropertyMetadata(null));

    public static readonly DependencyProperty IconWidthProperty = DependencyProperty.Register(
        nameof(IconWidth), typeof(double), typeof(ComingSoonView), new PropertyMetadata(62d));

    public static readonly DependencyProperty IconHeightProperty = DependencyProperty.Register(
        nameof(IconHeight), typeof(double), typeof(ComingSoonView), new PropertyMetadata(62d));

    public static readonly DependencyProperty IconFillProperty = DependencyProperty.Register(
        nameof(IconFill), typeof(Brush), typeof(ComingSoonView), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty FormatsHeadingProperty = DependencyProperty.Register(
        nameof(FormatsHeading), typeof(string), typeof(ComingSoonView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FormatsProperty = DependencyProperty.Register(
        nameof(Formats),
        typeof(string),
        typeof(ComingSoonView),
        new PropertyMetadata(string.Empty, OnFormatsChanged));

    public static readonly DependencyProperty InformationalTextProperty = DependencyProperty.Register(
        nameof(InformationalText), typeof(string), typeof(ComingSoonView), new PropertyMetadata(string.Empty));

    public ComingSoonView()
    {
        InitializeComponent();
        UpdateFormatItems();
    }

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public double IconWidth
    {
        get => (double)GetValue(IconWidthProperty);
        set => SetValue(IconWidthProperty, value);
    }

    public double IconHeight
    {
        get => (double)GetValue(IconHeightProperty);
        set => SetValue(IconHeightProperty, value);
    }

    public Brush IconFill
    {
        get => (Brush)GetValue(IconFillProperty);
        set => SetValue(IconFillProperty, value);
    }

    public string FormatsHeading
    {
        get => (string)GetValue(FormatsHeadingProperty);
        set => SetValue(FormatsHeadingProperty, value);
    }

    public string Formats
    {
        get => (string)GetValue(FormatsProperty);
        set => SetValue(FormatsProperty, value);
    }

    public string InformationalText
    {
        get => (string)GetValue(InformationalTextProperty);
        set => SetValue(InformationalTextProperty, value);
    }

    private static void OnFormatsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        ((ComingSoonView)dependencyObject).UpdateFormatItems();
    }

    private void UpdateFormatItems()
    {
        if (FormatItems is null)
        {
            return;
        }

        FormatItems.ItemsSource = Formats.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
