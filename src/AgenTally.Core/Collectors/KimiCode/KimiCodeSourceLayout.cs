namespace AgenTally.Core.Collectors.KimiCode;

internal sealed class KimiCodeSourceLayout
{
    private readonly IReadOnlyList<SessionDirectoryRule> _sessionDirectoryRules;

    private KimiCodeSourceLayout(
        string agentId,
        string instanceKind,
        string displayName,
        bool keepInstanceWhenMissing,
        bool useWorkDirectoryAsProject,
        params SessionDirectoryRule[] sessionDirectoryRules)
    {
        AgentId = agentId;
        InstanceKind = instanceKind;
        DisplayName = displayName;
        KeepInstanceWhenMissing = keepInstanceWhenMissing;
        UseWorkDirectoryAsProject = useWorkDirectoryAsProject;
        _sessionDirectoryRules = sessionDirectoryRules;
    }

    public static KimiCodeSourceLayout Cli { get; } = new(
        "kimi-code",
        "cli",
        "Kimi Code CLI (Windows)",
        keepInstanceWhenMissing: true,
        useWorkDirectoryAsProject: true,
        new SessionDirectoryRule("session_", StripPrefix: true));

    public static KimiCodeSourceLayout DesktopWork { get; } = new(
        "kimi-work",
        "desktop-work",
        "Kimi Work Desktop (Windows)",
        keepInstanceWhenMissing: false,
        useWorkDirectoryAsProject: false,
        new SessionDirectoryRule("conv-", StripPrefix: false),
        new SessionDirectoryRule("ctitle-", StripPrefix: false));

    public string AgentId { get; }

    public string InstanceKind { get; }

    public string DisplayName { get; }

    public bool KeepInstanceWhenMissing { get; }

    public bool UseWorkDirectoryAsProject { get; }

    public string InstanceId(string kimiHome) =>
        KimiCodeSourceIdentity.InstanceId(kimiHome, InstanceKind);

    public bool TryGetRootSessionId(
        string directoryName,
        out string rootSessionId)
    {
        ArgumentNullException.ThrowIfNull(directoryName);
        foreach (SessionDirectoryRule rule in _sessionDirectoryRules)
        {
            if (!directoryName.StartsWith(rule.Prefix, StringComparison.Ordinal) ||
                directoryName.Length <= rule.Prefix.Length)
            {
                continue;
            }

            rootSessionId = rule.StripPrefix
                ? directoryName[rule.Prefix.Length..]
                : directoryName;
            return true;
        }

        rootSessionId = string.Empty;
        return false;
    }

    private sealed record SessionDirectoryRule(
        string Prefix,
        bool StripPrefix);
}
