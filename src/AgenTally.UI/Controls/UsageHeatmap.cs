using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Controls;

public sealed class UsageHeatmap : FrameworkElement
{
    private const int WeekCount = 53;
    private const double HoverCardHeight = 38;
    private const double HoverCardWidth = 152;
    private static readonly Brush DefaultEmptyBrush = FrozenBrush("#ECE5DA");
    private static readonly Brush DefaultLevel1Brush = FrozenBrush("#EBD2C5");
    private static readonly Brush DefaultLevel2Brush = FrozenBrush("#DDA98F");
    private static readonly Brush DefaultLevel3Brush = FrozenBrush("#D17E5B");
    private static readonly Brush DefaultLevel4Brush = FrozenBrush("#B85438");
    private static readonly Brush DefaultLabelBrush = FrozenBrush("#9D958A");
    private static readonly Brush DefaultHoverCardBackground =
        FrozenBrush("#FFFDF9");
    private static readonly Brush DefaultHoverCardBorderBrush =
        FrozenBrush("#DED4C7");
    private static readonly Brush DefaultHoverCardTextBrush =
        FrozenBrush("#292621");
    private readonly List<HeatmapHitTarget> _hitTargets = [];
    private readonly Popup _hoverPopup;
    private readonly HoverCardSurface _hoverPopupSurface;
    private DateTime? _hoveredDate;
    private Rect _hoveredCellBounds;
    private Rect _renderedHoverCardBounds;

