using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed record KimiCodeEntityMetadata(
    string RootSessionId,
    string SessionId,
    SessionKind SessionKind,
    string? DirectParentSessionId,
    SessionRelationOrigin RelationOrigin,
    SessionRelationState RelationState,
    SessionRole SessionRole,
    string AgentPathHash,
    string AgentLeafHash,
    string? ProjectId,
    string? ProjectPath,
    string? ProjectRepositoryIdentityHash,
    string? SessionName,
    DateTimeOffset? SessionNameUpdatedAtUtc);

public sealed record KimiCodeEntityMetadataResult(
    KimiCodeEntityMetadata? Metadata,
    CollectorDiagnostic? Diagnostic);

public sealed class KimiCodeEntityMetadataReader
{
    private const long MaxStateBytes = 2 * 1024 * 1024;
    private const int MaxIdentityCharacters = 1024;

    private readonly KimiCodeSourceLayout _layout;

    public KimiCodeEntityMetadataReader()
        : this(KimiCodeSourceLayout.Cli)
    {
    }

    internal KimiCodeEntityMetadataReader(KimiCodeSourceLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public async Task<KimiCodeEntityMetadataResult> ReadAsync(
        string kimiHome,
        string wirePath,
        string? sourceEntityId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryResolvePath(
                    kimiHome,
                    wirePath,
                    out string rootSessionId,
                    out string agentId,
                    out string statePath))
            {
                return Invalid(
                    sourceEntityId,
                    "kimi_code.invalid_entity_path",
                    "A Kimi Code wire path did not match the documented session layout.");
            }

            var stateInfo = new FileInfo(statePath);
            if (!stateInfo.Exists ||
                (File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0 ||
                stateInfo.Length is <= 0 or > MaxStateBytes)
            {
                return Invalid(
                    sourceEntityId,
                    "kimi_code.invalid_session_state",
                    "Kimi Code session metadata was missing or exceeded its safe size limit.");
            }

            byte[] stateBytes = await BoundedFileReader.ReadAllBytesAsync(
                statePath,
                checked((int)MaxStateBytes),
                cancellationToken);
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    stateBytes,
                    new JsonDocumentOptions { MaxDepth = 12 });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("agents", out JsonElement agents) ||
                    agents.ValueKind != JsonValueKind.Object ||
                    !agents.TryGetProperty(agentId, out JsonElement agent) ||
                    agent.ValueKind != JsonValueKind.Object)
                {
                    return Invalid(
                        sourceEntityId,
                        "kimi_code.invalid_session_state",
                        "Kimi Code session metadata did not identify the wire Agent.");
                }

                CodexProjectIdentity? project = null;
                if (_layout.UseWorkDirectoryAsProject)
                {
                    if (!TryReadProjectIdentity(
                            root,
                            out CodexProjectIdentity resolvedProject))
                    {
                        return Invalid(
                            sourceEntityId,
                            "kimi_code.invalid_project_identity",
                            "Kimi Code session metadata did not provide a reliable work directory.");
                    }

                    project = resolvedProject;
                }

                string? agentType = KimiCodeTextNormalizer.ReadBoundedString(
                    agent,
                    "type",
                    64);
                string? parentAgentId = KimiCodeTextNormalizer.ReadBoundedString(
                    agent,
                    "parentAgentId",
                    MaxIdentityCharacters);
                bool isMain = string.Equals(
                        agentType,
                        "main",
                        StringComparison.Ordinal) &&
                    string.Equals(agentId, "main", StringComparison.Ordinal);
                bool isSubagent = string.Equals(
                    agentType,
                    "sub",
                    StringComparison.Ordinal) &&
                    parentAgentId is not null &&
                    agents.TryGetProperty(
                        parentAgentId,
                        out JsonElement parentAgent) &&
                    parentAgent.ValueKind == JsonValueKind.Object;
                if (!isMain && !isSubagent)
                {
                    return Invalid(
                        sourceEntityId,
                        "kimi_code.invalid_agent_relation",
                        "Kimi Code session metadata did not provide a reliable Agent role or parent.");
                }

                string sessionId = KimiCodeSourceIdentity.AgentSessionId(
                    rootSessionId,
                    agentId);
                string? parentSessionId = isSubagent
                    ? KimiCodeSourceIdentity.AgentSessionId(
                        rootSessionId,
                        parentAgentId!)
                    : null;
                if (string.Equals(sessionId, parentSessionId, StringComparison.Ordinal))
                {
                    return Invalid(
                        sourceEntityId,
                        "kimi_code.invalid_agent_relation",
                        "Kimi Code session metadata contained a self-parent relation.");
                }

