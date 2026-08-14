using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Controls;

public enum CalendarRangeVisualState
{
    None,
    Range,
    Endpoint
}

public partial class DateTimeRangePicker : UserControl
{
    public static readonly DependencyProperty StartLocalProperty =
        DependencyProperty.Register(
            nameof(StartLocal),
            typeof(DateTime?),
            typeof(DateTimeRangePicker),
            new PropertyMetadata(null, OnCommittedRangeChanged));

    public static readonly DependencyProperty EndExclusiveLocalProperty =
        DependencyProperty.Register(
            nameof(EndExclusiveLocal),
            typeof(DateTime?),
            typeof(DateTimeRangePicker),
            new PropertyMetadata(null, OnCommittedRangeChanged));

    public static readonly DependencyProperty IsSelectionPendingProperty =
        DependencyProperty.Register(
            nameof(IsSelectionPending),
            typeof(bool),
            typeof(DateTimeRangePicker),
            new PropertyMetadata(false, OnSelectionPendingChanged));

    public static readonly DependencyProperty CommitCommandProperty =
        DependencyProperty.Register(
            nameof(CommitCommand),
            typeof(ICommand),
            typeof(DateTimeRangePicker));

    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.Register(
            nameof(CancelCommand),
            typeof(ICommand),
            typeof(DateTimeRangePicker));

    public static readonly DependencyProperty TimeZoneProperty =
        DependencyProperty.Register(
            nameof(TimeZone),
            typeof(TimeZoneInfo),
            typeof(DateTimeRangePicker),
            new PropertyMetadata(TimeZoneInfo.Local, OnTimeZoneChanged));

    public static readonly DependencyProperty RangeVisualStateProperty =
        DependencyProperty.RegisterAttached(
            "RangeVisualState",
            typeof(CalendarRangeVisualState),
            typeof(DateTimeRangePicker),
            new FrameworkPropertyMetadata(CalendarRangeVisualState.None));

    private readonly string[] _stageLabels =
        ["起始日期", "起始小时", "结束日期", "结束小时"];
    private PickerStage _stage;
    private DateTime? _draftStart;
    private DateTime? _draftEnd;
    private bool _openedForPendingSelection;
    private bool _committed;
    private bool _suppressCancel;
    private bool _updatingCalendar;
    private bool _calendarPointerGestureActive;
    private bool _stageUpdatePending;
    private int _stageUpdateVersion;