    public static readonly DependencyProperty DaysProperty =
        DependencyProperty.Register(
            nameof(Days),
            typeof(IEnumerable<UsageHeatmapDay>),
            typeof(UsageHeatmap),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnDaysChanged));

    public static readonly DependencyProperty DayClickCommandProperty =
        DependencyProperty.Register(
            nameof(DayClickCommand),
            typeof(ICommand),
            typeof(UsageHeatmap));

    public static readonly DependencyProperty EmptyBrushProperty = BrushProperty(
        nameof(EmptyBrush),
        DefaultEmptyBrush);
    public static readonly DependencyProperty Level1BrushProperty = BrushProperty(
        nameof(Level1Brush),
        DefaultLevel1Brush);
    public static readonly DependencyProperty Level2BrushProperty = BrushProperty(
        nameof(Level2Brush),
        DefaultLevel2Brush);
    public static readonly DependencyProperty Level3BrushProperty = BrushProperty(
        nameof(Level3Brush),
        DefaultLevel3Brush);
    public static readonly DependencyProperty Level4BrushProperty = BrushProperty(
        nameof(Level4Brush),
        DefaultLevel4Brush);
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(
        nameof(LabelBrush),
        DefaultLabelBrush);
    public static readonly DependencyProperty HoverCardBackgroundProperty =
        BrushProperty(
            nameof(HoverCardBackground),
            DefaultHoverCardBackground);
    public static readonly DependencyProperty HoverCardBorderBrushProperty =
        BrushProperty(
            nameof(HoverCardBorderBrush),
            DefaultHoverCardBorderBrush);
    public static readonly DependencyProperty HoverCardTextBrushProperty =
        BrushProperty(
            nameof(HoverCardTextBrush),
            DefaultHoverCardTextBrush);

    public UsageHeatmap()
    {
        _hoverPopupSurface = new HoverCardSurface(this)
        {
            Width = HoverCardWidth,
            Height = HoverCardHeight + UsageHoverCardVisuals.ShadowOffset,
            IsHitTestVisible = false,
            Focusable = false
        };
        _hoverPopup = new Popup
        {
            AllowsTransparency = true,
            Child = _hoverPopupSurface,
            IsHitTestVisible = false,
            Focusable = false,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            StaysOpen = true
        };
        Unloaded += OnUnloaded;
    }

    public IEnumerable<UsageHeatmapDay>? Days
    {
        get => (IEnumerable<UsageHeatmapDay>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public ICommand? DayClickCommand
    {
        get => (ICommand?)GetValue(DayClickCommandProperty);
        set => SetValue(DayClickCommandProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public Brush Level1Brush
    {
        get => (Brush)GetValue(Level1BrushProperty);
        set => SetValue(Level1BrushProperty, value);
    }

    public Brush Level2Brush
    {
        get => (Brush)GetValue(Level2BrushProperty);
        set => SetValue(Level2BrushProperty, value);
    }

    public Brush Level3Brush
    {
        get => (Brush)GetValue(Level3BrushProperty);
        set => SetValue(Level3BrushProperty, value);
    }

    public Brush Level4Brush
    {
        get => (Brush)GetValue(Level4BrushProperty);
        set => SetValue(Level4BrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush HoverCardBackground
    {
        get => (Brush)GetValue(HoverCardBackgroundProperty);
        set => SetValue(HoverCardBackgroundProperty, value);
    }

    public Brush HoverCardBorderBrush
    {
        get => (Brush)GetValue(HoverCardBorderBrushProperty);
        set => SetValue(HoverCardBorderBrushProperty, value);
    }

    public Brush HoverCardTextBrush
    {
        get => (Brush)GetValue(HoverCardTextBrushProperty);
        set => SetValue(HoverCardTextBrushProperty, value);
    }

    internal DateTime? HoveredDate => _hoveredDate;

    internal Rect HoverCardBounds => _renderedHoverCardBounds;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _hitTargets.Clear();
        UsageHeatmapDay[] days = Days?
            .OrderBy(day => day.Date)
            .ToArray() ?? [];
        if (days.Length == 0 || ActualWidth < 180 || ActualHeight < 100)
        {
            DrawText(drawingContext, "暂无每日数据", new Point(8, 8));
            return;
        }

        const double labelWidth = 24;
        const double monthHeight = 18;
        double availableWidth = Math.Max(1, ActualWidth - labelWidth);
        double availableHeight = Math.Max(1, ActualHeight - monthHeight);
        double horizontalStep = availableWidth / WeekCount;
        double verticalStep = availableHeight / 7;
        double cell = Math.Max(
            3,
            Math.Min(
                12,
                Math.Min(horizontalStep - 1.5, verticalStep - 1.5)));
        double rowStep = verticalStep;
        DateTime firstDate = days[0].Date.Date;
        DateTime gridStart = firstDate.AddDays(
            -(((int)firstDate.DayOfWeek + 6) % 7));
        long maximum = days
            .Where(day => day.TotalTokens.HasValue)
            .Select(day => day.TotalTokens!.Value)
            .DefaultIfEmpty(0)
            .Max();

        DrawText(drawingContext, "一", new Point(0, monthHeight + rowStep - 2));
        DrawText(drawingContext, "三", new Point(0, monthHeight + (rowStep * 3) - 2));
        DrawText(drawingContext, "五", new Point(0, monthHeight + (rowStep * 5) - 2));

        int lastMonth = -1;
        double lastMonthLabelRight = labelWidth - 8;
        foreach (UsageHeatmapDay day in days)
        {
            int dayOffset = (day.Date.Date - gridStart).Days;
            int week = dayOffset / 7;
            int row = dayOffset % 7;
            if (week is < 0 or >= WeekCount)
            {
                continue;
            }

            double x = labelWidth + (week * horizontalStep);
            double y = monthHeight + (row * rowStep);
            var bounds = new Rect(x, y, cell, cell);
            drawingContext.DrawRoundedRectangle(
                SelectBrush(day.TotalTokens, maximum),
                null,
                bounds,
                2,
                2);
            _hitTargets.Add(new HeatmapHitTarget(bounds, day));

            if (day.Date.Month != lastMonth && day.Date.Day <= 7)
            {
                string monthLabel = day.Date.ToString(
                    "M月",
                    CultureInfo.CurrentCulture);
                FormattedText monthText = CreateText(monthLabel);
                if (x >= lastMonthLabelRight + 6 &&
                    x + monthText.Width <= ActualWidth)
                {
                    drawingContext.DrawText(monthText, new Point(x, 0));
                    lastMonthLabelRight = x + monthText.Width;
                }

                lastMonth = day.Date.Month;
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        UsageHeatmapDay? day = HitTestTarget(eventArgs.GetPosition(this))?.Day;
        if (day is not null && DayClickCommand?.CanExecute(day) == true)
        {
            DayClickCommand.Execute(day);
            eventArgs.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        UpdateHover(eventArgs.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        ClearHover();
    }

    internal bool UpdateHover(Point position)
    {
        HeatmapHitTarget? target = HitTestTarget(position);
        if (target is null)
        {
            ClearHover();
            return false;
        }

        Cursor = Cursors.Hand;
        DateTime hoveredDate = target.Day.Date.Date;
        if (_hoveredDate == hoveredDate &&
            _hoveredCellBounds == target.Bounds &&
            !_renderedHoverCardBounds.IsEmpty)
        {
            return true;
        }

        _hoveredDate = hoveredDate;
        _hoveredCellBounds = target.Bounds;
        _renderedHoverCardBounds = CreateHoverCardBounds(target.Bounds);
        ShowHoverPopup(
            CreateHoverPresentation(target.Day),
            _renderedHoverCardBounds);
        InvalidateVisual();
        return true;
    }

    internal void ClearHover()
    {
        bool hadHover = _hoveredDate.HasValue ||
            !_renderedHoverCardBounds.IsEmpty;
        _hoveredDate = null;
        _hoveredCellBounds = Rect.Empty;
        _renderedHoverCardBounds = Rect.Empty;
        Cursor = null;
        _hoverPopup.IsOpen = false;
        if (hadHover)
        {
            InvalidateVisual();
        }
    }

    internal Rect GetDayBounds(DateTime date) => _hitTargets
        .FirstOrDefault(target => target.Day.Date.Date == date.Date)
        ?.Bounds ?? Rect.Empty;

    internal static HeatmapHoverCardPresentation CreateHoverPresentation(
        UsageHeatmapDay day) => new(
            day.Date.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
            FormatCompactTokens(day.TotalTokens));

    internal static string FormatCompactTokens(long? value)
    {
        if (!value.HasValue)
        {
            return "—";
        }

        decimal absolute = Math.Abs((decimal)value.Value);
        if (absolute >= 1_000_000_000m)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Value / 1_000_000_000m:0.#}B");
        }

        if (absolute >= 1_000_000m)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Value / 1_000_000m:0.#}M");
        }

        return value.Value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private HeatmapHitTarget? HitTestTarget(Point point) => _hitTargets
        .FirstOrDefault(target => target.Bounds.Contains(point));

    private Rect CreateHoverCardBounds(Rect cell)
    {
        const double verticalGap = 8;
        const double viewportGap = 8;
        double viewportLeft = viewportGap;
        double viewportRight = Math.Max(
            viewportLeft + 140,
            ActualWidth - viewportGap);
        double viewportTop = viewportGap;
        double viewportBottom = Math.Max(
            viewportTop + HoverCardHeight,
            ActualHeight - viewportGap);
        if (IsLoaded && Window.GetWindow(this) is Window window)
        {
            Point origin = TranslatePoint(new Point(0, 0), window);
            viewportLeft = viewportGap - origin.X;
            viewportRight = window.ActualWidth - viewportGap - origin.X;
            viewportTop = 48 - origin.Y;
            viewportBottom = window.ActualHeight - viewportGap - origin.Y;
        }

        double availableWidth = Math.Max(
            140,
            viewportRight - viewportLeft);
        double cardWidth = Math.Min(HoverCardWidth, availableWidth);
        double left = cell.Left + ((cell.Width - cardWidth) / 2);
        left = Math.Clamp(
            left,
            viewportLeft,
            Math.Max(viewportLeft, viewportRight - cardWidth));

        double above = cell.Top - verticalGap - HoverCardHeight;
        double below = cell.Bottom + verticalGap;
        double top;
        if (above >= viewportTop)
        {
            top = above;
        }
        else if (below + HoverCardHeight <= viewportBottom)
        {
            top = below;
        }
        else
        {
            top = Math.Clamp(
                above,
                viewportTop,
                Math.Max(viewportTop, viewportBottom - HoverCardHeight));
        }

        return new Rect(left, top, cardWidth, HoverCardHeight);
    }

    private void ShowHoverPopup(
        HeatmapHoverCardPresentation presentation,
        Rect card)
    {
        _hoverPopupSurface.Width = card.Width;
        _hoverPopupSurface.Height =
            HoverCardHeight + UsageHoverCardVisuals.ShadowOffset;
        _hoverPopupSurface.Presentation = presentation;
        _hoverPopup.HorizontalOffset = card.X;
        _hoverPopup.VerticalOffset = card.Y;
        if (IsLoaded)
        {
            _hoverPopup.IsOpen = true;
        }
    }

    private void DrawHoverCard(
        DrawingContext drawingContext,
        Rect card,
        HeatmapHoverCardPresentation presentation)
    {
        UsageHoverCardVisuals.DrawSurface(
            drawingContext,
            card,
            HoverCardBackground,
            HoverCardBorderBrush);

        const double padding = 10;
        FormattedText dateText = CreateHoverText(presentation.DateText);
        FormattedText totalText = CreateHoverText(presentation.TotalText);
        double totalX = card.Right - padding - totalText.Width;
        dateText.MaxTextWidth = Math.Max(
            40,
            totalX - (card.Left + padding) - 10);
        dateText.Trimming = TextTrimming.CharacterEllipsis;
        double textY = card.Top +
            ((card.Height - Math.Max(dateText.Height, totalText.Height)) / 2);
        drawingContext.DrawText(
            dateText,
            new Point(card.Left + padding, textY));
        drawingContext.DrawText(
            totalText,
            new Point(totalX, textY));
    }

    private FormattedText CreateHoverText(string value) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(
            new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal),
        10.5,
        HoverCardTextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        _hoverPopup.IsOpen = false;
    }

    private static void OnDaysChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        ((UsageHeatmap)dependencyObject).ClearHover();
    }

    private Brush SelectBrush(long? value, long maximum)
    {
        if (!value.HasValue || value.Value <= 0 || maximum <= 0)
        {
            return EmptyBrush;
        }

        double ratio = value.Value / (double)maximum;
        return ratio switch
        {
            <= 0.25 => Level1Brush,
            <= 0.5 => Level2Brush,
            <= 0.75 => Level3Brush,
            _ => Level4Brush
        };
    }

    private void DrawText(
        DrawingContext drawingContext,
        string value,
        Point point)
    {
        drawingContext.DrawText(CreateText(value), point);
    }

    private FormattedText CreateText(string value) => new(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Microsoft YaHei UI"),
            9.5,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static DependencyProperty BrushProperty(
        string name,
        Brush defaultValue) => DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(UsageHeatmap),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private sealed record HeatmapHitTarget(
        Rect Bounds,
        UsageHeatmapDay Day);

    private sealed class HoverCardSurface : FrameworkElement
    {
        private readonly UsageHeatmap _owner;
        private HeatmapHoverCardPresentation? _presentation;

        public HoverCardSurface(UsageHeatmap owner)
        {
            _owner = owner;
        }

        public HeatmapHoverCardPresentation? Presentation
        {
            get => _presentation;
            set
            {
                _presentation = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (Presentation is null)
            {
                return;
            }

            _owner.DrawHoverCard(
                drawingContext,
                new Rect(0, 0, ActualWidth, HoverCardHeight),
                Presentation);
        }
    }

    private static Brush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}

internal sealed record HeatmapHoverCardPresentation(
    string DateText,
    string TotalText);
