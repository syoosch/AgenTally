using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.QwenCode;

public sealed record QwenCodeEventContext(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string SourceFingerprint,
    DateTimeOffset ImportedAtUtc,
    string ExpectedSessionId);

public sealed record QwenCodeParseResult(
    UsageEvent? Event,
    QwenCodeParseState State,
    CollectorDiagnostic? Diagnostic)
{
    public UsageSessionMetadata? Session { get; init; }

    public UsageTurnMetadata? Turn { get; init; }

    public IReadOnlyList<UsageEventToolMetadata> EventTools { get; init; } = [];
}

// Field selection and Token semantics were cross-checked against tokscale
// 4.8.1 and ccusage 20.0.19. Qwen reports cache inside prompt tokens and
// thinking inside candidate tokens; AgenTally removes those overlaps exactly.
public sealed class QwenCodeJsonlParser
{
    public const string CurrentParserVersion = "qwen-code-jsonl-v1";
    private const int MaxIdentityCharacters = 1024;

    public QwenCodeParseResult ParseLine(
        JsonlLine line,
        QwenCodeParseState state,
        QwenCodeEventContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                line.Utf8,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(state, context, line, "qwen-code.invalid_json_record",
                    "A Qwen Code JSONL record was not an object.");
            }

            string? type = ReadString(root, "type", 64);
            if (type is not ("user" or "assistant"))
            {
                return new QwenCodeParseResult(null, UpdateProject(root, state), null);
            }

            string? sessionId = ReadString(root, "sessionId", MaxIdentityCharacters);
            if (!string.Equals(sessionId, context.ExpectedSessionId, StringComparison.Ordinal))
            {
                return Invalid(state, context, line, "qwen-code.invalid_session_identity",
                    "A Qwen Code record did not belong to its chat file.");
            }

            DateTimeOffset? timestamp = ReadTimestamp(root);
            if (!timestamp.HasValue)
            {
                return Invalid(state, context, line, "qwen-code.invalid_timestamp",
                    "A Qwen Code record had no supported UTC timestamp.");
            }

