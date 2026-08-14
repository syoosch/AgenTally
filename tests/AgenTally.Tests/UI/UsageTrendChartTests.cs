using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.Controls;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class UsageTrendChartTests
{
    [TestMethod]
    public async Task Labels_UseExplicitGranularityAndTimeZone()
    {
        await using var host = new StaDispatcherTestHost();
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone(
            "AgenTally.Tests.Chart.UTC+08",
            TimeSpan.FromHours(8),
            "UTC+08",
            "UTC+08");
        DateTimeOffset bucket =
            new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);
        await host.InvokeAsync(() =>
        {
            var chart = new UsageTrendChart
            {
                TimeZone = zone,
                Granularity = TrendGranularity.Hour
            };

            Assert.AreEqual("08-01 09:00", chart.FormatAxisBucket(bucket));
            Assert.AreEqual(
                "2026-08-01 09:00–10:00",
                chart.FormatHoverInterval(bucket));

            chart.Granularity = TrendGranularity.Day;
            Assert.AreEqual("08-01", chart.FormatAxisBucket(bucket));
            Assert.AreEqual("2026-08-01", chart.FormatHoverInterval(bucket));

            chart.Granularity = TrendGranularity.Week;
            Assert.AreEqual("08-01", chart.FormatAxisBucket(bucket));
            Assert.AreEqual(
                "2026-08-01–08-07",
                chart.FormatHoverInterval(bucket));

            chart.Granularity = TrendGranularity.Day;
            chart.RangeStartInclusiveUtc = bucket.AddHours(2);
            chart.RangeEndExclusiveUtc = bucket.AddHours(7);
            Assert.AreEqual(
                "2026-08-01 11:00–16:00",
                chart.FormatHoverInterval(bucket));
        });
    }

    [TestMethod]
    public async Task HoverCard_ShowsImmediateNearestBucketDetailsAndClearsOutsidePlot()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            DateTimeOffset start = new(
                2026,
                8,
                10,
                15,
                0,
                0,
                TimeSpan.Zero);
            var chart = new UsageTrendChart
            {
                Granularity = TrendGranularity.Hour,
                TimeZone = TimeZoneInfo.Utc,
                RangeStartInclusiveUtc = start,
                RangeEndExclusiveUtc = start.AddHours(2),
                Points =
                [
                    new UsageTrendPoint(
                        start,
                        new MetricAggregate(1_000, 2, 0),
                        new MetricAggregate(200, 2, 0),
                        new MetricAggregate(100, 2, 0),
                        new MetricAggregate(700, 2, 0),
                        new MetricAggregate(0, 2, 0),
                        RequestCount: 2)
                    {
                        Pricing = new PricingAggregate(
                            1.2345m,
                            CompleteRecords: 1,
                            PartialRecords: 1,
                            UnpricedRecords: 0,
                            PricingMissingCategory.CacheWriteTokens)
                    },
                    new UsageTrendPoint(
                        start.AddHours(1),
                        new MetricAggregate(500, 1, 0),
                        new MetricAggregate(100, 1, 0),
                        new MetricAggregate(50, 1, 0),
                        new MetricAggregate(350, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        RequestCount: 1)
                ]
            };
            LayoutAndRender(chart, 900, 280);

            TrendHoverCardPresentation presentation =
                chart.CreateHoverPresentation(0) ??
                throw new AssertFailedException("首个时间桶应生成详情卡内容。");
            Assert.AreEqual("2026-08-10 15:00–16:00", presentation.IntervalText);
            Assert.AreEqual("1,000 Token", presentation.TotalText);
            CollectionAssert.AreEqual(
                new[] { "缓存输入", "未缓存输入", "输出" },
                presentation.Metrics.Select(static metric => metric.Label).ToArray());
            CollectionAssert.AreEqual(
                new[] { "700", "200", "100" },
                presentation.Metrics.Select(static metric => metric.Value).ToArray());
            Assert.AreEqual("等效价格（部分）", presentation.PriceLabel);
            Assert.AreEqual("$1.23", presentation.PriceValue);

            Assert.IsTrue(chart.UpdateHover(new Point(54, 60)));
            Assert.AreEqual(start, chart.HoveredBucketStartUtc);
            Assert.AreEqual(Cursors.None, chart.Cursor);
            LayoutAndRender(chart, 900, 280);
            Assert.IsFalse(chart.HoverCardBounds.IsEmpty);
            Assert.AreEqual(264, chart.HoverCardBounds.Width, 0.001);
            Assert.IsGreaterThanOrEqualTo(0, chart.HoverCardBounds.Left);
            Assert.IsLessThanOrEqualTo(900, chart.HoverCardBounds.Right);
            Assert.IsGreaterThanOrEqualTo(0, chart.HoverCardBounds.Top);
            Assert.IsLessThanOrEqualTo(280, chart.HoverCardBounds.Bottom);

            Assert.IsTrue(chart.UpdateHover(new Point(841, 60)));
            Assert.AreEqual(start.AddHours(1), chart.HoveredBucketStartUtc);
            LayoutAndRender(chart, 900, 280);
            Assert.IsLessThan(
                841,
                chart.HoverCardBounds.Right,
                "右侧时间桶的详情卡应翻转到指示线左侧。");
            Assert.IsLessThanOrEqualTo(900, chart.HoverCardBounds.Right);

            Assert.IsFalse(chart.UpdateHover(new Point(0, 0)));
            Assert.IsNull(chart.HoveredBucketStartUtc);
            Assert.IsNull(chart.Cursor);
            Assert.IsTrue(chart.HoverCardBounds.IsEmpty);

            chart.AllowHoverCardOutsidePlot = true;
            LayoutAndRender(chart, 900, 210);
            Assert.IsTrue(chart.UpdateHover(new Point(54, 60)));
            LayoutAndRender(chart, 900, 210);
            Assert.IsTrue(
                chart.HoverCardBounds.Bottom <= chart.PlotBounds.Top ||
                chart.HoverCardBounds.Top >= chart.PlotBounds.Bottom,
                "允许越出绘图区时，详情卡不应再覆盖矮图表。");
        });
    }

    [TestMethod]
    public async Task PlotContent_IsClippedInsideEveryRoundedCorner()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            DateTimeOffset start = new(
                2026,
                8,
                10,
                0,
                0,
                0,
                TimeSpan.Zero);
            var chart = new UsageTrendChart
            {
                BorderBrush = Brushes.Black,
                GridBrush = Brushes.Magenta,
                OutputBrush = Brushes.Transparent,
                PlotBackground = Brushes.White,
                TotalBrush = Brushes.Transparent,
                Points =
                [
                    new UsageTrendPoint(
                        start,
                        new MetricAggregate(10, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        new MetricAggregate(10, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        RequestCount: 1),
                    new UsageTrendPoint(
                        start.AddDays(1),
                        new MetricAggregate(20, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        new MetricAggregate(20, 1, 0),
                        new MetricAggregate(0, 1, 0),
                        RequestCount: 1)
                ]
            };

            RenderTargetBitmap bitmap = Render(chart, 900, 280);
            byte[] pixels = new byte[900 * 280 * 4];
            bitmap.CopyPixels(pixels, 900 * 4, 0);
            Rect plot = chart.PlotBounds;
            int left = (int)Math.Round(plot.Left);
            int right = (int)Math.Round(plot.Right);
            int top = (int)Math.Round(plot.Top);
            int bottom = (int)Math.Round(plot.Bottom);

            AssertNoMagenta(
                pixels,
                900,
                left,
                left + 3,
                top - 1,
                top + 1,
                "左上圆角");
            AssertNoMagenta(
                pixels,
                900,
                right - 3,
                right,
                top - 1,
                top + 1,
                "右上圆角");
            AssertNoMagenta(
                pixels,
                900,
                left,
                left + 3,
                bottom - 1,
                bottom + 1,
                "左下圆角");
            AssertNoMagenta(
                pixels,
                900,
                right - 3,
                right,
                bottom - 1,
                bottom + 1,
                "右下圆角");
        });
    }

    [TestMethod]
    public void AxisScale_UsesReadableUpperBoundsAboveEveryPositiveMaximum()
    {
        (long Maximum, double ExpectedUpper)[] cases =
        [
            (0, 4),
            (1, 4),
            (183, 200),
            (48_500, 60_000),
            (20_600_000, 24_000_000)
        ];

        foreach ((long maximum, double expectedUpper) in cases)
        {
            TrendAxisScale scale = UsageTrendChart.CreateAxisScale(maximum);

            Assert.AreEqual(expectedUpper, scale.Maximum, 0.0001);
            Assert.AreEqual(4, scale.MajorIntervalCount);
            if (maximum > 0)
            {
                Assert.IsGreaterThan(
                    maximum,
                    scale.Maximum,
                    $"Maximum {maximum} must retain visible top headroom.");
            }
        }
    }

    [TestMethod]
    public void MonotoneCurve_PassesEveryPointWithoutOvershootOrDirectionReversal()
    {
        Point[] points =
        [
            new(0, 100),
            new(8, 60),
            new(20, 80),
            new(33, 20),
            new(46, 20),
            new(60, 90)
        ];

        IReadOnlyList<TrendCurveSegment> segments =
            UsageTrendChart.CreateMonotoneCurveSegments(points);

        Assert.HasCount(points.Length - 1, segments);
        for (int index = 0; index < segments.Count; index++)
        {
            TrendCurveSegment segment = segments[index];
            Assert.AreEqual(points[index], segment.Start);
            Assert.AreEqual(points[index + 1], segment.End);
            double lower = Math.Min(segment.Start.Y, segment.End.Y);
            double upper = Math.Max(segment.Start.Y, segment.End.Y);
            double previous = segment.Start.Y;
            for (int sample = 0; sample <= 100; sample++)
            {
                double ratio = sample / 100d;
                Point value = Evaluate(segment, ratio);
                Assert.IsGreaterThanOrEqualTo(
                    lower - 0.000001,
                    value.Y,
                    $"Segment {index} created an extra peak.");
                Assert.IsLessThanOrEqualTo(
                    upper + 0.000001,
                    value.Y,
                    $"Segment {index} created an extra valley.");
                Assert.IsGreaterThanOrEqualTo(
                    segment.Start.X - 0.000001,
                    value.X);
                Assert.IsLessThanOrEqualTo(
                    segment.End.X + 0.000001,
                    value.X);
                if (sample > 0 && segment.End.Y > segment.Start.Y)
                {
                    Assert.IsGreaterThanOrEqualTo(
                        previous - 0.000001,
                        value.Y,
                        $"Segment {index} reversed an increasing interval.");
                }
                else if (sample > 0 && segment.End.Y < segment.Start.Y)
                {
                    Assert.IsLessThanOrEqualTo(
                        previous + 0.000001,
                        value.Y,
                        $"Segment {index} reversed a decreasing interval.");
                }
                else if (sample > 0)
                {
                    Assert.AreEqual(
                        segment.Start.Y,
                        value.Y,
                        0.000001,
                        $"Segment {index} introduced a wave in a flat interval.");
                }

                previous = value.Y;
            }
        }
    }

    private static Point Evaluate(TrendCurveSegment segment, double ratio)
    {
        double inverse = 1 - ratio;
        double startWeight = inverse * inverse * inverse;
        double control1Weight = 3 * inverse * inverse * ratio;
        double control2Weight = 3 * inverse * ratio * ratio;
        double endWeight = ratio * ratio * ratio;
        return new Point(
            (startWeight * segment.Start.X) +
            (control1Weight * segment.Control1.X) +
            (control2Weight * segment.Control2.X) +
            (endWeight * segment.End.X),
            (startWeight * segment.Start.Y) +
            (control1Weight * segment.Control1.Y) +
            (control2Weight * segment.Control2.Y) +
            (endWeight * segment.End.Y));
    }

    private static void LayoutAndRender(
        FrameworkElement element,
        double width,
        double height)
    {
        _ = Render(element, width, height);
    }

    private static RenderTargetBitmap Render(
        FrameworkElement element,
        double width,
        double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)width,
            (int)height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void AssertNoMagenta(
        byte[] pixels,
        int width,
        int left,
        int right,
        int top,
        int bottom,
        string corner)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                Assert.IsFalse(
                    red > 180 && blue > 180 && green < 100,
                    $"{corner}不应出现越过圆角的网格线。像素位置：({x}, {y})。");
            }
        }
    }
}
