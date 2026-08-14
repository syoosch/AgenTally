using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgenTally.UI.Runtime;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PageEnterDuration =
        TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan WindowRefreshDebounce =
        TimeSpan.FromMilliseconds(75);

    private readonly MainViewModel _viewModel;
    private readonly IUiPreferencesStore _preferencesStore;
    private readonly DispatcherTimer _windowRefreshTimer;
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    public MainWindow(MainViewModel viewModel)
        : this(viewModel, new UnavailableUiPreferencesStore())
    {
    }

    internal MainWindow(
        MainViewModel viewModel,
        IUiPreferencesStore preferencesStore)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(preferencesStore);
        InitializeComponent();
        _preferencesStore = preferencesStore;
        ApplyPersistedWindowSize();
        DataContext = viewModel;
        _viewModel = viewModel;
        _windowRefreshTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = WindowRefreshDebounce,
        };
        _windowRefreshTimer.Tick += OnWindowRefreshTimerTick;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnInitialLoaded;
        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;
        Closed += OnWindowClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        SourceInitialized -= OnSourceInitialized;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        nint windowHandle = new WindowInteropHelper(this).Handle;
        var preference = DwmWindowCornerPreference.Round;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowAttribute.WindowCornerPreference,
            ref preference,
            sizeof(int));
    }

    private void OnInitialLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnInitialLoaded;
        QueueLayoutRefresh();
        PlayPageEnterTransition();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        PersistWindowSize();
        Closed -= OnWindowClosed;
        SizeChanged -= OnWindowSizeChanged;
        StateChanged -= OnWindowStateChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _windowRefreshTimer.Stop();
        _windowRefreshTimer.Tick -= OnWindowRefreshTimerTick;
    }

    private void ApplyPersistedWindowSize()
    {
        UiWindowSize? stored = _preferencesStore.ReadWindowSize();
        if (stored is null)
        {
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        Width = ClampToAvailableWorkArea(
            stored.Width,
            MinWidth,
            workArea.Width);
        Height = ClampToAvailableWorkArea(
            stored.Height,
            MinHeight,
            workArea.Height);
    }

    private void PersistWindowSize()
    {
        Rect restoreBounds = RestoreBounds;
        double width = WindowState == WindowState.Normal
            ? ActualWidth
            : restoreBounds.Width;
        double height = WindowState == WindowState.Normal
            ? ActualHeight
            : restoreBounds.Height;
        if (!double.IsFinite(width) || width <= 0d)
        {
            width = Width;
        }

        if (!double.IsFinite(height) || height <= 0d)
        {
            height = Height;
        }

        _ = _preferencesStore.TryWriteWindowSize(
            new UiWindowSize(
                Math.Max(MinWidth, width),
                Math.Max(MinHeight, height)));
    }

    private static double ClampToAvailableWorkArea(
        double stored,
        double minimum,
        double available)
    {
        double result = Math.Max(minimum, stored);
        return double.IsFinite(available) && available >= minimum
            ? Math.Min(result, available)
            : result;
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState != WindowState.Minimized)
        {
            _stateBeforeMinimize = WindowState;
            QueueLayoutRefresh();
        }
    }

    private void OnWindowSizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
    {
        QueueLayoutRefresh();
    }

    private void QueueLayoutRefresh()
    {
        _windowRefreshTimer.Stop();
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return;
        }

        _windowRefreshTimer.Start();
    }

    private void OnWindowRefreshTimerTick(
        object? sender,
        EventArgs eventArgs)
    {
        _windowRefreshTimer.Stop();
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return;
        }

        MainContentSurface.InvalidateMeasure();
        PageContent.InvalidateMeasure();
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        PageContent.InvalidateVisual();

        // A synchronous UpdateLayout during the native maximize/restore
        // transaction can re-enter WPF while WindowChrome is still changing
        // the HWND surface. Let WPF schedule its normal layout pass, then
        // invalidate the complete native frame and every child in one request.
        nint windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != 0)
        {
            _ = RedrawWindow(
                windowHandle,
                0,
                0,
                RedrawWindowFlags.Invalidate |
                RedrawWindowFlags.Erase |
                RedrawWindowFlags.AllChildren |
                RedrawWindowFlags.Frame);
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            PlayPageEnterTransition();
        }
    }

    private void OnNavigationPreviewMouseDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Right)
        {
            return;
        }

        // WPF selects a ListBoxItem on right-click even though the nested
        // navigation Button does not execute. Consume that gesture before the
        // Selector can detach the sidebar selection from CurrentPage.
        eventArgs.Handled = true;
    }

    private void PlayPageEnterTransition()
    {
        // FillBehavior.Stop keeps base values (Opacity 1, offset 0) once the
        // storyboard completes, so no animation clock is kept alive.
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0d, 1d, PageEnterDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(fade, PageContent);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        var rise = new DoubleAnimation(8d, 0d, PageEnterDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        Storyboard.SetTarget(rise, PageContent);
        Storyboard.SetTargetProperty(
            rise,
            new PropertyPath("RenderTransform.(TranslateTransform.Y)"));
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(rise);
        storyboard.Begin();
    }

    private void OnCaptionMinimizeClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCaptionMaximizeRestoreClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCaptionCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    internal void ActivateFromExternalRequest()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _stateBeforeMinimize;
        }

        if (!IsVisible)
        {
            Show();
        }

        bool activated = Activate();
        nint windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == 0)
        {
            return;
        }

        if (!SetForegroundWindow(windowHandle) && !activated)
        {
            var flash = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = windowHandle,
                Flags = FlashWindowFlags.Tray,
                Count = 3,
                TimeoutMilliseconds = 0
            };
            _ = FlashWindowEx(ref flash);
        }
    }

    internal async Task RequestCodexRescanAsync()
    {
        if (!_viewModel.RebuildCodexCommand.CanExecute(null) ||
            MessageBox.Show(
                "将只读重新扫描全部已支持 Agent 的现有日志并安全更新本地统计。任何原始日志都不会被修改，现有统计只有在所有来源完整扫描成功后才会原子更新。是否继续？",
                "确认重新扫描",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.RebuildCodexCommand.ExecuteAsync();
    }

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33,
    }

    private enum DwmWindowCornerPreference
    {
        Round = 2,
    }

    [Flags]
    private enum RedrawWindowFlags : uint
    {
        Invalidate = 0x0001,
        Erase = 0x0004,
        AllChildren = 0x0080,
        Frame = 0x0400,
    }

    [Flags]
    private enum FlashWindowFlags : uint
    {
        Tray = 0x00000002,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint WindowHandle;
        public FlashWindowFlags Flags;
        public uint Count;
        public uint TimeoutMilliseconds;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SYSLIB1054 // A source-generated import would require enabling unsafe code for this single call.
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        DwmWindowAttribute attribute,
        ref DwmWindowCornerPreference value,
        int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(
        nint windowHandle,
        nint updateRect,
        nint updateRegion,
        RedrawWindowFlags flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo flashInfo);
#pragma warning restore SYSLIB1054
}