            QwenCodeParseState next = UpdateProject(root, state with
            {
                SessionId = sessionId,
                LastTimestampUtc = timestamp
            });
            return type == "user"
                ? ParseUser(root, line, next, context, timestamp.Value)
                : ParseAssistant(root, line, next, context, timestamp.Value);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or OverflowException or ArgumentException)
        {
            return Invalid(state, context, line, "qwen-code.invalid_usage_record",
                "A Qwen Code JSONL record contained invalid structural data.");
        }
    }

    private static QwenCodeParseResult ParseUser(
        JsonElement root,
        JsonlLine line,
        QwenCodeParseState state,
        QwenCodeEventContext context,
        DateTimeOffset timestamp)
    {
        string? uuid = ReadString(root, "uuid", MaxIdentityCharacters);
        if (uuid is null)
        {
            return Invalid(state, context, line, "qwen-code.invalid_prompt_record",
                "A Qwen Code user record had no stable identity.");
        }

        string? preview = null;
        if (root.TryGetProperty("message", out JsonElement message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("parts", out JsonElement parts))
        {
            preview = ReadPromptPreview(parts);
        }
        string turnHash = QwenCodeSourceIdentity.HashIdentity(
            "qwen-code-turn",
            $"{state.SessionId}\0{uuid}");
        QwenCodeParseState next = state with
        {
            TurnIdHash = turnHash,
            TurnStartedAtUtc = timestamp,
            PromptPreview = preview
        };
        return new QwenCodeParseResult(null, next, null)
        {
            Session = CreateSession(next, context, timestamp),
            Turn = CreateTurn(next, context, null)
        };
    }

    private static QwenCodeParseResult ParseAssistant(
        JsonElement root,
        JsonlLine line,
        QwenCodeParseState state,
        QwenCodeEventContext context,
        DateTimeOffset timestamp)
    {
        if (!root.TryGetProperty("usageMetadata", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return new QwenCodeParseResult(null, state, null);
        }

        string? uuid = ReadString(root, "uuid", MaxIdentityCharacters);
        long prompt = RequiredCounter(usage, "promptTokenCount");
        long candidates = RequiredCounter(usage, "candidatesTokenCount");
        long thoughts = OptionalCounter(usage, "thoughtsTokenCount");
        long cached = OptionalCounter(usage, "cachedContentTokenCount");
        long total = RequiredCounter(usage, "totalTokenCount");
        if (uuid is null || cached > prompt || thoughts > candidates ||
            checked(prompt + candidates) != total)
        {
            return Invalid(state, context, line, "qwen-code.invalid_usage_record",
                "A Qwen Code usage record had inconsistent Token counters or no identity.");
        }

        string modelValue = ReadString(root, "model", 512) ?? "unknown";
        var model = new ModelIdentity
        {
            RawModel = modelValue,
            NormalizedModel = modelValue,
            ResolutionOrigin = modelValue == "unknown"
                ? ModelResolutionOrigin.Unknown
                : ModelResolutionOrigin.LogConfirmed
        };
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(prompt),
            UncachedInput = TokenMetric.Exact(prompt - cached),
            CacheRead = TokenMetric.Exact(cached),
            CacheWrite = TokenMetric.Unavailable,
            Output = TokenMetric.Exact(candidates - thoughts),
            Reasoning = TokenMetric.Exact(thoughts),
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Exact(total),
            NormalizedTotal = TokenMetric.Exact(total),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Included
        };
        string dedup = QwenCodeSourceIdentity.HashIdentity(
            "qwen-code-call",
            $"{state.SessionId}\0{uuid}");
        var usageEvent = new UsageEvent(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            $"qwen-code-call:{dedup[..32]}",
            dedup,
            context.Instance.SourceKind,
            timestamp,
            context.ImportedAtUtc.ToUniversalTime(),
            model,
            tokens,
            CompletionState.Finalized,
            DataQuality.Exact,
            CurrentParserVersion,
            context.SourceFingerprint,
            line.LineNumber)
        {
            SessionId = state.SessionId,
            TurnIdHash = state.TurnIdHash,
            ProjectId = state.ProjectId,
            ProjectPath = state.ProjectPath,
            ProjectRepositoryIdentityHash = state.ProjectRepositoryIdentityHash
        };

        IReadOnlyList<UsageEventToolMetadata> tools = ReadToolNames(root)
            .Select((name, index) => new UsageEventToolMetadata(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                dedup,
                index,
                name,
                CurrentParserVersion))
            .ToArray();
        return new QwenCodeParseResult(usageEvent, state, null)
        {
            Session = CreateSession(state, context, timestamp),
            Turn = CreateTurn(state, context, timestamp),
            EventTools = tools
        };
    }

    private static UsageSessionMetadata CreateSession(
        QwenCodeParseState state,
        QwenCodeEventContext context,
        DateTimeOffset observedAtUtc) => new(
        context.Instance.AgentId,
        context.Instance.SourceInstanceId,
        context.Entity.SourceEntityId,
        state.SessionId ?? context.ExpectedSessionId,
        SessionKind.Primary,
        null,
        null,
        SessionRelationOrigin.None,
        SessionRelationState.None,
        ReplayState.Active,
        CompatibilityLevel.PartiallyCompatible,
        observedAtUtc,
        CurrentParserVersion)
    {
        ProjectId = state.ProjectId,
        ProjectPath = state.ProjectPath,
        ProjectRepositoryIdentityHash = state.ProjectRepositoryIdentityHash,
        SessionRole = SessionRole.Main,
        SessionName = state.PromptPreview,
        SessionNameUpdatedAtUtc = state.PromptPreview is null ? null : state.TurnStartedAtUtc
    };

    private static UsageTurnMetadata? CreateTurn(
        QwenCodeParseState state,
        QwenCodeEventContext context,
        DateTimeOffset? completedAtUtc)
    {
        if (state.SessionId is null || state.TurnIdHash is null ||
            !state.TurnStartedAtUtc.HasValue)
        {
            return null;
        }
        return new UsageTurnMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.SessionId,
            state.TurnIdHash,
            state.TurnStartedAtUtc.Value,
            completedAtUtc >= state.TurnStartedAtUtc ? completedAtUtc : null,
            state.PromptPreview,
            1,
            CurrentParserVersion);
    }

    private static QwenCodeParseState UpdateProject(
        JsonElement root,
        QwenCodeParseState state)
    {
        string? cwd = ReadString(root, "cwd", CodexProjectIdentity.MaxProjectPathCharacters);
        return cwd is not null && CodexProjectIdentity.TryCreate(cwd, out CodexProjectIdentity project)
            ? state with
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash
            }
            : state;
    }

    private static IReadOnlyList<string> ReadToolNames(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var names = new List<string>();
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("functionCall", out JsonElement call) ||
                call.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? name = ReadString(call, "name", 128);
            if (name is not null && names.Count < 256)
            {
                names.Add(name);
            }
        }
        return names;
    }

    private static string? ReadPromptPreview(JsonElement parts)
    {
        if (parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var source = new StringBuilder(256);
        foreach (JsonElement part in parts.EnumerateArray())
        {
            string? text = part.ValueKind == JsonValueKind.String
                ? part.GetString()
                : part.ValueKind == JsonValueKind.Object &&
                  part.TryGetProperty("text", out JsonElement value) &&
                  value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (source.Length > 0)
            {
                source.Append(' ');
            }
            source.Append(text);
        }
        return NormalizePreview(source.ToString());
    }

    internal static string? NormalizePreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var result = new StringBuilder(160);
        bool pendingSpace = false;
        int scalars = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace && scalars < 120)
            {
                result.Append(' ');
                scalars++;
            }
            pendingSpace = false;
            if (scalars >= 120)
            {
                break;
            }
            result.Append(rune.ToString());
            scalars++;
        }
        return result.Length == 0 ? null : result.ToString();
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        string? value = ReadString(root, "timestamp", 128);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
    }

    private static string? ReadString(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? result = property.GetString();
        return result is { Length: > 0 } && result.Length <= maximum &&
               !string.IsNullOrWhiteSpace(result) && !result.Any(char.IsControl)
            ? result
            : null;
    }

    private static long RequiredCounter(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) || result < 0)
        {
            throw new InvalidDataException("A Qwen Code Token counter was invalid.");
        }
        return result;
    }

    private static long OptionalCounter(JsonElement usage, string name) =>
        !usage.TryGetProperty(name, out JsonElement value) ||
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? 0
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) && result >= 0
                ? result
                : throw new InvalidDataException("A Qwen Code Token counter was invalid.");

    private static QwenCodeParseResult Invalid(
        QwenCodeParseState state,
        QwenCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        null,
        state,
        new CollectorDiagnostic(code, message, context.Entity.SourceEntityId, line.ByteOffset));
}