    public DateTimeRangePicker()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DateCalendar.LayoutUpdated += OnCalendarLayoutUpdated;
        UpdateCommittedText();
        UpdateTimeZoneText();
    }

    public DateTime? StartLocal
    {
        get => (DateTime?)GetValue(StartLocalProperty);
        set => SetValue(StartLocalProperty, value);
    }

    public DateTime? EndExclusiveLocal
    {
        get => (DateTime?)GetValue(EndExclusiveLocalProperty);
        set => SetValue(EndExclusiveLocalProperty, value);
    }

    public bool IsSelectionPending
    {
        get => (bool)GetValue(IsSelectionPendingProperty);
        set => SetValue(IsSelectionPendingProperty, value);
    }

    public ICommand? CommitCommand
    {
        get => (ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public TimeZoneInfo TimeZone
    {
        get => (TimeZoneInfo)GetValue(TimeZoneProperty);
        set => SetValue(TimeZoneProperty, value);
    }

    internal PickerStage ActiveStage => _stage;

    internal IReadOnlyList<HourOption> VisibleHourOptions =>
        HourItems.ItemsSource as IReadOnlyList<HourOption> ?? [];

    internal bool IsPopupOpen => PickerPopup.IsOpen;

    internal bool IsHourPanelVisible => HourItems.Visibility == Visibility.Visible;

    internal bool IsCalendarPanelVisible =>
        DateCalendar.Visibility == Visibility.Visible;

    internal UIElement CalendarInputSurface => DateCalendar;

    internal double PopupContentHeight =>
        (PickerPopup.Child as FrameworkElement)?.ActualHeight ?? 0d;

    internal bool ClosesOnOutsideInput => !PickerPopup.StaysOpen;

    internal string TimeZoneDescription => TimeZoneText.Text;

    internal bool IsOpenedForPendingSelection => _openedForPendingSelection;

    internal bool IsCommittedDraft => _committed;

    public static CalendarRangeVisualState GetRangeVisualState(
        DependencyObject element) =>
        (CalendarRangeVisualState)element.GetValue(RangeVisualStateProperty);

    public static void SetRangeVisualState(
        DependencyObject element,
        CalendarRangeVisualState value) =>
        element.SetValue(RangeVisualStateProperty, value);

    internal void OpenForSelection() => OpenPicker(IsSelectionPending);

    internal void SelectDateForTest(DateTime date) =>
        DateCalendar.SelectedDate = date.Date;

    internal void SelectHourForTest(int hour) => SelectHour(hour);

    internal void CancelForTest() => CancelDraftAndClose();

    private static void OnCommittedRangeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (DateTimeRangePicker)dependencyObject;
        picker.UpdateCommittedText();
    }

    private static void OnSelectionPendingChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (DateTimeRangePicker)dependencyObject;
        if ((bool)eventArgs.NewValue)
        {
            picker.Dispatcher.BeginInvoke(
                () => picker.OpenPicker(true),
                DispatcherPriority.Loaded);
            return;
        }

        if (picker.PickerPopup.IsOpen && picker._openedForPendingSelection)
        {
            picker._suppressCancel = true;
            picker.PickerPopup.IsOpen = false;
        }
    }

    private static void OnTimeZoneChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        ((DateTimeRangePicker)dependencyObject).UpdateTimeZoneText();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (IsSelectionPending)
        {
            OpenPicker(true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (PickerPopup.IsOpen && IsSelectionPending)
        {
            CancelDraftAndClose();
        }
        else if (PickerPopup.IsOpen)
        {
            _suppressCancel = true;
            PickerPopup.IsOpen = false;
        }
        else if (IsSelectionPending)
        {
            ExecuteCancel();
        }
    }

    private void OnTriggerClick(object sender, RoutedEventArgs eventArgs) =>
        OpenPicker(IsSelectionPending);

    private void OpenPicker(bool openedForPendingSelection)
    {
        if (!IsLoaded || PickerPopup.IsOpen)
        {
            return;
        }

        _openedForPendingSelection = openedForPendingSelection;
        _committed = false;
        _suppressCancel = false;
        ResetStageUpdateDeferral();
        _draftStart = NormalizeHour(StartLocal);
        _draftEnd = NormalizeHour(EndExclusiveLocal);
        _stage = PickerStage.StartDate;
        PickerPopup.IsOpen = true;
        UpdateStage();
        Dispatcher.BeginInvoke(
            () => DateCalendar.Focus(),
            DispatcherPriority.Input);
    }

    private void OnPopupClosed(object? sender, EventArgs eventArgs)
    {
        bool shouldCancel =
            _openedForPendingSelection && !_committed && !_suppressCancel;
        _openedForPendingSelection = false;
        _suppressCancel = false;
        ResetStageUpdateDeferral();
        if (shouldCancel)
        {
            ExecuteCancel();
        }
    }

    private void ExecuteCancel()
    {
        if (CancelCommand?.CanExecute(null) == true)
        {
            CancelCommand.Execute(null);
        }
    }

    private void CancelDraftAndClose()
    {
        bool shouldCancel = _openedForPendingSelection && !_committed;
        _suppressCancel = true;
        _openedForPendingSelection = false;
        ResetStageUpdateDeferral();
        PickerPopup.IsOpen = false;
        if (shouldCancel)
        {
            ExecuteCancel();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape || !PickerPopup.IsOpen)
        {
            return;
        }

        CancelDraftAndClose();
        eventArgs.Handled = true;
    }

    private void OnBackClick(object sender, RoutedEventArgs eventArgs)
    {
        ResetStageUpdateDeferral();
        _stage = _stage switch
        {
            PickerStage.StartHour => PickerStage.StartDate,
            PickerStage.EndDate => PickerStage.StartHour,
            PickerStage.EndHour => PickerStage.EndDate,
            _ => PickerStage.StartDate
        };
        UpdateStage();
    }

    private void OnSelectedDatesChanged(
        object? sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (_updatingCalendar || DateCalendar.SelectedDate is not DateTime date)
        {
            return;
        }

        DateTime selectedDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (_stage == PickerStage.StartDate)
        {
            _draftStart = selectedDate.AddHours(_draftStart?.Hour ?? 0);
            if (_draftEnd <= _draftStart)
            {
                _draftEnd = selectedDate.AddDays(1);
            }

            _stage = PickerStage.StartHour;
        }
        else if (_stage == PickerStage.EndDate)
        {
            _draftEnd = selectedDate.AddHours(_draftEnd?.Hour ?? 0);
            _stage = PickerStage.EndHour;
        }

        RequestStageUpdateAfterCurrentPointerGesture();
    }

    private void OnCalendarPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs) =>
        _calendarPointerGestureActive = true;

    private void OnCalendarPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        _calendarPointerGestureActive = false;
        QueuePendingStageUpdate();
    }

    private void OnCalendarLostMouseCapture(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (Mouse.LeftButton != MouseButtonState.Released)
        {
            return;
        }

        _calendarPointerGestureActive = false;
        QueuePendingStageUpdate();
    }

    private void RequestStageUpdateAfterCurrentPointerGesture()
    {
        _stageUpdatePending = true;
        QueuePendingStageUpdate();
    }

    private void QueuePendingStageUpdate()
    {
        if (!_stageUpdatePending || _calendarPointerGestureActive)
        {
            return;
        }

        _stageUpdatePending = false;
        PickerStage expectedStage = _stage;
        int expectedVersion = ++_stageUpdateVersion;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (expectedVersion == _stageUpdateVersion &&
                    PickerPopup.IsOpen &&
                    _stage == expectedStage)
                {
                    UpdateStage();
                }
            },
            DispatcherPriority.Input);
    }

    private void ResetStageUpdateDeferral()
    {
        _calendarPointerGestureActive = false;
        _stageUpdatePending = false;
        _stageUpdateVersion++;
    }

    private void OnHourClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: int hour })
        {
            SelectHour(hour);
        }
    }

    private void SelectHour(int hour)
    {
        if (hour is < 0 or > 23)
        {
            return;
        }

        if (_stage == PickerStage.StartHour && _draftStart is DateTime start)
        {
            DateTime candidate = start.Date.AddHours(hour);
            if (IsInvalidLocalTime(candidate))
            {
                return;
            }

            _draftStart = candidate;
            if (_draftEnd <= candidate)
            {
                _draftEnd = candidate.Date.AddDays(1);
            }

            _stage = PickerStage.EndDate;
            UpdateStage();
            return;
        }

        if (_stage != PickerStage.EndHour ||
            _draftStart is not DateTime rangeStart ||
            _draftEnd is not DateTime endDate)
        {
            return;
        }

        DateTime rangeEnd = endDate.Date.AddHours(hour);
        if (!IsValidEnd(rangeStart, rangeEnd))
        {
            return;
        }

        var range = new CustomTimeRange(rangeStart, rangeEnd);
        if (CommitCommand?.CanExecute(range) != true)
        {
            return;
        }

        _draftEnd = rangeEnd;
        _committed = true;
        CommitCommand.Execute(range);
        PickerPopup.IsOpen = false;
    }

    private void OnCalendarDisplayDateChanged(
        object? sender,
        CalendarDateChangedEventArgs eventArgs) =>
        Dispatcher.BeginInvoke(UpdateCalendarRangeVisuals, DispatcherPriority.Loaded);

    private void OnCalendarLayoutUpdated(object? sender, EventArgs eventArgs) =>
        UpdateCalendarRangeVisuals();

    private void UpdateStage()
    {
        StageTitle.Text = _stage switch
        {
            PickerStage.StartDate => "选择起始日期",
            PickerStage.StartHour => "选择起始小时",
            PickerStage.EndDate => "选择结束日期",
            PickerStage.EndHour => "选择结束小时",
            _ => string.Empty
        };
        StageHint.Text = _stage switch
        {
            PickerStage.StartDate => "先确定范围从哪一天开始",
            PickerStage.StartHour => FormatDateHint(_draftStart, "这一天的起始整点"),
            PickerStage.EndDate => "再确定结束边界所在日期",
            PickerStage.EndHour => FormatDateHint(_draftEnd, "结束时刻本身不计入范围"),
            _ => string.Empty
        };
        BackButton.Visibility = _stage == PickerStage.StartDate
            ? Visibility.Collapsed
            : Visibility.Visible;
        bool showingCalendar = _stage is PickerStage.StartDate or PickerStage.EndDate;
        DateCalendar.Visibility = showingCalendar
            ? Visibility.Visible
            : Visibility.Collapsed;
        HourItems.Visibility = showingCalendar
            ? Visibility.Collapsed
            : Visibility.Visible;
        ProgressItems.ItemsSource = _stageLabels
            .Select((label, index) => new StageOption(
                label,
                index == (int)_stage,
                index < (int)_stage))
            .ToArray();

        if (showingCalendar)
        {
            PrepareCalendar();
        }
        else
        {
            PrepareHours();
        }

        UpdateCalendarRangeVisuals();
    }

    private void PrepareCalendar()
    {
        _updatingCalendar = true;
        try
        {
            DateCalendar.BlackoutDates.Clear();
            DateCalendar.DisplayDateStart = null;
            DateTime? selected = _stage == PickerStage.StartDate
                ? _draftStart?.Date
                : _draftEnd?.Date ?? _draftStart?.Date;
            if (_stage == PickerStage.EndDate && _draftStart is DateTime start)
            {
                DateCalendar.DisplayDateStart = start.Date;
            }

            DateCalendar.SelectedDate = null;
            DateCalendar.DisplayDate = selected ?? DateTime.Today;
        }
        finally
        {
            _updatingCalendar = false;
        }

        Dispatcher.BeginInvoke(
            UpdateCalendarRangeVisuals,
            DispatcherPriority.Loaded);
    }

    private void PrepareHours()
    {
        DateTime? selectedDate = _stage == PickerStage.StartHour
            ? _draftStart
            : _draftEnd;
        HourItems.ItemsSource = Enumerable.Range(0, 24)
            .Select(hour =>
            {
                DateTime candidate = (selectedDate ?? DateTime.Today)
                    .Date
                    .AddHours(hour);
                bool enabled = _stage == PickerStage.StartHour
                    ? !IsInvalidLocalTime(candidate)
                    : _draftStart is DateTime start && IsValidEnd(start, candidate);
                return new HourOption(
                    hour,
                    $"{hour:00}:00",
                    enabled,
                    selectedDate?.Hour == hour);
            })
            .ToArray();
    }

    private bool IsValidEnd(DateTime start, DateTime end)
    {
        if (end <= start || IsInvalidLocalTime(end))
        {
            return false;
        }

        try
        {
            _ = new StatisticsPeriodResolver(TimeZone).CreateBounds(start, end);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsInvalidLocalTime(DateTime value) =>
        TimeZone.IsInvalidTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified));

    private void UpdateCommittedText()
    {
        StartValueText.Text = StartLocal?.ToString(
            "yyyy-MM-dd HH:00",
            CultureInfo.CurrentCulture) ?? "请选择";
        EndValueText.Text = EndExclusiveLocal?.ToString(
            "yyyy-MM-dd HH:00",
            CultureInfo.CurrentCulture) ?? "请选择";
    }

    private void UpdateTimeZoneText()
    {
        TimeZoneInfo zone = TimeZone ?? TimeZoneInfo.Local;
        TimeSpan offset = zone.GetUtcOffset(DateTimeOffset.Now);
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        TimeZoneText.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{zone.DisplayName} · UTC{sign}{offset.Duration():hh\\:mm}");
    }

    private void UpdateCalendarRangeVisuals()
    {
        if (!DateCalendar.IsLoaded)
        {
            return;
        }

        DateTime? startDate = _draftStart?.Date;
        DateTime? endDate = _draftEnd?.Date;
        foreach (CalendarDayButton dayButton in FindVisualChildren<CalendarDayButton>(
                     DateCalendar))
        {
            CalendarRangeVisualState state = CalendarRangeVisualState.None;
            if (dayButton.DataContext is DateTime date && startDate is DateTime start)
            {
                DateTime day = date.Date;
                if (day == start || (endDate is DateTime end && day == end))
                {
                    state = CalendarRangeVisualState.Endpoint;
                }
                else if (endDate is DateTime rangeEnd && day > start && day < rangeEnd)
                {
                    state = CalendarRangeVisualState.Range;
                }
            }

            if (GetRangeVisualState(dayButton) != state)
            {
                SetRangeVisualState(dayButton, state);
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static DateTime? NormalizeHour(DateTime? value) =>
        value is DateTime date
            ? DateTime.SpecifyKind(
                new DateTime(
                    date.Year,
                    date.Month,
                    date.Day,
                    date.Hour,
                    0,
                    0),
                DateTimeKind.Unspecified)
            : null;

    private static string FormatDateHint(DateTime? value, string suffix) =>
        value is DateTime date
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{date:yyyy年M月d日} · {suffix}")
            : suffix;

    internal enum PickerStage
    {
        StartDate,
        StartHour,
        EndDate,
        EndHour
    }

    internal sealed record HourOption(
        int Hour,
        string Label,
        bool IsEnabled,
        bool IsSelected);

    private sealed record StageOption(
        string Label,
        bool IsActive,
        bool IsComplete);
}
