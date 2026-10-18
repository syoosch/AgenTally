using System.Collections.Frozen;
using System.Globalization;

namespace AgenTally.Storage.Pricing;

public sealed class PeakOffPeakSchedule
{
    internal const string DeepSeekSchedule = "deepseek-beijing-weekday-v1";
    internal const string DeepSeekDailySchedule = "deepseek-utc-daily-v1";
    internal const string DeepSeekNightSchedule = "deepseek-utc-night-2025-v1";
    private readonly FrozenDictionary<int, FrozenSet<DateOnly>> _holidays;

    internal PeakOffPeakSchedule(PeakOffPeakScheduleDocument document)
    {
        WeekdaysOnly = document.Schedule == DeepSeekSchedule;
        IsHistoricalNightDiscount = document.Schedule == DeepSeekNightSchedule;
        if (document.Schedule is not (DeepSeekSchedule or DeepSeekDailySchedule or DeepSeekNightSchedule) ||
            (!IsHistoricalNightDiscount && (document.PeakMultiplier is <= 1m or > 100m)) ||
            (IsHistoricalNightDiscount && (document.PeakMultiplier != 1m || document.PeakRates is null)) ||
            (!IsHistoricalNightDiscount && document.PeakRates is not null) ||
            document.HolidayCalendars is null ||
            WeekdaysOnly && document.HolidayCalendars.Count == 0 ||
            !WeekdaysOnly && document.HolidayCalendars.Count != 0)
        {
            throw new InvalidDataException("Invalid peak/off-peak price schedule.");
        }

        PeakMultiplier = document.PeakMultiplier;
        ExplicitPeakRates = document.PeakRates;
        if (ExplicitPeakRates is { } rates)
        {
            // Validate each published category independently: R1's historical
            // prices were rounded, so multiplying its discount by four is wrong.
            _ = rates.ToRate("time-price-validation");
        }
        var calendars = new Dictionary<int, FrozenSet<DateOnly>>();
        foreach (PriceHolidayCalendarDocument calendar in document.HolidayCalendars)
        {
            if (calendar.Year is < 1 or > 9999 ||
                !Uri.TryCreate(calendar.OfficialSource, UriKind.Absolute,
                    out Uri? source) || source.Scheme != Uri.UriSchemeHttps ||
                !TryParseDate(calendar.VerifiedOn, out _) ||
                calendar.Days is not { Count: > 0 and <= 366 })
            {
                throw new InvalidDataException("Invalid price holiday calendar.");
            }

            var days = new HashSet<DateOnly>();
            foreach (string value in calendar.Days)
            {
                if (!TryParseDate(value, out DateOnly day) ||
                    day.Year != calendar.Year || !days.Add(day))
                {
                    throw new InvalidDataException("Invalid price holiday date.");
                }
            }

            if (!calendars.TryAdd(calendar.Year, days.ToFrozenSet()))
            {
                throw new InvalidDataException("Duplicate price holiday calendar.");
            }
        }

        _holidays = calendars.ToFrozenDictionary();
    }

    public decimal PeakMultiplier { get; }

    public bool WeekdaysOnly { get; }

    public bool IsHistoricalNightDiscount { get; }

    public TimePriceRates? ExplicitPeakRates { get; }

    public IReadOnlyList<int> CalendarYears => _holidays.Keys.Order().ToArray();

    internal ResolvedPriceRule? Resolve(
        ResolvedPriceRule rule,
        DateTimeOffset occurredAtUtc)
    {
        if (IsHistoricalNightDiscount)
        {
            TimeSpan utc = occurredAtUtc.UtcDateTime.TimeOfDay;
            bool standard = utc >= TimeSpan.FromMinutes(30) && utc < TimeSpan.FromHours(16.5);
            return rule with
            {
                RuleId = $"{rule.RuleId}/{(standard ? "peak" : "off-peak")}",
                Rate = standard ? ExplicitPeakRates!.ToRate(rule.MatchedModel) : rule.Rate,
                PeakOffPeak = null
            };
        }

        DateTimeOffset beijing;
        try
        {
            beijing = occurredAtUtc.ToOffset(TimeSpan.FromHours(8));
        }
        catch (ArgumentException)
        {
            return null;
        }

        // Beijing's date owns the weekday/holiday. Make-up working weekends
        // remain off-peak under the provider's explicit Monday-Friday rule.
        TimeSpan time = beijing.TimeOfDay;
        bool peakCandidate =
            (!WeekdaysOnly || beijing.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday) &&
            ((time >= TimeSpan.FromHours(9) && time < TimeSpan.FromHours(12)) ||
             (time >= TimeSpan.FromHours(14) && time < TimeSpan.FromHours(18)));
        bool peak = peakCandidate;
        if (peakCandidate && WeekdaysOnly)
        {
            if (!_holidays.TryGetValue(beijing.Year, out FrozenSet<DateOnly>? days))
            {
                // An unpublished/unbundled holiday calendar is not evidence
                // that a weekday is billable at the peak rate.
                return null;
            }

            peak = !days.Contains(DateOnly.FromDateTime(beijing.DateTime));
        }

        decimal multiplier = peak ? PeakMultiplier : 1m;
        ModelPriceRate rate = rule.Rate;
        return rule with
        {
            RuleId = $"{rule.RuleId}/{(peak ? "peak" : "off-peak")}",
            Rate = new ModelPriceRate(
                rule.MatchedModel,
                rate.InputUsdPerMillion * multiplier,
                rate.CachedInputUsdPerMillion * multiplier,
                rate.CacheWriteUsdPerMillion * multiplier,
                rate.OutputUsdPerMillion * multiplier,
                rate.LongContextThresholdTokens,
                rate.LongContextInputMultiplier,
                rate.LongContextOutputMultiplier),
            PeakOffPeak = null
        };
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
}

internal sealed record PeakOffPeakScheduleDocument(
    string Schedule,
    decimal PeakMultiplier,
    IReadOnlyList<PriceHolidayCalendarDocument> HolidayCalendars,
    TimePriceRates? PeakRates = null);

public sealed record TimePriceRates(
    decimal? InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? CacheWriteUsdPerMillion,
    decimal? OutputUsdPerMillion)
{
    internal ModelPriceRate ToRate(string model) => new(model,
        InputUsdPerMillion ?? throw new InvalidDataException("Missing timed input price."),
        CachedInputUsdPerMillion,
        CacheWriteUsdPerMillion,
        OutputUsdPerMillion ?? throw new InvalidDataException("Missing timed output price."));
}

internal sealed record PriceHolidayCalendarDocument(
    int Year,
    string OfficialSource,
    string VerifiedOn,
    IReadOnlyList<string> Days);
