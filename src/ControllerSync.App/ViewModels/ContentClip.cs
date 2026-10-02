using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControllerSync.App.ViewModels;

public readonly record struct SavedClip(string Host, int Port, string FileName);

public sealed class ContentClip : INotifyPropertyChanged
{
    private readonly Func<ContentClip, Task> _cancel;
    private string _status = "Ready";

    public ContentClip(string path, Func<ContentClip, Task> cancel)
    {
        Path = path;
        _cancel = cancel;
        CancelCommand = new RelayCommand(() => _cancel(this));
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public bool WasSent { get; set; }

    public bool LoadedHere { get; set; }

    public int Layer { get; set; }

    public int Clip { get; set; }

    public List<SavedClip> Saved { get; } = new();

    public CancellationTokenSource? SendCancel { get; set; }

    public RelayCommand CancelCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
