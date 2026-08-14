using System.Windows.Input;

namespace AgenTally.UI.Infrastructure;

public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly bool _allowsConcurrentExecutions;
    private Exception? _lastException;
    private Task? _executionTask;
    private int _executionCount;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        bool allowsConcurrentExecutions = false)
        : this(
            _ => execute(),
            canExecute is null ? null : _ => canExecute(),
            allowsConcurrentExecutions)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        bool allowsConcurrentExecutions = false)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _allowsConcurrentExecutions = allowsConcurrentExecutions;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => Volatile.Read(ref _executionCount) != 0;

    public Exception? LastException
    {
        get => _lastException;
        private set => SetProperty(ref _lastException, value);
    }

    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => SetProperty(ref _executionTask, value);
    }

    public bool CanExecute(object? parameter) =>
        (_allowsConcurrentExecutions || !IsExecuting) &&
        (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter)
    {
        Task execution = ExecuteAsync(parameter);
        ExecutionTask = execution;
        _ = ObserveCommandExecutionAsync(execution);
    }

    public Task ExecuteAsync(object? parameter = null)
    {
        if ((_canExecute?.Invoke(parameter) ?? true) is false)
        {
            return Task.CompletedTask;
        }

        int executionCount;
        if (_allowsConcurrentExecutions)
        {
            executionCount = Interlocked.Increment(ref _executionCount);
        }
        else
        {
            if (Interlocked.CompareExchange(ref _executionCount, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            executionCount = 1;
        }

        if (executionCount == 1)
        {
            OnPropertyChanged(nameof(IsExecuting));
            RaiseCanExecuteChanged();
        }

        Task execution = ExecuteCoreAsync(parameter);
        ExecutionTask = execution;
        return execution;
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteCoreAsync(object? parameter)
    {
        LastException = null;
        try
        {
            await _execute(parameter);
        }
        catch (Exception exception)
        {
            LastException = exception;
            throw;
        }
        finally
        {
            int remaining = _allowsConcurrentExecutions
                ? Interlocked.Decrement(ref _executionCount)
                : ResetExecutionCount();
            if (remaining == 0)
            {
                OnPropertyChanged(nameof(IsExecuting));
                RaiseCanExecuteChanged();
            }
        }
    }

    private int ResetExecutionCount()
    {
        Interlocked.Exchange(ref _executionCount, 0);
        return 0;
    }

    private static async Task ObserveCommandExecutionAsync(Task execution)
    {
        try
        {
            await execution;
        }
        catch
        {
            // ICommand cannot return a Task. LastException keeps the failure observable.
        }
    }
}
