using Avalonia.Controls;
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
        viewModel.PickFile = PickContentFileAsync;
        viewModel.LogLines.CollectionChanged += (_, _) =>
        {
            if (viewModel.LogLines.Count > 0)
                LogList.ScrollIntoView(viewModel.LogLines[^1]);
        };
    }

    private async Task<string?> PickContentFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a clip for Resolume",
            AllowMultiple = false,
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
        var path = files[0].TryGetLocalPath();
        return string.IsNullOrWhiteSpace(path) ? "" : path;
    }
}
