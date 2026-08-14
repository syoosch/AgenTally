using System.Text;
using System.Text.Json;
using System.Globalization;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed record ClaudeCodeEventContext(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string SourceFingerprint,
    DateTimeOffset ImportedAtUtc);

public sealed record ClaudeCodeParseResult(
    UsageEvent? Event,
    UsageSessionMetadata? SessionMetadata,
    UsageTurnMetadata? TurnMetadata,
    IReadOnlyList<UsageEventToolMetadata> EventTools,
    ClaudeCodeParseState State,
    CollectorDiagnostic? Diagnostic);

public sealed record ClaudeCodeTranscriptParserOptions(
    string ParserVersion,
    string? AcceptedEntrypoint,
    bool CapturePromptTurns,
    CompatibilityLevel MinimumCompatibility)
{
    public static ClaudeCodeTranscriptParserOptions Cli { get; } = new(
        ClaudeCodeTranscriptParser.CurrentParserVersion,
        "cli",
        CapturePromptTurns: true,
        CompatibilityLevel.FullyCompatible);

    public static ClaudeCodeTranscriptParserOptions DesktopLocalAgent { get; } = new(
        "claude-code-desktop-local-agent-v1",
        AcceptedEntrypoint: null,
        CapturePromptTurns: false,
        CompatibilityLevel.PartiallyCompatible);
}

public sealed class ClaudeCodeTranscriptParser
{
    public const string CurrentParserVersion = "claude-code-cli-v1";

    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 128;
    private const int MaxSessionNameCharacters = 4_096;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private readonly ClaudeCodeTranscriptParserOptions _options;

