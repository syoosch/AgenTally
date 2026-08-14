using System.Windows.Threading;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private readonly object _refreshGate = new();
    private CancellationTokenSource? _currentRefresh;
    private string? _errorMessage;
    private long _generation;
    private bool _hasSuccessfulRefresh;
    private int _interactionFeedbackCount;
    private bool _isLoading;
    private bool _isRefreshOperationFeedbackVisible;

    protected PageViewModel(string title, Dispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Title = title;
        UiDispatcher = dispatcher;
    }

    public string Title { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsRefreshFeedbackVisible =>
        _isRefreshOperationFeedbackVisible || _interactionFeedbackCount > 0;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasErrorMessage));
            }
        }
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected Dispatcher UiDispatcher { get; }

    internal bool HasSuccessfulRefresh
    {
        get
        {
            UiDispatcher.VerifyAccess();
            return _hasSuccessfulRefresh;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshCoreAsync(cancellationToken, showFeedback: true);

    internal Task RefreshInBackgroundAsync(
        CancellationToken cancellationToken) =>
        RefreshCoreAsync(cancellationToken, showFeedback: false);

    protected abstract Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool showFeedback);

    internal void InvalidateSuccessfulRefresh()
    {
        UiDispatcher.VerifyAccess();
        _hasSuccessfulRefresh = false;
    }

    public void CancelRefresh()
    {
        CancellationTokenSource? refresh;
        long canceledGeneration;
        lock (_refreshGate)
        {
            canceledGeneration = ++_generation;
            refresh = _currentRefresh;
            _currentRefresh = null;
        }

        SafeCancel(refresh);
        RunOnDispatcher(() =>
        {
            lock (_refreshGate)
            {
                if (_generation == canceledGeneration && _currentRefresh is null)
                {
                    IsLoading = false;
                    SetRefreshOperationFeedbackVisible(false);
                }
            }
        });
    }

    protected RefreshSession BeginRefresh(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        long generation;
        lock (_refreshGate)
        {
            generation = ++_generation;
            previous = _currentRefresh;
            _currentRefresh = linked;
        }

        SafeCancel(previous);
        return new RefreshSession(generation, linked);
    }

    protected Task SetRefreshStartedAsync(
        RefreshSession session,
        bool showFeedback)
    {
        session.MarksSuccessfulRefresh = true;
        return ApplyIfCurrentAsync(session, () =>
        {
            _hasSuccessfulRefresh = false;
            ErrorMessage = null;
            IsLoading = true;
            SetRefreshOperationFeedbackVisible(showFeedback);
        });
    }

    protected async Task<IDisposable> BeginInteractionFeedbackAsync()
    {
        if (UiDispatcher.CheckAccess())
        {
            IncrementInteractionFeedback();
        }
        else
        {
            await UiDispatcher.InvokeAsync(IncrementInteractionFeedback).Task;
        }

        return new InteractionFeedbackScope(this);
    }

    protected Task SetRefreshFailureAsync(
        RefreshSession session,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ApplyIfCurrentAsync(
            session,
            () => ErrorMessage = UiErrorMessageClassifier.Classify(exception));
    }

    protected Task ApplyIfCurrentAsync(
        RefreshSession session,
        Action apply)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(apply);
        if (UiDispatcher.CheckAccess())
        {
            ApplyUnderGate(session, apply);
            return Task.CompletedTask;
        }

        return UiDispatcher.InvokeAsync(() => ApplyUnderGate(session, apply)).Task;
    }

    protected async Task<T> ReadOnDispatcherAsync<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (UiDispatcher.CheckAccess())
        {
            return read();
        }

        return await UiDispatcher.InvokeAsync(read).Task;
    }

    protected Task RunOnDispatcherAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (UiDispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return UiDispatcher.InvokeAsync(action).Task;
    }

    protected async Task EndRefreshAsync(RefreshSession session)
    {
        void EndUnderGate()
        {
            lock (_refreshGate)
            {
                if (IsCurrentIdentityUnderGate(session))
                {
                    if (session.MarksSuccessfulRefresh &&
                        !session.Cancellation.IsCancellationRequested &&
                        ErrorMessage is null)
                    {
                        _hasSuccessfulRefresh = true;
                    }

                    IsLoading = false;
                    SetRefreshOperationFeedbackVisible(false);
                    _currentRefresh = null;
                }
            }
        }

        if (UiDispatcher.CheckAccess())
        {
            EndUnderGate();
        }
        else
        {
            await UiDispatcher.InvokeAsync(EndUnderGate).Task;
        }

        session.Dispose();
    }

    private void ApplyUnderGate(RefreshSession session, Action apply)
    {
        lock (_refreshGate)
        {
            if (IsCurrentUnderGate(session))
            {
                apply();
            }
        }
    }

    private bool IsCurrentUnderGate(RefreshSession session) =>
        !session.Cancellation.IsCancellationRequested &&
        IsCurrentIdentityUnderGate(session);

    private bool IsCurrentIdentityUnderGate(RefreshSession session) =>
        session.Generation == _generation &&
        ReferenceEquals(session.Cancellation, _currentRefresh);

    private void RunOnDispatcher(Action action)
    {
        if (UiDispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            UiDispatcher.Invoke(action);
        }
    }

    private void IncrementInteractionFeedback()
    {
        UiDispatcher.VerifyAccess();
        bool wasVisible = IsRefreshFeedbackVisible;
        _interactionFeedbackCount++;
        if (wasVisible != IsRefreshFeedbackVisible)
        {
            OnPropertyChanged(nameof(IsRefreshFeedbackVisible));
        }
    }

    private void DecrementInteractionFeedback()
    {
        RunOnDispatcher(() =>
        {
            bool wasVisible = IsRefreshFeedbackVisible;
            if (_interactionFeedbackCount > 0)
            {
                _interactionFeedbackCount--;
            }

            if (wasVisible != IsRefreshFeedbackVisible)
            {
                OnPropertyChanged(nameof(IsRefreshFeedbackVisible));
            }
        });
    }

    private void SetRefreshOperationFeedbackVisible(bool value)
    {
        UiDispatcher.VerifyAccess();
        bool wasVisible = IsRefreshFeedbackVisible;
        _isRefreshOperationFeedbackVisible = value;
        if (wasVisible != IsRefreshFeedbackVisible)
        {
            OnPropertyChanged(nameof(IsRefreshFeedbackVisible));
        }
    }

    private static void SafeCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A superseded refresh may finish and dispose between selection and cancel.
        }
    }

    protected sealed class RefreshSession : IDisposable
    {
        private int _disposed;

        internal RefreshSession(
            long generation,
            CancellationTokenSource cancellation)
        {
            Generation = generation;
            Cancellation = cancellation;
        }

        internal long Generation { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal bool MarksSuccessfulRefresh { get; set; }

        public CancellationToken Token => Cancellation.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class InteractionFeedbackScope(PageViewModel owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.DecrementInteractionFeedback();
            }
        }
    }
}
