namespace AgenTally.UI.ViewModels;

public enum TrendGranularity
{
    Hour,
    Day,
    Week
}

internal sealed record CustomTimeRange
{
    public CustomTimeRange(DateTime startLocal, DateTime endExclusiveLocal)
    {
        StartLocal = NormalizeHour(startLocal, nameof(startLocal));
        EndExclusiveLocal = NormalizeHour(
            endExclusiveLocal,
            nameof(endExclusiveLocal));
        if (EndExclusiveLocal <= StartLocal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endExclusiveLocal),
                "自定义时间范围的结束时刻必须晚于开始时刻。");
        }
    }

    public DateTime StartLocal { get; }

    public DateTime EndExclusiveLocal { get; }

    private static DateTime NormalizeHour(DateTime value, string parameterName)
    {
        DateTime local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (local.Minute != 0 ||
            local.Second != 0 ||
            local.Millisecond != 0 ||
            local.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException(
                "自定义时间范围必须精确到整点。",
                parameterName);
        }

        return local;
    }
}

internal sealed record StatisticsPeriodBounds(
    DateTimeOffset StartInclusiveUtc,
    DateTimeOffset EndExclusiveUtc,
    DateTime LocalStart,
    DateTime LocalEndExclusive)
{
    public TimeSpan Elapsed => EndExclusiveUtc - StartInclusiveUtc;

    public CustomTimeRange ToCustomRange() => new(LocalStart, LocalEndExclusive);
}

internal sealed class StatisticsPeriodResolver
{
    private readonly TimeZoneInfo _localTimeZone;

    public StatisticsPeriodResolver(TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _localTimeZone = localTimeZone;
    }

    public StatisticsPeriodBounds Resolve(
        string period,
        DateTimeOffset nowUtc,
        DateTime? customStartLocal,
        DateTime? customEndExclusiveLocal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        DateTime localDate = TimeZoneInfo.ConvertTime(
            nowUtc,
            _localTimeZone).Date;
        DateTime localStart;
        DateTime localEndExclusive = localDate.AddDays(1);
        switch (period)
        {
            case DashboardViewModel.AllTime:
                return CreateAllTimeBounds(localEndExclusive);
            case DashboardViewModel.Today:
                localStart = localDate;
                break;
            case DashboardViewModel.SevenDays:
                localStart = localDate.AddDays(-6);
                break;
            case DashboardViewModel.ThirtyDays:
                localStart = localDate.AddDays(-29);
                break;
            case DashboardViewModel.NinetyDays:
                localStart = localDate.AddDays(-89);
                break;
            case DashboardViewModel.Custom:
                if (customStartLocal is not DateTime customStart ||
                    customEndExclusiveLocal is not DateTime customEnd)
                {
                    throw new InvalidOperationException(
                        "请选择完整的自定义开始和结束时刻。");
                }

                var range = new CustomTimeRange(customStart, customEnd);
                return CreateBounds(range.StartLocal, range.EndExclusiveLocal);
            default:
                throw new InvalidOperationException("不支持的时间范围。");
        }

        return CreateBounds(localStart, localEndExclusive);
    }

    public StatisticsPeriodBounds CreateBounds(
        DateTime localStart,
        DateTime localEndExclusive)
    {
        DateTime start = DateTime.SpecifyKind(
            localStart,
            DateTimeKind.Unspecified);
        DateTime end = DateTime.SpecifyKind(
            localEndExclusive,
            DateTimeKind.Unspecified);
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(localEndExclusive));
        }

        if (_localTimeZone.IsInvalidTime(start) ||
            _localTimeZone.IsInvalidTime(end))
        {
            throw new InvalidOperationException(
                "所选时刻在当前系统时区中不存在，请选择其他小时。");
        }

        DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(start, _localTimeZone);
        DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(end, _localTimeZone);
        if (endUtc <= startUtc)
        {
            throw new InvalidOperationException(
                "自定义时间范围的结束时刻必须晚于开始时刻。");
        }

        return new StatisticsPeriodBounds(
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(endUtc, TimeSpan.Zero),
            start,
            end);
    }

    public CustomTimeRange CreateDraftSeed(
        string previousPeriod,
        DateTimeOffset nowUtc,
        StatisticsPeriodBounds? lastEffectiveBounds)
    {
        if (lastEffectiveBounds is not null)
        {
            return lastEffectiveBounds.ToCustomRange();
        }

        string seedPeriod = previousPeriod == DashboardViewModel.AllTime
            ? DashboardViewModel.ThirtyDays
            : previousPeriod;
        return Resolve(seedPeriod, nowUtc, null, null).ToCustomRange();
    }

    private StatisticsPeriodBounds CreateAllTimeBounds(
        DateTime localEndExclusive)
    {
        DateTimeOffset endUtc = ToUtc(localEndExclusive);
        return new StatisticsPeriodBounds(
            DateTimeOffset.UnixEpoch,
            endUtc,
            TimeZoneInfo.ConvertTime(
                DateTimeOffset.UnixEpoch,
                _localTimeZone).Date,
            DateTime.SpecifyKind(
                localEndExclusive,
                DateTimeKind.Unspecified));
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        DateTime unspecified = DateTime.SpecifyKind(
            local,
            DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
            unspecified,
            _localTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
