using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ControllerSync.App.ViewModels;

namespace ControllerSync.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel()
            };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                if (window.DataContext is IDisposable disposable)
                    disposable.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
