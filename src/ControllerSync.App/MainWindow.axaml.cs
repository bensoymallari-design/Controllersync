using Avalonia.Controls;
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
        viewModel.LogLines.CollectionChanged += (_, _) =>
        {
            if (viewModel.LogLines.Count > 0)
                LogList.ScrollIntoView(viewModel.LogLines[^1]);
        };
    }
}
