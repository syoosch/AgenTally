namespace AgenTally.Domain.Usage;

public readonly record struct TokenMetric
{
    public TokenMetric(long? value, MetricOrigin origin)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Token 数不能为负数。");
        }

        if (value is null &&
            origin is not MetricOrigin.Unavailable and not MetricOrigin.Unknown)
        {
            throw new ArgumentException(
                "缺少数值时，来源必须是 Unavailable 或 Unknown。",
                nameof(origin));
        }

        if (value is not null &&
            origin is MetricOrigin.Unavailable or MetricOrigin.Unknown)
        {
            throw new ArgumentException(
                "Unavailable 或 Unknown 不能携带数值。",
                nameof(origin));
        }

        Value = value;
        Origin = origin;
    }

    public long? Value { get; }

    public MetricOrigin Origin { get; }

    public bool IsAvailable => Value.HasValue;

    public static TokenMetric Unavailable => new(null, MetricOrigin.Unavailable);

    public static TokenMetric Unknown => new(null, MetricOrigin.Unknown);

    public static TokenMetric Exact(long value) => new(value, MetricOrigin.Exact);
}
