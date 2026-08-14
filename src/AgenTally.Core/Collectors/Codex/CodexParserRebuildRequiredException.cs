namespace AgenTally.Core.Collectors.Codex;

public sealed class CodexParserRebuildRequiredException :
    AgentParserRebuildRequiredException
{
    public CodexParserRebuildRequiredException(
        string storedParserVersion,
        string requiredParserVersion)
        : base("codex", storedParserVersion, requiredParserVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedParserVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredParserVersion);

    }
}
