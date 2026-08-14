namespace AgenTally.Core.Collectors;

public class AgentParserRebuildRequiredException : InvalidOperationException
{
    public AgentParserRebuildRequiredException(
        string agentId,
        string storedParserVersion,
        string requiredParserVersion)
        : base("Stored Agent usage data requires a parser rebuild.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedParserVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredParserVersion);
        AgentId = agentId;
        StoredParserVersion = storedParserVersion;
        RequiredParserVersion = requiredParserVersion;
    }

    public string AgentId { get; }

    public string StoredParserVersion { get; }

    public string RequiredParserVersion { get; }
}
