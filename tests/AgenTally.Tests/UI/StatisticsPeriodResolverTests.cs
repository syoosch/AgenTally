using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class StatisticsPeriodResolverTests
{
    [TestMethod]
    public void CustomRange_RequiresDistinctWholeHourExclusiveEndpoints()
    {
        DateTime start = new(2026, 8, 1, 9, 0, 0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CustomTimeRange(start, start));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new CustomTimeRange(start, start.AddHours(-1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new CustomTimeRange(start.AddMinutes(1), start.AddHours(1)));

        var range = new CustomTimeRange(start, start.AddHours(1));
        Assert.AreEqual(DateTimeKind.Unspecified, range.StartLocal.Kind);
        Assert.AreEqual(DateTimeKind.Unspecified, range.EndExclusiveLocal.Kind);
        Assert.AreEqual(TimeSpan.FromHours(1),
            range.EndExclusiveLocal - range.StartLocal);
    }

    [TestMethod]
    public void ResolveCustom_ConvertsSystemLocalHoursToStrictUtcRange()
    {
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone(
            "AgenTally.Tests.Resolver.UTC+08",
            TimeSpan.FromHours(8),
            "UTC+08",
            "UTC+08");
        var resolver = new StatisticsPeriodResolver(zone);

        StatisticsPeriodBounds bounds = resolver.Resolve(
            DashboardViewModel.Custom,
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTime(2026, 8, 1, 9, 0, 0),
            new DateTime(2026, 8, 1, 10, 0, 0));

        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero),
            bounds.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero),
            bounds.EndExclusiveUtc);
        Assert.AreEqual(TimeSpan.FromHours(1), bounds.Elapsed);
    }

    [TestMethod]
    public void ResolveCustom_PreservesCrossMonthAndYearHourBoundaries()
    {
        var resolver = new StatisticsPeriodResolver(TimeZoneInfo.Utc);
        StatisticsPeriodBounds bounds = resolver.Resolve(
            DashboardViewModel.Custom,
            new DateTimeOffset(2027, 1, 1, 2, 0, 0, TimeSpan.Zero),
            new DateTime(2026, 12, 31, 23, 0, 0),
            new DateTime(2027, 1, 1, 2, 0, 0));

        Assert.AreEqual(
            new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
            bounds.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2027, 1, 1, 2, 0, 0, TimeSpan.Zero),
            bounds.EndExclusiveUtc);
        Assert.AreEqual(TimeSpan.FromHours(3), bounds.Elapsed);
    }
}
