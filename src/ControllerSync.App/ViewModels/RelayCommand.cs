using System.Windows.Input;

namespace ControllerSync.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _can;
    private bool _busy;

    public RelayCommand(Func<Task> execute, Func<bool>? can = null)
        : this(_ => execute(), can == null ? null : _ => can())
    {
    }

    public RelayCommand(Action execute, Func<bool>? can = null)
        : this(_ =>
        {
            execute();
            return Task.CompletedTask;
        }, can == null ? null : _ => can())
    {
    }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_busy && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        _busy = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        finally
        {
            _busy = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
