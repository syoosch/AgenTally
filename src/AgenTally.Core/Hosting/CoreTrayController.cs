using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Hosting;

internal enum CoreTrayOpenResult
{
    Activated,
    Launched,
    LaunchInProgress,
    Exiting,
    Failed
}

internal enum CoreTrayExitResult
{
    Requested,
    AlreadyRequested,
    Failed
}

internal interface ITrackedUiProcess : IDisposable
{
    event EventHandler? Exited;

    bool HasExited { get; }
}

internal sealed class CoreTrayController : IDisposable
{
    private readonly object _gate = new();
    private readonly AgenTallyRuntimeProfile _profile;
    private readonly Func<bool> _tryActivate;
    private readonly Func<string, ITrackedUiProcess> _startUi;
    private readonly Func<ApplicationShutdownRequestResult> _requestShutdown;
    private ITrackedUiProcess? _launchedUi;
    private int _exitRequested;
    private int _disposed;

    public CoreTrayController(
        AgenTallyRuntimeProfile profile,
        Func<bool> tryActivate,
        Func<string, ITrackedUiProcess> startUi,
        Func<ApplicationShutdownRequestResult> requestShutdown)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _tryActivate = tryActivate ??
            throw new ArgumentNullException(nameof(tryActivate));
        _startUi = startUi ??
            throw new ArgumentNullException(nameof(startUi));
        _requestShutdown = requestShutdown ??
            throw new ArgumentNullException(nameof(requestShutdown));
    }

    public CoreTrayOpenResult OpenOrActivate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_exitRequested != 0)
            {
                return CoreTrayOpenResult.Exiting;
            }

            if (_tryActivate())
            {
                return CoreTrayOpenResult.Activated;
            }

            if (_launchedUi is not null)
            {
                if (!_launchedUi.HasExited)
                {
                    return CoreTrayOpenResult.LaunchInProgress;
                }

                ReleaseLaunchedUi();
            }

            try
            {
                string executablePath = Path.GetFullPath(
                    _profile.UiExecutablePath);
                if (!Path.IsPathFullyQualified(executablePath) ||
                    !File.Exists(executablePath))
                {
                    return CoreTrayOpenResult.Failed;
                }

                ITrackedUiProcess launched = _startUi(executablePath);
                launched.Exited += OnLaunchedUiExited;
                _launchedUi = launched;
                if (launched.HasExited)
                {
                    ReleaseLaunchedUi();
                    return CoreTrayOpenResult.Failed;
                }

                return CoreTrayOpenResult.Launched;
            }
            catch (Exception exception)
                when (exception is Win32Exception
                    or IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
            {
                ReleaseLaunchedUi();
                return CoreTrayOpenResult.Failed;
            }
        }
    }

    public CoreTrayExitResult RequestExit()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_exitRequested != 0)
            {
                return CoreTrayExitResult.AlreadyRequested;
            }

            ApplicationShutdownRequestResult request;
            try
            {
                request = _requestShutdown();
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException)
            {
                return CoreTrayExitResult.Failed;
            }

            if (!request.RequestAccepted)
            {
                return CoreTrayExitResult.Failed;
            }

            _exitRequested = 1;
            return CoreTrayExitResult.Requested;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _exitRequested = 1;
            ReleaseLaunchedUi();
        }
    }

    private void OnLaunchedUiExited(object? sender, EventArgs eventArgs)
    {
        lock (_gate)
        {
            if (ReferenceEquals(sender, _launchedUi))
            {
                ReleaseLaunchedUi();
            }
        }
    }

    private void ReleaseLaunchedUi()
    {
        if (_launchedUi is null)
        {
            return;
        }

        _launchedUi.Exited -= OnLaunchedUiExited;
        _launchedUi.Dispose();
        _launchedUi = null;
    }
}

internal sealed class SystemTrackedUiProcess : ITrackedUiProcess
{
    private readonly Process _process;

    private SystemTrackedUiProcess(Process process)
    {
        _process = process;
        _process.Exited += OnProcessExited;
    }

    public event EventHandler? Exited;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static ITrackedUiProcess Start(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ??
                throw new InvalidOperationException(
                    "The UI executable has no parent directory."),
            UseShellExecute = false
        };
        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Windows did not create the AgenTally UI process.");
        process.EnableRaisingEvents = true;
        return new SystemTrackedUiProcess(process);
    }

    public void Dispose()
    {
        _process.Exited -= OnProcessExited;
        _process.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs) =>
        Exited?.Invoke(this, EventArgs.Empty);
}
