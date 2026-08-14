using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgenTally.UI.Controls;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class UsageHeatmapTests
{
    [TestMethod]
    public void HoverCard_FormatsLongTotalsWithCompactUnits()
    {
        Assert.AreEqual("—", UsageHeatmap.FormatCompactTokens(null));
        Assert.AreEqual("999,999", UsageHeatmap.FormatCompactTokens(999_999));
        Assert.AreEqual("1M", UsageHeatmap.FormatCompactTokens(1_000_000));
        Assert.AreEqual(
            "643.8M",
            UsageHeatmap.FormatCompactTokens(643_800_000));
        Assert.AreEqual(
            "4.2B",
            UsageHeatmap.FormatCompactTokens(4_158_269_695));
    }

    [TestMethod]
    public async Task HoverCard_FollowsHoveredDayCellAndClearsOutside()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            DateTime start = new(2025, 7, 29);
            DateTime end = start.AddDays(364);
            var heatmap = new UsageHeatmap
            {
                Days = Enumerable.Range(0, 365)
                    .Select(index => new UsageHeatmapDay(
                        start.AddDays(index),
                        index == 0
                            ? 643_800_000
                            : index == 364
                                ? 4_158_269_695
                                : index + 1,
                        1,
                        0))
                    .ToArray()
            };
            byte[] beforeHover = Render(heatmap, 300, 126);

            Rect firstCell = heatmap.GetDayBounds(start);
            Rect lastCell = heatmap.GetDayBounds(end);
            Assert.IsFalse(firstCell.IsEmpty);
            Assert.IsFalse(lastCell.IsEmpty);

            Assert.IsTrue(heatmap.UpdateHover(Center(firstCell)));
            Assert.AreEqual(start, heatmap.HoveredDate);
            byte[] afterHover = Render(heatmap, 300, 126);
            CollectionAssert.AreEqual(
                beforeHover,
                afterHover,
                "悬停只应显示外部详情卡，不应给日期方块增加描边。");
            Rect firstCard = heatmap.HoverCardBounds;
            Assert.IsFalse(firstCard.IsEmpty);
            Assert.AreEqual(152d, firstCard.Width, 0.001);
            Assert.AreEqual(38d, firstCard.Height, 0.001);
            Assert.IsGreaterThanOrEqualTo(0d, firstCard.Left);
            Assert.IsLessThanOrEqualTo(300d, firstCard.Right);
            Assert.AreEqual(
                new HeatmapHoverCardPresentation("2025-07-29", "643.8M"),
                UsageHeatmap.CreateHoverPresentation(
                    heatmap.Days!.First()));

            Assert.IsTrue(heatmap.UpdateHover(Center(lastCell)));
            Assert.AreEqual(end, heatmap.HoveredDate);
            Rect lastCard = heatmap.HoverCardBounds;
            Assert.IsGreaterThan(
                firstCard.Left,
                lastCard.Left,
                "详情卡应随悬停日期方块向右移动。");
            Assert.IsLessThanOrEqualTo(300d, lastCard.Right);

            heatmap.ClearHover();
            Assert.IsNull(heatmap.HoveredDate);
            Assert.IsTrue(heatmap.HoverCardBounds.IsEmpty);
        });
    }

    private static Point Center(Rect bounds) => new(
        bounds.Left + (bounds.Width / 2),
        bounds.Top + (bounds.Height / 2));

    private static byte[] Render(
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
        byte[] pixels = new byte[(int)(width * height * 4)];
        bitmap.CopyPixels(pixels, (int)width * 4, 0);
        return pixels;
    }
}
