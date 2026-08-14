using System.Drawing;
using System.Reflection;
using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Hosting;

internal interface ICoreTraySession : IDisposable
{
    Task Completion { get; }
}

internal static class CoreTrayText
{
    public const string Open = "打开 AgenTally";
    public const string Exit = "退出 AgenTally";
}

internal interface ICoreTrayFactory
{
    ICoreTraySession Start(AgenTallyRuntimeProfile profile);
}

internal sealed class SystemCoreTrayFactory : ICoreTrayFactory
{
    public static SystemCoreTrayFactory Instance { get; } = new();

    private SystemCoreTrayFactory()
    {
    }

    public ICoreTraySession Start(AgenTallyRuntimeProfile profile) =>
        new CoreTrayHost(profile);
}

internal sealed class CoreTrayHost : ICoreTraySession
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ManualResetEventSlim _started = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private volatile CoreTrayApplicationContext? _context;
    private Exception? _startupFailure;
    private int _stopping;
    private int _disposed;

    public CoreTrayHost(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _thread = new Thread(() => RunMessageLoop(profile))
        {
            IsBackground = true,
            Name = "AgenTally.Core.Tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(StartupTimeout))
        {
            Interlocked.Exchange(ref _stopping, 1);
            throw new InvalidOperationException(
                "AgenTally tray did not initialize in time.");
        }

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException(
                "AgenTally tray could not initialize.",
                _startupFailure);
        }
    }

    public Task Completion => _completion.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _stopping, 1);
        _context?.Exit();
        bool stopped = _thread.Join(ShutdownTimeout);
        _started.Dispose();
        if (!stopped)
        {
            throw new InvalidOperationException(
                "AgenTally tray thread did not stop cleanly.");
        }
    }

    private void RunMessageLoop(AgenTallyRuntimeProfile profile)
    {
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(
                System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                System.Windows.Forms.UnhandledExceptionMode.ThrowException);
            var context = new CoreTrayApplicationContext(profile);
            _context = context;
            _started.Set();
            if (Volatile.Read(ref _stopping) == 0)
            {
                System.Windows.Forms.Application.Run(context);
            }

            if (Volatile.Read(ref _stopping) == 0)
            {
                _completion.TrySetException(new InvalidOperationException(
                    "AgenTally tray message loop stopped unexpectedly."));
            }
            else
            {
                _completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _started.Set();
            _completion.TrySetException(exception);
        }
        finally
        {
            _context?.Dispose();
            _context = null;
        }
    }

    private sealed class CoreTrayApplicationContext :
        System.Windows.Forms.ApplicationContext
    {
        private const string OpenFailureMessage =
            "无法打开 AgenTally，请重新发布或安装后重试。";
        private const string ExitFailureMessage =
            "无法发送完全退出请求；AgenTally 将继续运行，请重试。";

        private readonly System.Windows.Forms.Control _dispatcher = new();
        private readonly Icon _icon;
        private readonly System.Windows.Forms.ContextMenuStrip _menu;
        private readonly System.Windows.Forms.ToolStripMenuItem _openItem;
        private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
        private readonly CoreTrayController _controller;
        private int _exiting;
        private int _disposed;

        public CoreTrayApplicationContext(AgenTallyRuntimeProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            _dispatcher.CreateControl();
            _icon = LoadIcon();
            _openItem = new System.Windows.Forms.ToolStripMenuItem(
                CoreTrayText.Open,
                image: null,
                (_, _) => OpenOrActivateUi());
            _exitItem = new System.Windows.Forms.ToolStripMenuItem(
                CoreTrayText.Exit,
                image: null,
                (_, _) => RequestFullExit());
            _menu = new System.Windows.Forms.ContextMenuStrip();
            _menu.Items.Add(_openItem);
            _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _menu.Items.Add(_exitItem);
            _controller = new CoreTrayController(
                profile,
                () => UiActivationSignal.TryRequest(profile),
                SystemTrackedUiProcess.Start,
                () => ApplicationShutdownSignal.Request(profile));
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = _icon,
                Text = profile.DisplayName,
                Visible = true
            };
            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        }

        public void Exit()
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0)
            {
                return;
            }

            if (_dispatcher.IsHandleCreated && !_dispatcher.IsDisposed)
            {
                _dispatcher.BeginInvoke(ExitThread);
            }
        }

        protected override void ExitThreadCore()
        {
            _notifyIcon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Exchange(ref _exiting, 1);
                _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _controller.Dispose();
                _menu.Dispose();
                _icon.Dispose();
                _dispatcher.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnNotifyIconDoubleClick(object? sender, EventArgs eventArgs) =>
            OpenOrActivateUi();

        private void OpenOrActivateUi()
        {
            CoreTrayOpenResult result = _controller.OpenOrActivate();
            if (result == CoreTrayOpenResult.Failed)
            {
                ShowError(OpenFailureMessage);
            }
        }

        private void RequestFullExit()
        {
            CoreTrayExitResult result = _controller.RequestExit();
            if (result == CoreTrayExitResult.Requested)
            {
                _openItem.Enabled = false;
                _exitItem.Enabled = false;
            }
            else if (result == CoreTrayExitResult.Failed)
            {
                ShowError(ExitFailureMessage);
            }
        }

        private void ShowError(string message)
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                _notifyIcon.Text,
                message,
                System.Windows.Forms.ToolTipIcon.Error);
        }

        private static Icon LoadIcon()
        {
            const string ResourceName =
                "AgenTally.Core.Resources.AgenTally.ico";
            using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName) ??
                throw new InvalidOperationException(
                    "The embedded AgenTally icon is missing.");
            using var source = new Icon(stream);
            return (Icon)source.Clone();
        }
    }
}