    public ClaudeCodeTranscriptParser(
        ClaudeCodeTranscriptParserOptions? options = null)
    {
        _options = options ?? ClaudeCodeTranscriptParserOptions.Cli;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ParserVersion);
    }

    public string ParserVersion => _options.ParserVersion;

    public ClaudeCodeParseResult ParseLine(
        JsonlLine line,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.Utf8, DocumentOptions);
        }
        catch (JsonException)
        {
            return Empty(
                state,
                Diagnostic(context, line, "claude_code.invalid_json",
                    "A Claude Code transcript line was not valid JSON."));
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Empty(
                    state,
                    Diagnostic(context, line, "claude_code.invalid_record",
                        "A Claude Code transcript record was not an object."));
            }

            ClaudeCodeParseState nextState = UpdateCommonState(root, state);
            string? type = ReadBoundedString(root, "type", 64);
            if (type is null)
            {
                return Empty(nextState, null);
            }

            return type switch
            {
                "user" when !_options.CapturePromptTurns =>
                    IgnoreUser(nextState, context),
                "user" => ParseUser(root, line, nextState, context),
                "assistant" => ParseAssistant(root, line, nextState, context),
                "ai-title" => ParseTitle(root, line, nextState, context),
                _ => Empty(nextState, null)
            };
        }
    }

    private ClaudeCodeParseResult ParseUser(
        JsonElement root,
        JsonlLine line,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context)
    {
        UsageSessionMetadata? session = CreateSession(
            state,
            context,
            Compatibility(state, hasReliableTurn: false));
        if (!TryGetMessage(root, "user", out JsonElement message))
        {
            return new ClaudeCodeParseResult(
                null,
                session,
                null,
                [],
                state,
                Diagnostic(context, line, "claude_code.invalid_user_record",
                    "A Claude Code user record did not contain a valid message."));
        }

        string? promptId = ReadBoundedString(
            root,
            "promptId",
            MaxIdentityCharacters);
        if (promptId is null)
        {
            return new ClaudeCodeParseResult(
                null,
                CreateSession(
                    state,
                    context,
                    CompatibilityLevel.PartiallyCompatible),
                null,
                [],
                state,
                null);
        }

        string turnIdHash = ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-prompt",
            promptId);
        bool sidechain = ReadBoolean(root, "isSidechain");
        bool directPrompt = IsDirectPrompt(message);
        bool sameTurn = string.Equals(
            state.CurrentTurnIdHash,
            turnIdHash,
            StringComparison.Ordinal);
        DateTimeOffset startedAtUtc = sameTurn &&
            state.CurrentTurnStartedAtUtc.HasValue
                ? state.CurrentTurnStartedAtUtc.Value
                : state.LastTimestampUtc ?? context.ImportedAtUtc.ToUniversalTime();
        int userMessageCount = sameTurn
            ? state.CurrentUserMessageCount
            : 0;
        if (directPrompt && !sidechain)
        {
            userMessageCount = checked(userMessageCount + 1);
        }

        string? preview = sameTurn
            ? state.CurrentPromptPreview
            : null;
        if (preview is null && directPrompt && !sidechain)
        {
            preview = BuildPromptPreview(message);
        }

        ClaudeCodeParseState nextState = state with
        {
            CurrentTurnIdHash = turnIdHash,
            CurrentTurnStartedAtUtc = startedAtUtc,
            CurrentPromptPreview = preview,
            CurrentUserMessageCount = userMessageCount
        };
        UsageTurnMetadata? turn = nextState.SessionId is null
            ? null
            : new UsageTurnMetadata(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                nextState.SessionId,
                turnIdHash,
                startedAtUtc,
                null,
                preview,
                userMessageCount,
                ParserVersion);

        return new ClaudeCodeParseResult(
            null,
            CreateSession(
                nextState,
                context,
                Compatibility(nextState, hasReliableTurn: true)),
            turn,
            [],
            nextState,
            null);
    }

    private ClaudeCodeParseResult ParseAssistant(
        JsonElement root,
        JsonlLine line,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context)
    {
        if (!TryGetMessage(root, "assistant", out JsonElement message))
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_assistant_record",
                "A Claude Code assistant record did not contain a valid message.");
        }

        string? entrypoint = ReadBoundedString(root, "entrypoint", 64);
        if (_options.AcceptedEntrypoint is not null &&
            entrypoint is not null &&
            !string.Equals(
                entrypoint,
                _options.AcceptedEntrypoint,
                StringComparison.Ordinal))
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.unsupported_entrypoint",
                "A Claude Code transcript entrypoint is not supported by this collector.");
        }

        string? model = ReadBoundedString(
            message,
            "model",
            MaxModelCharacters);
        if (string.Equals(model, "<synthetic>", StringComparison.Ordinal) &&
            (root.TryGetProperty("error", out _) ||
             message.TryGetProperty("error", out _)))
        {
            return new ClaudeCodeParseResult(
                null,
                CreateSession(
                    state,
                    context,
                    Compatibility(
                        state,
                        state.CurrentTurnIdHash is not null)),
                null,
                [],
                state,
                null);
        }

        string? messageId = ReadBoundedString(
            message,
            "id",
            MaxIdentityCharacters);
        if (messageId is null || model is null || state.LastTimestampUtc is null)
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_call_identity",
                "A Claude Code assistant record did not provide reliable call identity, model, or time.");
        }

        if (!TryReadUsage(
                message,
                out long input,
                out long cacheRead,
                out long cacheWrite,
                out long output,
                out long totalInput,
                out long normalizedTotal))
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_usage",
                "A Claude Code assistant record did not provide reliable Token usage.");
        }

        bool terminal = output > 0 ||
            ReadBoundedString(message, "stop_reason", 64) is not null ||
            HasNonNullProperty(message, "stop_details");
        string dedupKey = ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-message",
            messageId);
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(totalInput),
            UncachedInput = TokenMetric.Exact(input),
            CacheRead = TokenMetric.Exact(cacheRead),
            CacheWrite = TokenMetric.Exact(cacheWrite),
            Output = TokenMetric.Exact(output),
            Reasoning = TokenMetric.Unavailable,
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Unavailable,
            NormalizedTotal = TokenMetric.Exact(normalizedTotal),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Unknown
        };
        var usageEvent = new UsageEvent(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            $"{context.Entity.SourceEntityId}:line:{line.LineNumber}",
            dedupKey,
            context.Instance.SourceKind,
            state.LastTimestampUtc.Value,
            context.ImportedAtUtc.ToUniversalTime(),
            new ModelIdentity
            {
                RawModel = model,
                NormalizedModel = model.Trim().ToLowerInvariant(),
                ProviderId = null,
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            tokens,
            terminal ? CompletionState.Completed : CompletionState.Partial,
            DataQuality.Exact,
            ParserVersion,
            context.SourceFingerprint,
            line.LineNumber)
        {
            SessionId = state.SessionId,
            TurnIdHash = state.CurrentTurnIdHash,
            ProjectId = state.ProjectId,
            ProjectPath = state.ProjectPath,
            ProjectRepositoryIdentityHash =
                state.ProjectRepositoryIdentityHash
        };

        UsageTurnMetadata? turn = terminal &&
            state.SessionId is not null &&
            state.CurrentTurnIdHash is not null &&
            state.CurrentTurnStartedAtUtc.HasValue
                ? new UsageTurnMetadata(
                    context.Instance.AgentId,
                    context.Instance.SourceInstanceId,
                    context.Entity.SourceEntityId,
                    state.SessionId,
                    state.CurrentTurnIdHash,
                    state.CurrentTurnStartedAtUtc.Value,
                    state.LastTimestampUtc,
                    state.CurrentPromptPreview,
                    state.CurrentUserMessageCount,
                    ParserVersion)
                : null;

        return new ClaudeCodeParseResult(
            usageEvent,
            CreateSession(
                state,
                context,
                Compatibility(
                    state,
                    state.CurrentTurnIdHash is not null)),
            turn,
            ReadTools(message, context, dedupKey),
            state,
            null);
    }

    private ClaudeCodeParseResult ParseTitle(
        JsonElement root,
        JsonlLine line,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context)
    {
        string? title = NormalizeSessionName(
            ReadBoundedString(
                root,
                "aiTitle",
                MaxSessionNameCharacters));
        if (title is null)
        {
            return Empty(
                state,
                Diagnostic(context, line, "claude_code.invalid_session_title",
                    "A Claude Code session title was invalid."));
        }

        UsageSessionMetadata? session = CreateSession(
            state,
            context,
            Compatibility(
                state,
                state.CurrentTurnIdHash is not null),
            title,
            state.LastTimestampUtc);
        return new ClaudeCodeParseResult(
            null,
            session,
            null,
            [],
            state,
            null);
    }

    private static ClaudeCodeParseState UpdateCommonState(
        JsonElement root,
        ClaudeCodeParseState state)
    {
        DateTimeOffset? timestamp = TryReadTimestamp(root, "timestamp");
        string? sessionId = ReadBoundedString(
            root,
            "sessionId",
            MaxIdentityCharacters);
        ClaudeCodeParseState next = state with
        {
            SessionId = sessionId ?? state.SessionId,
            LastTimestampUtc = timestamp ?? state.LastTimestampUtc
        };

        string? cwd = ReadBoundedString(
            root,
            "cwd",
            CodexProjectIdentity.MaxProjectPathCharacters);
        if (cwd is not null &&
            CodexProjectIdentity.TryCreate(
                cwd,
                out CodexProjectIdentity project))
        {
            next = next with
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash
            };
        }

        return next;
    }

    private ClaudeCodeParseResult IgnoreUser(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context) => new(
        null,
        CreateSession(state, context, CompatibilityLevel.PartiallyCompatible),
        null,
        [],
        state,
        null);

    private UsageSessionMetadata? CreateSession(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        CompatibilityLevel compatibilityLevel,
        string? sessionName = null,
        DateTimeOffset? sessionNameUpdatedAtUtc = null)
    {
        if (state.SessionId is null)
        {
            return null;
        }

        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.SessionId,
            SessionKind.Primary,
            null,
            null,
            SessionRelationOrigin.None,
            SessionRelationState.None,
            ReplayState.Active,
            compatibilityLevel,
            state.LastTimestampUtc ?? context.ImportedAtUtc.ToUniversalTime(),
            ParserVersion)
        {
            ProjectId = state.ProjectId,
            ProjectPath = state.ProjectPath,
            ProjectRepositoryIdentityHash =
                state.ProjectRepositoryIdentityHash,
            SessionRole = SessionRole.Main,
            SessionName = sessionName,
            SessionNameUpdatedAtUtc = sessionNameUpdatedAtUtc
        };
    }

    private CompatibilityLevel Compatibility(
        ClaudeCodeParseState state,
        bool hasReliableTurn)
    {
        CompatibilityLevel observed =
            state.ProjectId is null || !hasReliableTurn
            ? CompatibilityLevel.PartiallyCompatible
            : CompatibilityLevel.FullyCompatible;
        return (CompatibilityLevel)Math.Max(
            (int)observed,
            (int)_options.MinimumCompatibility);
    }

    private ClaudeCodeParseResult Incompatible(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        null,
        CreateSession(
            state,
            context,
            CompatibilityLevel.TemporarilyIncompatible),
        null,
        [],
        state,
        Diagnostic(context, line, code, message));

    private static ClaudeCodeParseResult Empty(
        ClaudeCodeParseState state,
        CollectorDiagnostic? diagnostic) => new(
        null,
        null,
        null,
        [],
        state,
        diagnostic);

    private static bool TryReadUsage(
        JsonElement message,
        out long input,
        out long cacheRead,
        out long cacheWrite,
        out long output,
        out long totalInput,
        out long normalizedTotal)
    {
        input = cacheRead = cacheWrite = output = totalInput = normalizedTotal = 0;
        if (!message.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !TryReadNonnegativeInt64(usage, "input_tokens", out input) ||
            !TryReadNonnegativeInt64(
                usage,
                "cache_read_input_tokens",
                out cacheRead) ||
            !TryReadNonnegativeInt64(
                usage,
                "cache_creation_input_tokens",
                out cacheWrite) ||
            !TryReadNonnegativeInt64(usage, "output_tokens", out output))
        {
            return false;
        }

        try
        {
            totalInput = checked(input + cacheRead + cacheWrite);
            normalizedTotal = checked(totalInput + output);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadNonnegativeInt64(
        JsonElement value,
        string propertyName,
        out long result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out result) &&
               result >= 0;
    }

    private IReadOnlyList<UsageEventToolMetadata> ReadTools(
        JsonElement message,
        ClaudeCodeEventContext context,
        string dedupKey)
    {
        if (!message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tools = new List<UsageEventToolMetadata>();
        var ordinals = new HashSet<int>();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    ReadBoundedString(block, "type", 64),
                    "tool_use",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string? toolId = ReadBoundedString(
                block,
                "id",
                MaxIdentityCharacters);
            string? toolName = ReadBoundedString(block, "name", 128);
            if (toolId is null || toolName is null)
            {
                continue;
            }

            int ordinal = ClaudeCodeSourceIdentity.StableOrdinal(toolId);
            if (!ordinals.Add(ordinal))
            {
                continue;
            }

            tools.Add(new UsageEventToolMetadata(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                dedupKey,
                ordinal,
                toolName,
                ParserVersion));
        }

        return tools;
    }

    private static bool TryGetMessage(
        JsonElement root,
        string expectedRole,
        out JsonElement message)
    {
        if (root.TryGetProperty("message", out message) &&
            message.ValueKind == JsonValueKind.Object &&
            string.Equals(
                ReadBoundedString(message, "role", 32),
                expectedRole,
                StringComparison.Ordinal))
        {
            return true;
        }

        message = default;
        return false;
    }

    private static bool IsDirectPrompt(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(content.GetString());
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool hasPromptContent = false;
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = ReadBoundedString(block, "type", 64);
            if (string.Equals(type, "tool_result", StringComparison.Ordinal))
            {
                return false;
            }

            hasPromptContent |= type switch
            {
                "text" => block.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(text.GetString()),
                "image" or "audio" => true,
                _ => false
            };
        }

        return hasPromptContent;
    }

    private static string? BuildPromptPreview(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            return null;
        }

        var source = new StringBuilder(256);
        if (content.ValueKind == JsonValueKind.String)
        {
            source.Append(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? type = ReadBoundedString(block, "type", 64);
                string? promptPart = type switch
                {
                    "text" when block.TryGetProperty("text", out JsonElement text) &&
                        text.ValueKind == JsonValueKind.String => text.GetString(),
                    "image" => "[图片]",
                    "audio" => "[音频]",
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(promptPart))
                {
                    continue;
                }

                if (source.Length > 0)
                {
                    source.Append(' ');
                }

                source.Append(promptPart);
            }
        }

        return NormalizePreview(source.ToString());
    }

    private static string? NormalizePreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maximumScalars = 120;
        var normalized = new StringBuilder(maximumScalars);
        bool pendingSpace = false;
        int scalarCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) ||
                category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (scalarCount + 1 >= maximumScalars)
                {
                    break;
                }

                normalized.Append(' ');
                scalarCount++;
            }

            pendingSpace = false;
            if (scalarCount >= maximumScalars)
            {
                break;
            }

            normalized.Append(rune.ToString());
            scalarCount++;
        }

        return normalized.Length == 0 ? null : normalized.ToString();
    }

    private static string? NormalizeSessionName(string? value)
    {
        return NormalizePreview(value);
    }

    private static string? ReadBoundedString(
        JsonElement value,
        string propertyName,
        int maximumCharacters)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? result = property.GetString();
        return result is { Length: > 0 } &&
               result.Length <= maximumCharacters &&
               !string.IsNullOrWhiteSpace(result) &&
               !result.Any(char.IsControl)
            ? result
            : null;
    }

    private static DateTimeOffset? TryReadTimestamp(
        JsonElement value,
        string propertyName)
    {
        string? raw = ReadBoundedString(value, propertyName, 128);
        return raw is not null &&
               DateTimeOffset.TryParse(
                   raw,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static bool ReadBoolean(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind is JsonValueKind.True;

    private static bool HasNonNullProperty(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static CollectorDiagnostic Diagnostic(
        ClaudeCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        code,
        message,
        context.Entity.SourceEntityId,
        line.ByteOffset);
}
