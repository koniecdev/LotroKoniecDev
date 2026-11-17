using System.Windows.Input;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// A button's command: what a click does, and whether the button is enabled. The view model calls
/// <see cref="RaiseCanExecuteChanged"/> when the answer may have changed.
/// </summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter)
    {
        if (_canExecute())
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public DelegateCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }
}
