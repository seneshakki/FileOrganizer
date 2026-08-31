using System.Diagnostics;
using System.Windows.Input;

namespace FileOrganizer.App.ViewModels;

/// <summary>A single-flight asynchronous command whose failures have an explicit UI boundary.</summary>
public sealed class AsyncDelegateCommand : ViewModelBase, ICommand
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool> _canExecute;
    private readonly Action<Exception> _failureHandler;
    private bool _isRunning;

    public AsyncDelegateCommand(
        Func<CancellationToken, Task> execute,
        Action<Exception> failureHandler,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(failureHandler);

        _execute = execute;
        _failureHandler = failureHandler;
        _canExecute = canExecute ?? (static () => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public bool CanExecute(object? parameter)
    {
        lock (_gate)
        {
            return !_isRunning && _canExecute();
        }
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isRunning || !_canExecute())
            {
                return;
            }

            _isRunning = true;
        }

        try
        {
            NotifyRunningStateChanged();
            await _execute(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
            }

            NotifyRunningStateChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    async void ICommand.Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void NotifyRunningStateChanged()
    {
        RaisePropertyChanged(nameof(IsRunning));
        RaiseCanExecuteChanged();
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            _failureHandler(exception);
        }
        catch (Exception handlerFailure)
        {
            Trace.TraceError(
                "FileOrganizer asynchronous command failure reporting failed: {0}",
                new AggregateException(exception, handlerFailure));
        }
    }
}
