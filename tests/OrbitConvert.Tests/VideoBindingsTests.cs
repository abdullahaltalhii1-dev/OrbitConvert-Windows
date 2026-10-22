using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrbitConvert.App;
using OrbitConvert.App.Localization;
using OrbitConvert.App.Services;
using OrbitConvert.App.ViewModels;

namespace OrbitConvert.Tests;

public sealed class VideoBindingsTests
{
    [Fact]
    public void ShellAndVideoBindings_ResolveInBothLanguagesAtMinimumAndDefaultSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var listener = new BindingErrors();
            var trace = PresentationTraceSources.DataBindingSource;
            var previousLevel = trace.Switch.Level;
            trace.Switch.Level = SourceLevels.Error;
            trace.Listeners.Add(listener);
            try
            {
                var app = new OrbitConvert.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var window = new MainWindow();
                var model = new MainViewModel(new FileMetadataProvider(), new LocalizationService(new TestLanguagePreferenceStore()));
                window.DataContext = model;
                var root = (FrameworkElement)window.Content;
                foreach (var language in new[] { AppLanguage.English, AppLanguage.Arabic })
                {
                    model.Localization.ChangeLanguage(language);
                    foreach (var section in Enum.GetValues<AppSection>())
                    {
                        model.NavigateCommand.Execute(section);
                        foreach (var pdfMode in section == AppSection.Pdf ? Enumerable.Range(0, 5) : [model.Pdf.ModeIndex])
                        {
                            model.Pdf.ModeIndex = pdfMode;
                            foreach (var size in new[] { new Size(900, 620), new Size(1120, 720), new Size(1920, 1080) })
                            {
                                root.Measure(size);
                                root.Arrange(new Rect(size));
                                root.UpdateLayout();
                                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                                Assert.Equal(pdfMode, model.Pdf.ModeIndex);
                            }
                        }
                    }
                }
                model.NavigateCommand.Execute(AppSection.Pdf);
                model.Pdf.ModeIndex = 3;
                root.Measure(new Size(1120, 720)); root.Arrange(new Rect(0, 0, 1120, 720)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var operation = Descendants(root).OfType<ComboBox>().Single(x =>
                    BindingOperations.GetBindingExpression(x, ComboBox.SelectedIndexProperty)?.ParentBinding.Path.Path == "Pdf.ModeIndex");
                Assert.Equal(3, operation.SelectedIndex);
                model.Localization.ChangeLanguage(AppLanguage.English);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(3, operation.SelectedIndex);
                Assert.Equal(3, model.Pdf.ModeIndex);
                model.NavigateCommand.Execute(AppSection.Settings);
                root.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                model.Localization.ChangeLanguage(AppLanguage.Arabic);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                model.NavigateCommand.Execute(AppSection.Pdf);
                root.Measure(new Size(1120, 720)); root.Arrange(new Rect(0, 0, 1120, 720)); root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                operation = Descendants(root).OfType<ComboBox>().Single(x =>
                    BindingOperations.GetBindingExpression(x, ComboBox.SelectedIndexProperty)?.ParentBinding.Path.Path == "Pdf.ModeIndex");
                Assert.Equal(3, operation.SelectedIndex);
                Assert.NotNull(operation.SelectedItem);
                window.Close();
                app.Shutdown();
                Assert.Empty(listener.Errors);
            }
            catch (Exception error) { failure = error; }
            finally { trace.Listeners.Remove(listener); trace.Switch.Level = previousLevel; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF binding/layout check timed out.");
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message is not null) { Errors.Add(message); } }
        public override void WriteLine(string? message) => Write(message);
    }
}
