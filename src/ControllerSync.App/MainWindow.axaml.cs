using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ControllerSync.App.ViewModels;

namespace ControllerSync.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachLog();
    }

    private void AttachLog()
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.CopyText = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        };
        viewModel.PickFile = PickContentFilesAsync;
        // The log sits inside the page scroller. Let the list move to the new line,
        // then stop that request so Start stays where the user left it.
        LogList.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, true);
        viewModel.LogLines.CollectionChanged += (_, _) =>
        {
            if (viewModel.LogLines.Count > 0)
                LogList.ScrollIntoView(viewModel.LogLines[^1]);
        };
    }

    private async Task<IReadOnlyList<string>?> PickContentFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose clips for Resolume",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video and pictures")
                {
                    Patterns = new[]
                    {
                        "*.mp4", "*.mov", "*.avi", "*.mkv", "*.webm", "*.wmv", "*.mpg", "*.mpeg", "*.m4v",
                        "*.gif", "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp"
                    }
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
            }
        });
        if (files.Count == 0)
            return null;
        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        return paths;
    }
}
