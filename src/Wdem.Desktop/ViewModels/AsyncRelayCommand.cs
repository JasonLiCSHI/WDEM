using System.Windows.Input;

namespace Wdem.Desktop.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
  private readonly Func<object?, Task> _execute;
  private readonly Predicate<object?>? _canExecute;
  private readonly Action<Exception> _onError;
  private bool _isExecuting;

  public AsyncRelayCommand(
      Func<object?, Task> execute,
      Predicate<object?>? canExecute = null,
      Action<Exception>? onError = null)
  {
    ArgumentNullException.ThrowIfNull(execute);
    _execute = execute;
    _canExecute = canExecute;
    _onError = onError ?? (_ => { });
  }

  public event EventHandler? CanExecuteChanged;

  public bool CanExecute(object? parameter) =>
      !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

  public async void Execute(object? parameter) => await ExecuteAsync(parameter);

  public async Task ExecuteAsync(object? parameter)
  {
    if (!CanExecute(parameter))
    {
      return;
    }

    _isExecuting = true;
    RaiseCanExecuteChanged();
    try
    {
      await _execute(parameter);
    }
    catch (Exception exception)
    {
      _onError(exception);
    }
    finally
    {
      _isExecuting = false;
      RaiseCanExecuteChanged();
    }
  }

  public void RaiseCanExecuteChanged() =>
      CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
