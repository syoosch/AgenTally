namespace AgenTally.Domain.Usage;

public sealed record UsageSessionNameMetadata
{
    public UsageSessionNameMetadata(
        string sessionId,
        string? sessionName,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Session-name update time must use UTC.",
                nameof(updatedAtUtc));
        }

        SessionId = sessionId;
        SessionName = sessionName;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SessionId { get; }

    public string? SessionName { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