                string? title = KimiCodeTextNormalizer.Normalize(
                    KimiCodeTextNormalizer.ReadBoundedString(
                        root,
                        "title",
                        MaxIdentityCharacters));
                DateTimeOffset? nameUpdatedAt = ReadUnixMilliseconds(
                    root,
                    "updatedAt") ?? ReadUnixMilliseconds(root, "createdAt");
                return new KimiCodeEntityMetadataResult(
                    new KimiCodeEntityMetadata(
                        rootSessionId,
                        sessionId,
                        isMain ? SessionKind.Primary : SessionKind.Side,
                        parentSessionId,
                        isMain
                            ? SessionRelationOrigin.None
                            : SessionRelationOrigin.SourceAgentParent,
                        isMain
                            ? SessionRelationState.None
                            : SessionRelationState.Confirmed,
                        isMain ? SessionRole.Main : SessionRole.Subagent,
                        KimiCodeSourceIdentity.HashIdentity(
                            "kimi-code-agent-path",
                            agentId),
                        KimiCodeSourceIdentity.HashIdentity(
                            "kimi-code-agent-leaf",
                            agentId),
                        project?.ProjectId,
                        project?.ProjectPath,
                        project?.RepositoryIdentityHash,
                        isMain ? title : null,
                        isMain ? nameUpdatedAt : null),
                    null);
            }
            finally
            {
                Array.Clear(stateBytes);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            return Invalid(
                sourceEntityId,
                "kimi_code.invalid_session_state",
                "Kimi Code session metadata could not be read safely.");
        }
    }

    private static bool TryReadProjectIdentity(
        JsonElement root,
        out CodexProjectIdentity project)
    {
        project = default;
        bool hasWorkDirectory = root.TryGetProperty("workDir", out _);
        bool hasCurrentDirectory = root.TryGetProperty("cwd", out _);
        string? workDirectory = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "workDir",
            CodexProjectIdentity.MaxProjectPathCharacters);
        string? currentDirectory = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "cwd",
            CodexProjectIdentity.MaxProjectPathCharacters);
        if ((hasWorkDirectory && workDirectory is null) ||
            (hasCurrentDirectory && currentDirectory is null) ||
            (!hasWorkDirectory && !hasCurrentDirectory))
        {
            return false;
        }

        CodexProjectIdentity? workProject = null;
        if (workDirectory is not null)
        {
            if (!CodexProjectIdentity.TryCreate(
                    workDirectory,
                    out CodexProjectIdentity resolvedWorkProject))
            {
                return false;
            }

            workProject = resolvedWorkProject;
        }

        CodexProjectIdentity? currentProject = null;
        if (currentDirectory is not null)
        {
            if (!CodexProjectIdentity.TryCreate(
                    currentDirectory,
                    out CodexProjectIdentity resolvedCurrentProject))
            {
                return false;
            }

            currentProject = resolvedCurrentProject;
        }

        if (workProject is not null &&
            currentProject is not null &&
            !string.Equals(
                workProject.Value.ProjectPath,
                currentProject.Value.ProjectPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        project = (currentProject ?? workProject)!.Value;
        return true;
    }

    private bool TryResolvePath(
        string kimiHome,
        string wirePath,
        out string rootSessionId,
        out string agentId,
        out string statePath)
    {
        rootSessionId = agentId = statePath = string.Empty;
        string normalizedHome = KimiCodeSourceIdentity.NormalizePath(kimiHome);
        string normalizedWire = KimiCodeSourceIdentity.NormalizePath(wirePath);
        string relative = Path.GetRelativePath(normalizedHome, normalizedWire);
        string[] components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 6 ||
            !string.Equals(components[0], "sessions", StringComparison.OrdinalIgnoreCase) ||
            !_layout.TryGetRootSessionId(components[2], out rootSessionId) ||
            !string.Equals(components[3], "agents", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(components[5], "wire.jsonl", StringComparison.OrdinalIgnoreCase) ||
            components[2].Length > MaxIdentityCharacters ||
            components[4].Length is <= 0 or > MaxIdentityCharacters ||
            components[2].Any(char.IsControl) ||
            components[4].Any(char.IsControl))
        {
            return false;
        }

        agentId = components[4];
        statePath = Path.Combine(
            normalizedHome,
            components[0],
            components[1],
            components[2],
            "state.json");
        return true;
    }

    private static DateTimeOffset? ReadUnixMilliseconds(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out long milliseconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static KimiCodeEntityMetadataResult Invalid(
        string? sourceEntityId,
        string code,
        string message) => new(
        null,
        new CollectorDiagnostic(code, message, sourceEntityId));
}
