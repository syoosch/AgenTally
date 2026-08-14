using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Controls;

public sealed class UsageTrendChart : FrameworkElement
{
    private const double HoverCardHeight = 126;
    private const double HoverCardWidth = 264;
    private static readonly double[] NaturalScaleSteps =
        [1, 1.2, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
    private static readonly Brush DefaultTextBrush = FrozenBrush("#7A8494");
    private static readonly Brush DefaultBorderBrush = FrozenBrush("#DDE3EB");
    private static readonly Brush DefaultGridBrush = FrozenBrush("#E8ECF2");
    private static readonly Brush DefaultTotalBrush = FrozenBrush("#2F6FED");
    private static readonly Brush DefaultOutputBrush = FrozenBrush("#7B61C9");
    private static readonly Brush DefaultUncachedInputBrush = FrozenBrush("#B7791F");
    private static readonly Brush DefaultHoverCardTextBrush = FrozenBrush("#292621");
    private static readonly Brush DefaultHoverCardSecondaryTextBrush = FrozenBrush("#655F57");

    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(IEnumerable<UsageTrendPoint>),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalBrushProperty = BrushProperty(
        nameof(TotalBrush),
        DefaultTotalBrush);
    public static readonly DependencyProperty OutputBrushProperty = BrushProperty(
        nameof(OutputBrush),
        DefaultOutputBrush);
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(
        nameof(GridBrush),
        DefaultGridBrush);
    public static readonly DependencyProperty BorderBrushProperty = BrushProperty(
        nameof(BorderBrush),
        DefaultBorderBrush);
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(
        nameof(TextBrush),
        DefaultTextBrush);
    public static readonly DependencyProperty PlotBackgroundProperty = BrushProperty(
        nameof(PlotBackground),
        Brushes.White);
    public static readonly DependencyProperty UncachedInputBrushProperty = BrushProperty(
        nameof(UncachedInputBrush),
        DefaultUncachedInputBrush);
    public static readonly DependencyProperty HoverCardTextBrushProperty = BrushProperty(
        nameof(HoverCardTextBrush),
        DefaultHoverCardTextBrush);
    public static readonly DependencyProperty HoverCardSecondaryTextBrushProperty = BrushProperty(
        nameof(HoverCardSecondaryTextBrush),
        DefaultHoverCardSecondaryTextBrush);
    public static readonly DependencyProperty ShowOutputProperty =
        DependencyProperty.Register(
            nameof(ShowOutput),
            typeof(bool),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightPeakProperty =
        DependencyProperty.Register(
            nameof(HighlightPeak),
            typeof(bool),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GranularityProperty =
        DependencyProperty.Register(
            nameof(Granularity),
            typeof(TrendGranularity),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                TrendGranularity.Day,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TimeZoneProperty =
        DependencyProperty.Register(
            nameof(TimeZone),
            typeof(TimeZoneInfo),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                TimeZoneInfo.Local,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RangeStartInclusiveUtcProperty =
        DependencyProperty.Register(
            nameof(RangeStartInclusiveUtc),
            typeof(DateTimeOffset?),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RangeEndExclusiveUtcProperty =
        DependencyProperty.Register(
            nameof(RangeEndExclusiveUtc),
            typeof(DateTimeOffset?),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AllowHoverCardOutsidePlotProperty =
        DependencyProperty.Register(
            nameof(AllowHoverCardOutsidePlot),
            typeof(bool),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    private IReadOnlyList<UsageTrendPoint> _renderedPoints = [];
    private Rect _renderedPlot;
    private DateTimeOffset? _hoveredBucketStartUtc;
    private Rect _renderedHoverCardBounds;
    private readonly Popup _hoverPopup;
    private readonly HoverCardSurface _hoverPopupSurface;

    public UsageTrendChart()
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

    public IEnumerable<UsageTrendPoint>? Points
    {
        get => (IEnumerable<UsageTrendPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush TotalBrush
    {
        get => (Brush)GetValue(TotalBrushProperty);
        set => SetValue(TotalBrushProperty, value);
    }

    public Brush OutputBrush
    {
        get => (Brush)GetValue(OutputBrushProperty);
        set => SetValue(OutputBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush BorderBrush
    {
        get => (Brush)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush PlotBackground
    {
        get => (Brush)GetValue(PlotBackgroundProperty);
        set => SetValue(PlotBackgroundProperty, value);
    }

    public Brush UncachedInputBrush
    {
        get => (Brush)GetValue(UncachedInputBrushProperty);
        set => SetValue(UncachedInputBrushProperty, value);
    }

    public Brush HoverCardTextBrush
    {
        get => (Brush)GetValue(HoverCardTextBrushProperty);
        set => SetValue(HoverCardTextBrushProperty, value);
    }

    public Brush HoverCardSecondaryTextBrush
    {
        get => (Brush)GetValue(HoverCardSecondaryTextBrushProperty);
        set => SetValue(HoverCardSecondaryTextBrushProperty, value);
    }

    public bool ShowOutput
    {
        get => (bool)GetValue(ShowOutputProperty);
        set => SetValue(ShowOutputProperty, value);
    }

    public bool HighlightPeak
    {
        get => (bool)GetValue(HighlightPeakProperty);
        set => SetValue(HighlightPeakProperty, value);
    }

    public TrendGranularity Granularity
    {
        get => (TrendGranularity)GetValue(GranularityProperty);
        set => SetValue(GranularityProperty, value);
    }

    public TimeZoneInfo TimeZone
    {
        get => (TimeZoneInfo)GetValue(TimeZoneProperty);
        set => SetValue(TimeZoneProperty, value);
    }

    public DateTimeOffset? RangeStartInclusiveUtc
    {
        get => (DateTimeOffset?)GetValue(RangeStartInclusiveUtcProperty);
        set => SetValue(RangeStartInclusiveUtcProperty, value);
    }

    public DateTimeOffset? RangeEndExclusiveUtc
    {
        get => (DateTimeOffset?)GetValue(RangeEndExclusiveUtcProperty);
        set => SetValue(RangeEndExclusiveUtcProperty, value);
    }

    public bool AllowHoverCardOutsidePlot
    {
        get => (bool)GetValue(AllowHoverCardOutsidePlotProperty);
        set => SetValue(AllowHoverCardOutsidePlotProperty, value);
    }

    internal DateTimeOffset? HoveredBucketStartUtc => _hoveredBucketStartUtc;

    internal Rect HoverCardBounds => _renderedHoverCardBounds;

    internal Rect PlotBounds => _renderedPlot;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _renderedPoints = [];
        _renderedPlot = Rect.Empty;
        _renderedHoverCardBounds = Rect.Empty;
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 220 || height < 120)
        {
            ResetHoverState();
            return;
        }

        var plot = new Rect(
            54,
            18,
            width - (ShowOutput ? 112 : 70),
            height - 56);
        _renderedPlot = plot;
        drawingContext.DrawRoundedRectangle(
            PlotBackground,
            new Pen(BorderBrush, 1),
            plot,
            6,
            6);

        UsageTrendPoint[] points = Points?
            .OrderBy(point => point.BucketStartUtc)
            .GroupBy(point => point.BucketStartUtc)
            .Select(static group => group.Last())
            .ToArray() ?? [];
        _renderedPoints = points;
        if (points.Length == 0)
        {
            ResetHoverState();
            DrawCenteredText(drawingContext, plot, "暂无趋势数据");
            return;
        }

        long[] totalValues = points
            .Select(static point => point.NormalizedTotal.Value)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        long[] outputValues = ShowOutput
            ? points
                .Select(static point => point.Output.Value)
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .ToArray()
            : [];
        if (totalValues.Length == 0 && outputValues.Length == 0)
        {
            ResetHoverState();
            DrawCenteredText(drawingContext, plot, "暂无可用指标");
            return;
        }

        TrendAxisScale totalScale = CreateAxisScale(
            totalValues.DefaultIfEmpty(0).Max());
        TrendAxisScale outputScale = CreateAxisScale(
            outputValues.DefaultIfEmpty(0).Max());
        DrawAxisLabels(
            drawingContext,
            plot,
            totalScale,
            outputScale,
            ShowOutput);
        long minimumTime = points[0].BucketStartUtc.ToUnixTimeMilliseconds();
        long maximumTime = points[^1].BucketStartUtc.ToUnixTimeMilliseconds();
        DrawTimeLabels(
            drawingContext,
            plot,
            points[0].BucketStartUtc,
            points[^1].BucketStartUtc);
        var plotClip = new RectangleGeometry(plot, 6, 6);
        plotClip.Freeze();
        drawingContext.PushClip(plotClip);
        DrawGridLines(
            drawingContext,
            plot,
            totalScale,
            outputScale,
            ShowOutput);
        DrawSeries(
            drawingContext,
            plot,
            points,
            minimumTime,
            maximumTime,
            totalScale.Maximum,
            new Pen(TotalBrush, 2),
            static point => point.NormalizedTotal.Value);
        if (ShowOutput)
        {
            DrawSeries(
                drawingContext,
                plot,
                points,
                minimumTime,
                maximumTime,
                outputScale.Maximum,
                new Pen(OutputBrush, 1.8),
                static point => point.Output.Value);
        }

        if (HighlightPeak)
        {
            DrawPeak(
                drawingContext,
                plot,
                points,
                minimumTime,
                maximumTime,
                totalScale.Maximum);
        }

        UsageTrendPoint? hoveredPoint = _hoveredBucketStartUtc is DateTimeOffset hovered
            ? points.FirstOrDefault(point => point.BucketStartUtc == hovered)
            : null;
        if (hoveredPoint is not null)
        {
            DrawHoverDetails(
                drawingContext,
                plot,
                hoveredPoint,
                minimumTime,
                maximumTime,
                totalScale.Maximum,
                outputScale.Maximum);
        }
        else if (_hoveredBucketStartUtc is not null)
        {
            ResetHoverState();
        }

        drawingContext.Pop();
        drawingContext.DrawRoundedRectangle(
            null,
            new Pen(BorderBrush, 1),
            plot,
            6,
            6);
    }

    private void ResetHoverState()
    {
        Cursor = null;
        _hoveredBucketStartUtc = null;
        _renderedHoverCardBounds = Rect.Empty;
        CloseHoverPopup();
    }

    private void DrawGridLines(
        DrawingContext drawingContext,
        Rect plot,
        TrendAxisScale totalScale,
        TrendAxisScale outputScale,
        bool showOutput)
    {
        int gridIntervals = Math.Max(
            totalScale.MajorIntervalCount,
            showOutput ? outputScale.MajorIntervalCount : 0);
        for (int index = 0; index <= gridIntervals; index++)
        {
            double ratio = index / (double)gridIntervals;
            double y = plot.Bottom - (plot.Height * ratio);
            drawingContext.DrawLine(
                new Pen(GridBrush, 1),
                new Point(plot.Left, y),
                new Point(plot.Right, y));
        }
    }

    private void DrawAxisLabels(
        DrawingContext drawingContext,
        Rect plot,
        TrendAxisScale totalScale,
        TrendAxisScale outputScale,
        bool showOutput)
    {
        for (int index = 0; index <= totalScale.MajorIntervalCount; index++)
        {
            double ratio = index / (double)totalScale.MajorIntervalCount;
            double y = plot.Bottom - (plot.Height * ratio);
            string totalLabel = FormatCompact(totalScale.Maximum * ratio);
            FormattedText text = CreateText(totalLabel, 10.5, TotalBrush);
            drawingContext.DrawText(
                text,
                new Point(plot.Left - text.Width - 8, y - (text.Height / 2)));
        }

        if (!showOutput)
        {
            return;
        }

        for (int index = 0; index <= outputScale.MajorIntervalCount; index++)
        {
            double ratio = index / (double)outputScale.MajorIntervalCount;
            double y = plot.Bottom - (plot.Height * ratio);
            string outputLabel = FormatCompact(outputScale.Maximum * ratio);
            FormattedText outputText = CreateText(
                outputLabel,
                10.5,
                OutputBrush);
            drawingContext.DrawText(
                outputText,
                new Point(plot.Right + 8, y - (outputText.Height / 2)));
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
        if (_renderedPoints.Count == 0 ||
            _renderedPlot.Width <= 0 ||
            !_renderedPlot.Contains(position))
        {
            ClearHover();
            return false;
        }

        double ratio = Math.Clamp(
            (position.X - _renderedPlot.Left) / _renderedPlot.Width,
            0,
            1);
        UsageTrendPoint nearest = GetNearestPoint(ratio);
        Cursor = Cursors.None;
        bool bucketChanged = _hoveredBucketStartUtc != nearest.BucketStartUtc;
        _hoveredBucketStartUtc = nearest.BucketStartUtc;
        if (AllowHoverCardOutsidePlot)
        {
            UpdateOverflowHoverPopup(nearest);
        }

        if (bucketChanged)
        {
            InvalidateVisual();
        }

        return true;
    }

    internal void ClearHover()
    {
        Cursor = null;
        CloseHoverPopup();
        if (_hoveredBucketStartUtc is null)
        {
            return;
        }

        _hoveredBucketStartUtc = null;
        _renderedHoverCardBounds = Rect.Empty;
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) =>
        CloseHoverPopup();

    private void CloseHoverPopup()
    {
        _hoverPopup.IsOpen = false;
        _hoverPopupSurface.Presentation = null;
    }

    internal TrendHoverCardPresentation? CreateHoverPresentation(
        double positionRatio)
    {
        if (_renderedPoints.Count == 0)
        {
            return null;
        }

        UsageTrendPoint nearest = GetNearestPoint(positionRatio);
        PricePresentation price = nearest.RequestCount == 0
            ? new PricePresentation(
                "$0.00",
                "暂无调用",
                PricePresentationState.Complete)
            : PricePresentationFormatter.Describe(nearest.Pricing);
        string priceLabel = price.State == PricePresentationState.Partial
            ? "等效价格（部分）"
            : "等效价格";
        return new TrendHoverCardPresentation(
            nearest.BucketStartUtc,
            FormatHoverInterval(nearest.BucketStartUtc),
            FormatMetric(nearest.NormalizedTotal, includeUnit: true),
            [
                new TrendHoverMetricPresentation(
                    "缓存输入",
                    FormatMetric(nearest.CacheRead)),
                new TrendHoverMetricPresentation(
                    "未缓存输入",
                    FormatMetric(nearest.UncachedInput)),
                new TrendHoverMetricPresentation(
                    "输出",
                    FormatMetric(nearest.Output))
            ],
            priceLabel,
            price.ValueText,
            price.State);
    }

    private UsageTrendPoint GetNearestPoint(double positionRatio)
    {
        double ratio = Math.Clamp(positionRatio, 0, 1);
        long minimum = _renderedPoints[0].BucketStartUtc
            .ToUnixTimeMilliseconds();
        long maximum = _renderedPoints[^1].BucketStartUtc
            .ToUnixTimeMilliseconds();
        long target = minimum + (long)((maximum - minimum) * ratio);
        return _renderedPoints
            .OrderBy(point => Math.Abs(
                point.BucketStartUtc.ToUnixTimeMilliseconds() - target))
            .First();
    }

    private void DrawTimeLabels(
        DrawingContext drawingContext,
        Rect plot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        FormattedText start = CreateText(FormatAxisBucket(startUtc), 10.5);
        FormattedText end = CreateText(FormatAxisBucket(endUtc), 10.5);
        double y = plot.Bottom + 8;
        drawingContext.DrawText(start, new Point(plot.Left, y));
        drawingContext.DrawText(end, new Point(plot.Right - end.Width, y));
    }

    internal string FormatAxisBucket(DateTimeOffset bucketStartUtc)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(
            bucketStartUtc,
            TimeZone ?? TimeZoneInfo.Local);
        string format = Granularity == TrendGranularity.Hour
            ? "MM-dd HH:00"
            : "MM-dd";
        return local.ToString(format, CultureInfo.CurrentCulture);
    }

    internal string FormatHoverInterval(DateTimeOffset bucketStartUtc)
    {
        DateTimeOffset nominalEndUtc = GetBucketEndUtc(bucketStartUtc);
        DateTimeOffset actualStartUtc = RangeStartInclusiveUtc is DateTimeOffset rangeStart &&
            rangeStart > bucketStartUtc
                ? rangeStart
                : bucketStartUtc;
        DateTimeOffset actualEndUtc = RangeEndExclusiveUtc is DateTimeOffset rangeEnd &&
            rangeEnd < nominalEndUtc
                ? rangeEnd
                : nominalEndUtc;
        if (actualEndUtc <= actualStartUtc)
        {
            actualStartUtc = bucketStartUtc;
            actualEndUtc = nominalEndUtc;
        }

        TimeZoneInfo zone = TimeZone ?? TimeZoneInfo.Local;
        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(actualStartUtc, zone);
        DateTimeOffset localEnd = TimeZoneInfo.ConvertTime(actualEndUtc, zone);
        bool isFullBucket = actualStartUtc == bucketStartUtc &&
            actualEndUtc == nominalEndUtc;
        if (Granularity == TrendGranularity.Day && isFullBucket)
        {
            return localStart.ToString(
                "yyyy-MM-dd",
                CultureInfo.CurrentCulture);
        }

        if (Granularity == TrendGranularity.Week && isFullBucket)
        {
            DateTimeOffset inclusiveEnd = localEnd.AddDays(-1);
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{localStart:yyyy-MM-dd}–{inclusiveEnd:MM-dd}");
        }

        return localStart.Date == localEnd.Date
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{localStart:yyyy-MM-dd HH:mm}–{localEnd:HH:mm}")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{localStart:yyyy-MM-dd HH:mm}–{localEnd:MM-dd HH:mm}");
    }

    private DateTimeOffset GetBucketEndUtc(DateTimeOffset bucketStartUtc)
    {
        if (Granularity == TrendGranularity.Hour)
        {
            return bucketStartUtc.AddHours(1);
        }

        TimeZoneInfo zone = TimeZone ?? TimeZoneInfo.Local;
        DateTime localStart = TimeZoneInfo.ConvertTime(
            bucketStartUtc,
            zone).DateTime;
        DateTime localEnd = Granularity == TrendGranularity.Week
            ? localStart.AddDays(7)
            : localStart.AddDays(1);
        DateTime utcEnd = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified),
            zone);
        return new DateTimeOffset(utcEnd, TimeSpan.Zero);
    }

    private static string FormatMetric(
        MetricAggregate aggregate,
        bool includeUnit = false)
    {
        if (aggregate.Value is not long value)
        {
            return aggregate.Coverage switch
            {
                MetricCoverageStatus.Unknown => "未知",
                MetricCoverageStatus.Unavailable => "不可取得",
                _ => "—"
            };
        }

        string text = value.ToString("N0", CultureInfo.InvariantCulture);
        if (includeUnit)
        {
            text += " Token";
        }

        return aggregate.Coverage == MetricCoverageStatus.Partial
            ? text + "（部分）"
            : text;
    }

    private static void DrawSeries(
        DrawingContext drawingContext,
        Rect plot,
        IReadOnlyList<UsageTrendPoint> points,
        long minimumTime,
        long maximumTime,
        double maximumValue,
        Pen pen,
        Func<UsageTrendPoint, long?> valueSelector)
    {
        var run = new List<Point>();
        foreach (UsageTrendPoint point in points)
        {
            long? rawValue = valueSelector(point);
            if (rawValue is null)
            {
                DrawSmoothRun(drawingContext, pen, run);
                run.Clear();
                continue;
            }

            double timeRatio = maximumTime == minimumTime
                ? 0.5
                : (point.BucketStartUtc.ToUnixTimeMilliseconds() - minimumTime) /
                  (double)(maximumTime - minimumTime);
            double valueRatio = Math.Clamp(rawValue.Value / maximumValue, 0, 1);
            var current = new Point(
                plot.Left + (plot.Width * timeRatio),
                plot.Bottom - (plot.Height * valueRatio));
            run.Add(current);
        }

        DrawSmoothRun(drawingContext, pen, run);
    }

    private static void DrawSmoothRun(
        DrawingContext drawingContext,
        Pen pen,
        IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            drawingContext.DrawEllipse(
                pen.Brush,
                null,
                points[0],
                pen.Thickness,
                pen.Thickness);
            return;
        }

        IReadOnlyList<TrendCurveSegment> segments =
            CreateMonotoneCurveSegments(points);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            foreach (TrendCurveSegment segment in segments)
            {
                context.BezierTo(
                    segment.Control1,
                    segment.Control2,
                    segment.End,
                    true,
                    false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void DrawHoverDetails(
        DrawingContext drawingContext,
        Rect plot,
        UsageTrendPoint point,
        long minimumTime,
        long maximumTime,
        double totalMaximum,
        double outputMaximum)
    {
        double timeRatio = maximumTime == minimumTime
            ? 0.5
            : (point.BucketStartUtc.ToUnixTimeMilliseconds() - minimumTime) /
              (double)(maximumTime - minimumTime);
        double x = plot.Left + (plot.Width * timeRatio);
        var guidePen = new Pen(BorderBrush, 1)
        {
            DashStyle = new DashStyle([4d, 4d], 0)
        };
        drawingContext.DrawLine(
            guidePen,
            new Point(x, plot.Top),
            new Point(x, plot.Bottom));

        DrawHoverMarker(
            drawingContext,
            plot,
            x,
            point.NormalizedTotal.Value,
            totalMaximum,
            TotalBrush);
        if (ShowOutput)
        {
            DrawHoverMarker(
                drawingContext,
                plot,
                x,
                point.Output.Value,
                outputMaximum,
                OutputBrush);
        }

        TrendHoverCardPresentation? presentation =
            CreateHoverPresentation(timeRatio);
        if (presentation is null || ActualHeight < 144)
        {
            CloseHoverPopup();
            return;
        }

        if (AllowHoverCardOutsidePlot)
        {
            Rect overflowCard = CreateOverflowHoverCardBounds(plot, x);
            _renderedHoverCardBounds = overflowCard;
            ShowHoverPopup(presentation, overflowCard);
            return;
        }

        CloseHoverPopup();
        const double horizontalGap = 14;
        const double edgeGap = 8;
        double cardWidth = Math.Min(
            HoverCardWidth,
            Math.Max(188, plot.Width - (edgeGap * 2)));
        double left = x + horizontalGap;
        if (left + cardWidth > plot.Right - edgeGap)
        {
            left = x - horizontalGap - cardWidth;
        }

        left = Math.Clamp(
            left,
            plot.Left + edgeGap,
            Math.Max(plot.Left + edgeGap, plot.Right - cardWidth - edgeGap));
        double top = Math.Clamp(
            plot.Top + edgeGap,
            edgeGap,
            ActualHeight - HoverCardHeight - edgeGap);
        var card = new Rect(left, top, cardWidth, HoverCardHeight);
        _renderedHoverCardBounds = card;
        DrawHoverCard(drawingContext, card, presentation);
    }

    private Rect CreateOverflowHoverCardBounds(Rect plot, double x)
    {
        const double horizontalGap = 14;
        const double verticalGap = 12;
        const double viewportGap = 8;
        double viewportLeft = viewportGap;
        double viewportRight = Math.Max(
            viewportLeft + 188,
            ActualWidth - viewportGap);
        double viewportTop = plot.Top - HoverCardHeight - verticalGap;
        double viewportBottom = plot.Bottom + verticalGap + HoverCardHeight;
        if (IsLoaded && Window.GetWindow(this) is Window window)
        {
            Point origin = TranslatePoint(new Point(0, 0), window);
            viewportLeft = viewportGap - origin.X;
            viewportRight = window.ActualWidth - viewportGap - origin.X;
            viewportTop = 48 - origin.Y;
            viewportBottom = window.ActualHeight - viewportGap - origin.Y;
        }

        double availableWidth = Math.Max(
            188,
            viewportRight - viewportLeft);
        double cardWidth = Math.Min(HoverCardWidth, availableWidth);
        double left = x + horizontalGap;
        if (left + cardWidth > viewportRight)
        {
            left = x - horizontalGap - cardWidth;
        }

        left = Math.Clamp(
            left,
            viewportLeft,
            Math.Max(viewportLeft, viewportRight - cardWidth));

        double above = plot.Top - verticalGap - HoverCardHeight;
        double below = plot.Bottom + verticalGap;
        double spaceAbove = plot.Top - viewportTop;
        double spaceBelow = viewportBottom - plot.Bottom;
        bool aboveFits = spaceAbove >= HoverCardHeight + verticalGap;
        bool belowFits = spaceBelow >= HoverCardHeight + verticalGap;
        double top;
        if (aboveFits && (!belowFits || spaceAbove >= spaceBelow))
        {
            top = above;
        }
        else if (belowFits)
        {
            top = below;
        }
        else if (spaceAbove >= spaceBelow)
        {
            top = Math.Max(viewportTop, above);
        }
        else
        {
            top = Math.Min(
                below,
                viewportBottom - HoverCardHeight);
        }

        return new Rect(left, top, cardWidth, HoverCardHeight);
    }

    private void UpdateOverflowHoverPopup(UsageTrendPoint point)
    {
        if (_renderedPoints.Count == 0 || _renderedPlot.IsEmpty)
        {
            return;
        }

        long minimumTime = _renderedPoints[0].BucketStartUtc
            .ToUnixTimeMilliseconds();
        long maximumTime = _renderedPoints[^1].BucketStartUtc
            .ToUnixTimeMilliseconds();
        double timeRatio = maximumTime == minimumTime
            ? 0.5
            : (point.BucketStartUtc.ToUnixTimeMilliseconds() - minimumTime) /
              (double)(maximumTime - minimumTime);
        double x = _renderedPlot.Left + (_renderedPlot.Width * timeRatio);
        TrendHoverCardPresentation? presentation =
            CreateHoverPresentation(timeRatio);
        if (presentation is null)
        {
            return;
        }

        Rect card = CreateOverflowHoverCardBounds(_renderedPlot, x);
        _renderedHoverCardBounds = card;
        ShowHoverPopup(presentation, card);
    }

    private void ShowHoverPopup(
        TrendHoverCardPresentation presentation,
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
        TrendHoverCardPresentation presentation)
    {
        UsageHoverCardVisuals.DrawSurface(
            drawingContext,
            card,
            PlotBackground,
            BorderBrush);

        const double padding = 14;
        FormattedText intervalText = CreateText(
            presentation.IntervalText,
            11.5,
            HoverCardTextBrush,
            FontWeights.SemiBold);
        FormattedText totalText = CreateText(
            presentation.TotalText,
            11.5,
            HoverCardTextBrush,
            FontWeights.SemiBold);
        double totalX = card.Right - padding - totalText.Width;
        intervalText.MaxTextWidth = Math.Max(
            40,
            totalX - (card.Left + padding) - 10);
        intervalText.Trimming = TextTrimming.CharacterEllipsis;
        drawingContext.DrawText(
            intervalText,
            new Point(card.Left + padding, card.Top + 12));
        drawingContext.DrawText(
            totalText,
            new Point(totalX, card.Top + 12));

        double separatorY = card.Top + 37;
        drawingContext.DrawLine(
            new Pen(BorderBrush, 1),
            new Point(card.Left + padding, separatorY),
            new Point(card.Right - padding, separatorY));

        Brush[] metricBrushes = [TotalBrush, UncachedInputBrush, OutputBrush];
        double rowY = card.Top + 45;
        for (int index = 0; index < presentation.Metrics.Count; index++)
        {
            TrendHoverMetricPresentation metric = presentation.Metrics[index];
            double centerY = rowY + 7;
            drawingContext.DrawEllipse(
                metricBrushes[Math.Min(index, metricBrushes.Length - 1)],
                null,
                new Point(card.Left + padding + 3, centerY),
                3,
                3);
            DrawHoverRow(
                drawingContext,
                card,
                rowY,
                metric.Label,
                metric.Value,
                padding + 12,
                HoverCardTextBrush);
            rowY += 18;
        }

        Brush priceBrush = presentation.PriceState is
            PricePresentationState.Partial or PricePresentationState.Unpriced
                ? UncachedInputBrush
                : HoverCardTextBrush;
        DrawHoverRow(
            drawingContext,
            card,
            rowY,
            presentation.PriceLabel,
            presentation.PriceValue,
            padding,
            priceBrush);
    }

    private void DrawHoverMarker(
        DrawingContext drawingContext,
        Rect plot,
        double x,
        long? value,
        double maximum,
        Brush brush)
    {
        if (value is null || maximum <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(value.Value / maximum, 0, 1);
        var center = new Point(
            x,
            plot.Bottom - (plot.Height * ratio));
        drawingContext.DrawEllipse(
            brush,
            new Pen(PlotBackground, 2),
            center,
            4.5,
            4.5);
    }

    private void DrawHoverRow(
        DrawingContext drawingContext,
        Rect card,
        double y,
        string label,
        string value,
        double labelOffset,
        Brush valueBrush)
    {
        FormattedText labelText = CreateText(
            label,
            10.5,
            HoverCardSecondaryTextBrush);
        FormattedText valueText = CreateText(
            value,
            10.5,
            valueBrush);
        drawingContext.DrawText(
            labelText,
            new Point(card.Left + labelOffset, y));
        drawingContext.DrawText(
            valueText,
            new Point(card.Right - 14 - valueText.Width, y));
    }

    private void DrawPeak(
        DrawingContext drawingContext,
        Rect plot,
        IReadOnlyList<UsageTrendPoint> points,
        long minimumTime,
        long maximumTime,
        double maximumValue)
    {
        UsageTrendPoint? peak = points
            .Where(static point => point.NormalizedTotal.Value.HasValue)
            .OrderByDescending(static point => point.NormalizedTotal.Value)
            .ThenBy(static point => point.BucketStartUtc)
            .FirstOrDefault();
        if (peak?.NormalizedTotal.Value is not long value)
        {
            return;
        }

        double timeRatio = maximumTime == minimumTime
            ? 0.5
            : (peak.BucketStartUtc.ToUnixTimeMilliseconds() - minimumTime) /
              (double)(maximumTime - minimumTime);
        double valueRatio = Math.Clamp(value / maximumValue, 0, 1);
        var point = new Point(
            plot.Left + (plot.Width * timeRatio),
            plot.Bottom - (plot.Height * valueRatio));
        drawingContext.DrawEllipse(
            PlotBackground,
            new Pen(TotalBrush, 2),
            point,
            4,
            4);
    }

    private void DrawCenteredText(
        DrawingContext drawingContext,
        Rect plot,
        string value)
    {
        FormattedText text = CreateText(value, 12);
        drawingContext.DrawText(
            text,
            new Point(
                plot.Left + ((plot.Width - text.Width) / 2),
                plot.Top + ((plot.Height - text.Height) / 2)));
    }

    private FormattedText CreateText(
        string value,
        double size,
        Brush? brush = null,
        FontWeight? weight = null) => new(
        value,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface(
            new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal,
            weight ?? FontWeights.Normal,
            FontStretches.Normal),
        size,
        brush ?? TextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal static TrendAxisScale CreateAxisScale(long maximumValue)
    {
        if (maximumValue <= 0)
        {
            return new TrendAxisScale(4, 4);
        }

        double requiredMaximum = maximumValue / 0.92d;
        double requiredInterval = requiredMaximum / 4d;
        double exponent = Math.Floor(Math.Log10(requiredInterval));
        double magnitude = Math.Pow(10, exponent);
        double normalized = requiredInterval / magnitude;
        double naturalInterval = NaturalScaleSteps.First(step =>
            step >= normalized - 1e-12d);
        double maximum = Math.Max(4, naturalInterval * magnitude * 4);
        if (maximum <= maximumValue)
        {
            maximum = Math.BitIncrement((double)maximumValue);
        }

        return new TrendAxisScale(maximum, 4);
    }

    internal static IReadOnlyList<TrendCurveSegment> CreateMonotoneCurveSegments(
        IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return [];
        }

        int pointCount = points.Count;
        var widths = new double[pointCount - 1];
        var deltas = new double[pointCount - 1];
        for (int index = 0; index < pointCount - 1; index++)
        {
            double width = points[index + 1].X - points[index].X;
            if (width <= 0 || !double.IsFinite(width))
            {
                throw new ArgumentException(
                    "Curve points must have finite, strictly increasing X coordinates.",
                    nameof(points));
            }

            widths[index] = width;
            deltas[index] =
                (points[index + 1].Y - points[index].Y) / width;
        }

        var slopes = new double[pointCount];
        if (pointCount == 2)
        {
            slopes[0] = deltas[0];
            slopes[1] = deltas[0];
        }
        else
        {
            slopes[0] = CreateEndpointSlope(
                widths[0],
                widths[1],
                deltas[0],
                deltas[1]);
            slopes[^1] = CreateEndpointSlope(
                widths[^1],
                widths[^2],
                deltas[^1],
                deltas[^2]);
            for (int index = 1; index < pointCount - 1; index++)
            {
                double before = deltas[index - 1];
                double after = deltas[index];
                if (before == 0 ||
                    after == 0 ||
                    Math.Sign(before) != Math.Sign(after))
                {
                    slopes[index] = 0;
                    continue;
                }

                double beforeWeight =
                    (2 * widths[index]) + widths[index - 1];
                double afterWeight =
                    widths[index] + (2 * widths[index - 1]);
                slopes[index] =
                    (beforeWeight + afterWeight) /
                    ((beforeWeight / before) + (afterWeight / after));
            }
        }

        var segments = new List<TrendCurveSegment>(pointCount - 1);
        for (int index = 0; index < pointCount - 1; index++)
        {
            double thirdWidth = widths[index] / 3d;
            segments.Add(new TrendCurveSegment(
                points[index],
                new Point(
                    points[index].X + thirdWidth,
                    points[index].Y + (slopes[index] * thirdWidth)),
                new Point(
                    points[index + 1].X - thirdWidth,
                    points[index + 1].Y -
                    (slopes[index + 1] * thirdWidth)),
                points[index + 1]));
        }

        return segments;
    }

    private static double CreateEndpointSlope(
        double endpointWidth,
        double adjacentWidth,
        double endpointDelta,
        double adjacentDelta)
    {
        double slope =
            (((2 * endpointWidth) + adjacentWidth) * endpointDelta -
             (endpointWidth * adjacentDelta)) /
            (endpointWidth + adjacentWidth);
        if (slope == 0 ||
            endpointDelta == 0 ||
            Math.Sign(slope) != Math.Sign(endpointDelta))
        {
            return 0;
        }

        if (Math.Sign(endpointDelta) != Math.Sign(adjacentDelta) &&
            Math.Abs(slope) > Math.Abs(3 * endpointDelta))
        {
            return 3 * endpointDelta;
        }

        return slope;
    }

    private static string FormatCompact(double value) => value switch
    {
        >= 1_000_000_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000_000_000:0.#}B"),
        >= 1_000_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000_000:0.#}M"),
        >= 1_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000:0.#}K"),
        _ => value.ToString(
            Math.Abs(value - Math.Round(value)) < 1e-9 ? "N0" : "0.##",
            CultureInfo.InvariantCulture),
    };

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static DependencyProperty BrushProperty(
        string name,
        Brush defaultValue) => DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(UsageTrendChart),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private sealed class HoverCardSurface : FrameworkElement
    {
        private readonly UsageTrendChart _owner;
        private TrendHoverCardPresentation? _presentation;

        public HoverCardSurface(UsageTrendChart owner)
        {
            _owner = owner;
        }

        public TrendHoverCardPresentation? Presentation
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
}

internal sealed record TrendHoverCardPresentation(
    DateTimeOffset BucketStartUtc,
    string IntervalText,
    string TotalText,
    IReadOnlyList<TrendHoverMetricPresentation> Metrics,
    string PriceLabel,
    string PriceValue,
    PricePresentationState PriceState);

internal sealed record TrendHoverMetricPresentation(
    string Label,
    string Value);

internal sealed record TrendAxisScale(
    double Maximum,
    int MajorIntervalCount);

internal sealed record TrendCurveSegment(
    Point Start,
    Point Control1,
    Point Control2,
    Point End);
