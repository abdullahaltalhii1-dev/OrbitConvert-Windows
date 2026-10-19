using System.Windows.Input;

namespace OrbitConvert.App.ViewModels;

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
