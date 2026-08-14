using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.WorkBuddy;

public sealed record WorkBuddyEventContext(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string SourceFingerprint,
    DateTimeOffset ImportedAtUtc,
    string ExpectedSessionId);

public sealed record WorkBuddyParseResult(
    UsageEvent? Event,
    WorkBuddyParseState State,
    CollectorDiagnostic? Diagnostic)
{
    public UsageSessionMetadata? SessionMetadata { get; init; }

    public UsageTurnMetadata? TurnMetadata { get; init; }

    public IReadOnlyList<UsageEventToolMetadata> EventTools { get; init; } = [];
}

// WorkBuddy source selection and field discovery were cross-checked against
// tokscale 4.8.1's MIT-licensed WorkBuddy/Tencent Buddy parser. AgenTally keeps
// a native bounded parser and corrects WorkBuddy's inclusive cache/reasoning
// semantics against its own reported total instead of adding overlaps twice.
public sealed class WorkBuddyJsonlParser
{
    public const string CurrentParserVersion = "workbuddy-jsonl-v3";

    private const int MaxIdentityCharacters = 1024;
    private const int MaxToolsPerCall = 256;
    private const int MaxTurnRecords = 1024;

    public WorkBuddyParseResult ParseLine(
        JsonlLine line,
        WorkBuddyParseState state,
        WorkBuddyEventContext context)
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
                return Invalid(
                    state,
                    context,
                    line,
                    "workbuddy.invalid_json_record",
                    "A WorkBuddy JSONL record was not an object.");
            }

            string? type = WorkBuddyTextNormalizer.ReadBoundedString(
                root,
                "type",
                64);
            string? sessionId = ResolveSessionId(root, state, context);
            if (sessionId is null)
            {
                return Invalid(
                    state,
                    context,
                    line,
                    "workbuddy.invalid_session_identity",
                    "A WorkBuddy record did not belong to the session file.");
            }

            DateTimeOffset? timestamp = ReadTimestamp(root);
            WorkBuddyParseState next = UpdateCommonState(
                root,
                state with
                {
                    SessionId = sessionId,
                    LastTimestampUtc = timestamp ?? state.LastTimestampUtc
                });

            string? role = WorkBuddyTextNormalizer.ReadBoundedString(
                root,
                "role",
                64);
            if (string.Equals(type, "message", StringComparison.Ordinal) &&
                string.Equals(role, "user", StringComparison.Ordinal))
            {
                return ParseUserMessage(root, line, next, context, timestamp);
            }

            bool belongsToCurrentTurn = TryAdvanceCurrentTurnChain(
                root,
                next,
                out next);

            if (string.Equals(type, "ai-title", StringComparison.Ordinal))
            {
                string? title = WorkBuddyTextNormalizer.Normalize(
                    WorkBuddyTextNormalizer.ReadBoundedString(
                        root,
                        "aiTitle",
                        4096));
                if (title is null)
                {
                    return new WorkBuddyParseResult(null, next, null);
                }

                next = next with
                {
                    SessionName = title,
                    SessionNameUpdatedAtUtc = timestamp ??
                        next.LastTimestampUtc ??
                        context.ImportedAtUtc.ToUniversalTime()
                };
                return new WorkBuddyParseResult(null, next, null)
                {
                    SessionMetadata = CreateSession(next, context)
                };
            }

            bool isAssistant =
                string.Equals(type, "message", StringComparison.Ordinal) &&
                string.Equals(role, "assistant", StringComparison.Ordinal);
            bool isFunctionCall = string.Equals(
                type,
                "function_call",
                StringComparison.Ordinal);
            if (!isAssistant && !isFunctionCall)
            {
                return new WorkBuddyParseResult(null, next, null);
            }

            WorkBuddyToolReference? currentTool = null;
            if (isFunctionCall && TryReadTool(root, out currentTool) &&
                belongsToCurrentTurn)
            {
                next = next with
                {
                    PendingTools = AddTool(next.PendingTools ?? [], currentTool!)
                };
            }

            string? status = WorkBuddyTextNormalizer.ReadBoundedString(
                root,
                "status",
                64);
            if (status is not null &&
                !string.Equals(status, "completed", StringComparison.Ordinal))
            {
                return new WorkBuddyParseResult(null, next, null);
            }

            WorkBuddyUsageRead usage = ReadUsage(root);
            if (!usage.HasAnyUsage)
            {
                return new WorkBuddyParseResult(null, next, null);
            }

            if (!usage.IsValid || timestamp is null)
            {
                return Invalid(
                    next,
                    context,
                    line,
                    "workbuddy.invalid_usage_record",
                    "A WorkBuddy usage record had an unsupported Token shape or timestamp.");
            }

            string? sourceIdentity = ReadSourceIdentity(root);
            if (sourceIdentity is null)
            {
                return Invalid(
                    next,
                    context,
                    line,
                    "workbuddy.invalid_usage_identity",
                    "A WorkBuddy usage record had no stable call identity.");
            }

            ModelIdentity model = WorkBuddyModelIdentityResolver.Resolve(root);
            string dedupKey = WorkBuddySourceIdentity.HashIdentity(
                "workbuddy-model-call",
                $"{sessionId}\0{sourceIdentity}");
            var usageEvent = new UsageEvent(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                $"workbuddy-call:{dedupKey[..32]}",
                dedupKey,
                context.Instance.SourceKind,
                timestamp.Value,
                context.ImportedAtUtc.ToUniversalTime(),
                model,
                usage.Tokens!,
                string.Equals(status, "completed", StringComparison.Ordinal)
                    ? CompletionState.Finalized
                    : CompletionState.Completed,
                DataQuality.Exact,
                CurrentParserVersion,
                context.SourceFingerprint,
                line.LineNumber)
            {
                SessionId = sessionId,
                TurnIdHash = belongsToCurrentTurn
                    ? next.CurrentTurnIdHash
                    : null,
                ProjectId = next.ProjectId,
                ProjectPath = next.ProjectPath,
                ProjectRepositoryIdentityHash =
                    next.ProjectRepositoryIdentityHash
            };
            IReadOnlyList<WorkBuddyToolReference> toolsForEvent =
                belongsToCurrentTurn
                    ? next.PendingTools ?? []
                    : currentTool is null
                        ? []
                        : [currentTool];
            IReadOnlyList<UsageEventToolMetadata> eventTools = toolsForEvent
                .Select((value, index) => new UsageEventToolMetadata(
                    context.Instance.AgentId,
                    context.Instance.SourceInstanceId,
                    context.Entity.SourceEntityId,
                    dedupKey,
                    index,
                    value.Name,
                    CurrentParserVersion))
                .ToArray();
            if (belongsToCurrentTurn)
            {
                next = next with { PendingTools = null };
            }

            return new WorkBuddyParseResult(usageEvent, next, null)
            {
                SessionMetadata = CreateSession(next, context),
                TurnMetadata = belongsToCurrentTurn
                    ? CreateTurn(next, context, timestamp)
                    : null,
                EventTools = eventTools
            };
        }
        catch (Exception exception)
            when (exception is JsonException
                or InvalidDataException
                or OverflowException
                or ArgumentException)
        {
            return Invalid(
                state,
                context,
                line,
                "workbuddy.invalid_usage_record",
                "A WorkBuddy JSONL record contained invalid structural data.");
        }
    }

    private static WorkBuddyParseResult ParseUserMessage(
        JsonElement root,
        JsonlLine line,
        WorkBuddyParseState state,
        WorkBuddyEventContext context,
        DateTimeOffset? timestamp)
    {
        string? messageId = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "id",
            MaxIdentityCharacters);
        if (messageId is null || timestamp is null)
        {
            return Invalid(
                state,
                context,
                line,
                "workbuddy.invalid_prompt_record",
                "A WorkBuddy user message had no stable identity or timestamp.");
        }

        WorkBuddyPromptRead prompt = root.TryGetProperty(
                "content",
                out JsonElement content)
            ? WorkBuddyTextNormalizer.ReadPromptPreview(content)
            : new WorkBuddyPromptRead(null, false);
        if (prompt.IsInternalContinuation)
        {
            TryAdvanceCurrentTurnChain(
                root,
                state,
                out WorkBuddyParseState continuationState);
            return new WorkBuddyParseResult(null, continuationState, null);
        }

        string turnIdHash = WorkBuddySourceIdentity.HashIdentity(
            "workbuddy-turn",
            $"{state.SessionId}\0{messageId}");
        WorkBuddyParseState next = state with
        {
            CurrentTurnIdHash = turnIdHash,
            CurrentTurnStartedAtUtc = timestamp,
            CurrentPromptPreview = prompt.Preview,
            CurrentUserMessageCount = 1,
            CurrentTurnRecordIdHashes =
            [
                WorkBuddySourceIdentity.HashIdentity(
                    "workbuddy-history-item",
                    messageId)
            ],
            PendingTools = null,
            SessionName = state.SessionName ?? prompt.Preview,
            SessionNameUpdatedAtUtc = state.SessionNameUpdatedAtUtc ??
                (prompt.Preview is null ? null : timestamp)
        };
        return new WorkBuddyParseResult(null, next, null)
        {
            SessionMetadata = CreateSession(next, context),
            TurnMetadata = CreateTurn(next, context, completedAtUtc: null)
        };
    }

    private static bool TryAdvanceCurrentTurnChain(
        JsonElement root,
        WorkBuddyParseState state,
        out WorkBuddyParseState next)
    {
        next = state;
        IReadOnlyList<string> current = state.CurrentTurnRecordIdHashes ?? [];
        if (current.Count == 0)
        {
            return false;
        }

        string? recordId = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "id",
            MaxIdentityCharacters);
        string? parentId = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "parentId",
            MaxIdentityCharacters);
        if (recordId is null || parentId is null)
        {
            return false;
        }

        string parentHash = WorkBuddySourceIdentity.HashIdentity(
            "workbuddy-history-item",
            parentId);
        if (!current.Contains(parentHash, StringComparer.Ordinal))
        {
            return false;
        }

        string recordHash = WorkBuddySourceIdentity.HashIdentity(
            "workbuddy-history-item",
            recordId);
        if (current.Contains(recordHash, StringComparer.Ordinal))
        {
            return true;
        }

        if (current.Count >= MaxTurnRecords)
        {
            throw new InvalidDataException(
                "A WorkBuddy turn exceeded its bounded record chain.");
        }

        next = state with
        {
            CurrentTurnRecordIdHashes = [.. current, recordHash]
        };
        return true;
    }

    private static WorkBuddyParseState UpdateCommonState(
        JsonElement root,
        WorkBuddyParseState state)
    {
        string? cwd = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "cwd",
            CodexProjectIdentity.MaxProjectPathCharacters);
        if (cwd is null ||
            !CodexProjectIdentity.TryCreate(cwd, out CodexProjectIdentity project))
        {
            return state;
        }

        return state with
        {
            ProjectId = project.ProjectId,
            ProjectPath = project.ProjectPath,
            ProjectRepositoryIdentityHash = project.RepositoryIdentityHash
        };
    }

    private static UsageSessionMetadata CreateSession(
        WorkBuddyParseState state,
        WorkBuddyEventContext context)
    {
        string sessionId = state.SessionId ?? context.ExpectedSessionId;
        DateTimeOffset observedAtUtc = state.LastTimestampUtc ??
            context.ImportedAtUtc.ToUniversalTime();
        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            sessionId,
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
            SessionName = state.SessionName,
            SessionNameUpdatedAtUtc = state.SessionNameUpdatedAtUtc
        };
    }

    private static UsageTurnMetadata? CreateTurn(
        WorkBuddyParseState state,
        WorkBuddyEventContext context,
        DateTimeOffset? completedAtUtc)
    {
        if (state.SessionId is null ||
            state.CurrentTurnIdHash is null ||
            !state.CurrentTurnStartedAtUtc.HasValue)
        {
            return null;
        }

        DateTimeOffset? completed = completedAtUtc >=
            state.CurrentTurnStartedAtUtc.Value
                ? completedAtUtc
                : null;
        return new UsageTurnMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.SessionId,
            state.CurrentTurnIdHash,
            state.CurrentTurnStartedAtUtc.Value,
            completed,
            state.CurrentPromptPreview,
            state.CurrentUserMessageCount,
            CurrentParserVersion);
    }

    private static WorkBuddyUsageRead ReadUsage(JsonElement root)
    {
        JsonElement providerData = ObjectProperty(root, "providerData");
        JsonElement providerUsage = ObjectProperty(providerData, "usage");
        JsonElement rawUsage = ObjectProperty(providerData, "rawUsage");
        JsonElement message = ObjectProperty(root, "message");
        JsonElement messageUsage = ObjectProperty(message, "usage");
        bool hasAnyUsage = providerUsage.ValueKind == JsonValueKind.Object ||
            rawUsage.ValueKind == JsonValueKind.Object ||
            messageUsage.ValueKind == JsonValueKind.Object;
        if (!hasAnyUsage)
        {
            return WorkBuddyUsageRead.None;
        }

        try
        {
            long? input = FirstPresent(
                ReadCounter(providerUsage, "inputTokens"),
                ReadCounter(messageUsage, "input_tokens"),
                ReadCounter(rawUsage, "prompt_tokens"),
                ReadCounter(rawUsage, "input_tokens"),
                ReadCounter(providerUsage, "cachedMissTokens"),
                ReadCounter(providerUsage, "cacheMissTokens"),
                ReadCounter(rawUsage, "cachedMissTokens"),
                ReadCounter(rawUsage, "cacheMissTokens"));
            long? output = FirstPresent(
                ReadCounter(providerUsage, "outputTokens"),
                ReadCounter(messageUsage, "output_tokens"),
                ReadCounter(rawUsage, "completion_tokens"),
                ReadCounter(rawUsage, "output_tokens"));
            long? reportedTotal = FirstPresent(
                ReadCounter(providerUsage, "totalTokens"),
                ReadCounter(messageUsage, "total_tokens"),
                ReadCounter(rawUsage, "total_tokens"));
            if (!input.HasValue || !output.HasValue || !reportedTotal.HasValue)
            {
                return WorkBuddyUsageRead.Invalid;
            }

            long cacheRead = FirstPresent(
                    ReadCounter(rawUsage, "prompt_cache_hit_tokens"),
                    ReadCounter(rawUsage, "cache_read_input_tokens"),
                    ReadCounter(rawUsage, "cached_tokens"),
                    ReadCounter(messageUsage, "cache_read_input_tokens"),
                    ReadDetailCounter(providerUsage, "inputTokensDetails", "cached_tokens"),
                    ReadCounter(providerUsage, "cacheReadInputTokens"),
                    ReadCounter(providerUsage, "cacheTokens")) ?? 0;
            long cacheWrite = FirstPresent(
                    ReadCounter(rawUsage, "prompt_cache_write_tokens"),
                    ReadCounter(rawUsage, "cache_creation_input_tokens"),
                    ReadCounter(messageUsage, "cache_creation_input_tokens"),
                    ReadCounter(providerUsage, "cacheCreationInputTokens"),
                    ReadCounter(providerUsage, "cachedWriteTokens")) ?? 0;
            long? explicitMiss = FirstPresent(
                ReadCounter(rawUsage, "prompt_cache_miss_tokens"),
                ReadCounter(rawUsage, "cachedMissTokens"),
                ReadCounter(rawUsage, "cacheMissTokens"),
                ReadCounter(providerUsage, "cachedMissTokens"),
                ReadCounter(providerUsage, "cacheMissTokens"));
            long reasoning = FirstPresent(
                    ReadCounter(rawUsage, "completion_thinking_tokens"),
                    ReadDetailCounter(
                        rawUsage,
                        "completion_tokens_details",
                        "reasoning_tokens"),
                    ReadDetailCounter(
                        providerUsage,
                        "outputTokensDetails",
                        "reasoning_tokens"),
                    ReadCounter(providerUsage, "reasoningTokens")) ?? 0;
            return MapUsage(
                input.Value,
                output.Value,
                cacheRead,
                cacheWrite,
                reasoning,
                reportedTotal.Value,
                explicitMiss);
        }
        catch (Exception exception)
            when (exception is InvalidDataException or OverflowException)
        {
            return WorkBuddyUsageRead.Invalid;
        }
    }

    private static WorkBuddyUsageRead MapUsage(
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long reasoning,
        long reportedTotal,
        long? explicitMiss)
    {
        long cache = checked(cacheRead + cacheWrite);
        var candidates = new List<WorkBuddyMappedUsage>(4);
        IEnumerable<bool?> cacheModes = cache == 0
            ? [null]
            : [true, false];
        IEnumerable<bool?> reasoningModes = reasoning == 0
            ? [null]
            : [true, false];
        foreach (bool? cacheIncluded in cacheModes)
        {
            if (cacheIncluded is true && cache > input)
            {
                continue;
            }

            long uncachedInput = cacheIncluded is true
                ? input - cache
                : input;
            if (explicitMiss.HasValue && explicitMiss.Value != uncachedInput)
            {
                continue;
            }

            foreach (bool? reasoningIncluded in reasoningModes)
            {
                if (reasoningIncluded is true && reasoning > output)
                {
                    continue;
                }

                long visibleOutput = reasoningIncluded is true
                    ? output - reasoning
                    : output;
                long normalizedTotal = checked(
                    uncachedInput +
                    cacheRead +
                    cacheWrite +
                    visibleOutput +
                    reasoning);
                if (normalizedTotal != reportedTotal)
                {
                    continue;
                }

                candidates.Add(new WorkBuddyMappedUsage(
                    uncachedInput,
                    visibleOutput,
                    cacheIncluded switch
                    {
                        true => MetricInclusion.Included,
                        false => MetricInclusion.Separate,
                        null => MetricInclusion.Unknown
                    },
                    reasoningIncluded switch
                    {
                        true => MetricInclusion.Included,
                        false => MetricInclusion.Separate,
                        null => MetricInclusion.Unknown
                    }));
            }
        }

        WorkBuddyMappedUsage[] distinct = candidates.Distinct().ToArray();
        if (distinct.Length != 1)
        {
            return WorkBuddyUsageRead.Invalid;
        }

        WorkBuddyMappedUsage mapped = distinct[0];
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(input),
            UncachedInput = TokenMetric.Exact(mapped.UncachedInput),
            CacheRead = TokenMetric.Exact(cacheRead),
            CacheWrite = TokenMetric.Exact(cacheWrite),
            Output = TokenMetric.Exact(mapped.Output),
            Reasoning = TokenMetric.Exact(reasoning),
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Exact(reportedTotal),
            NormalizedTotal = TokenMetric.Exact(reportedTotal),
            CacheIncludedInInput = mapped.CacheInclusion,
            ReasoningIncludedInOutput = mapped.ReasoningInclusion
        };
        return new WorkBuddyUsageRead(true, true, tokens);
    }

    private static long? ReadCounter(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out long result) ||
            result < 0)
        {
            throw new InvalidDataException("A WorkBuddy Token counter was invalid.");
        }

        return result;
    }

    private static long? ReadDetailCounter(
        JsonElement usage,
        string detailsProperty,
        string counterProperty)
    {
        if (usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty(detailsProperty, out JsonElement details) ||
            details.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (details.ValueKind == JsonValueKind.Object)
        {
            return ReadCounter(details, counterProperty);
        }

        if (details.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("WorkBuddy Token details were invalid.");
        }

        long total = 0;
        bool found = false;
        foreach (JsonElement item in details.EnumerateArray())
        {
            long? value = ReadCounter(item, counterProperty);
            if (!value.HasValue)
            {
                continue;
            }

            total = checked(total + value.Value);
            found = true;
        }

        return found ? total : null;
    }

    private static string? ResolveSessionId(
        JsonElement root,
        WorkBuddyParseState state,
        WorkBuddyEventContext context)
    {
        string? source = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "sessionId",
            MaxIdentityCharacters) ?? state.SessionId ?? context.ExpectedSessionId;
        return string.Equals(
                source,
                context.ExpectedSessionId,
                StringComparison.Ordinal)
            ? source
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        long? value = ReadCounter(root, "timestamp");
        if (!value.HasValue || value.Value <= 0)
        {
            return null;
        }

        long milliseconds = value.Value > 10_000_000_000
            ? value.Value
            : checked(value.Value * 1000);
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadSourceIdentity(JsonElement root)
    {
        JsonElement providerData = ObjectProperty(root, "providerData");
        return WorkBuddyTextNormalizer.ReadBoundedString(
                   providerData,
                   "messageId",
                   MaxIdentityCharacters) ??
               WorkBuddyTextNormalizer.ReadBoundedString(
                   providerData,
                   "traceId",
                   MaxIdentityCharacters) ??
               WorkBuddyTextNormalizer.ReadBoundedString(
                   root,
                   "id",
                   MaxIdentityCharacters);
    }

    private static bool TryReadTool(
        JsonElement root,
        out WorkBuddyToolReference? tool)
    {
        tool = null;
        string? name = WorkBuddyTextNormalizer.ReadBoundedString(
            root,
            "name",
            128);
        string? callId = WorkBuddyTextNormalizer.ReadBoundedString(
                root,
                "callId",
                MaxIdentityCharacters) ??
            WorkBuddyTextNormalizer.ReadBoundedString(
                root,
                "id",
                MaxIdentityCharacters);
        if (name is null || callId is null)
        {
            return false;
        }

        tool = new WorkBuddyToolReference(
            WorkBuddySourceIdentity.HashIdentity("workbuddy-tool-call", callId),
            name);
        return true;
    }

    private static WorkBuddyToolReference[] AddTool(
        IReadOnlyList<WorkBuddyToolReference> tools,
        WorkBuddyToolReference value)
    {
        if (tools.Any(existing => string.Equals(
                existing.CallIdHash,
                value.CallIdHash,
                StringComparison.Ordinal)))
        {
            return tools.ToArray();
        }

        if (tools.Count >= MaxToolsPerCall)
        {
            throw new InvalidDataException(
                "A WorkBuddy call exceeded the bounded tool count.");
        }

        return [.. tools, value];
    }

    private static JsonElement ObjectProperty(
        JsonElement value,
        string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.Object
            ? property
            : default;

    private static long? FirstPresent(params long?[] values) =>
        values.FirstOrDefault(static value => value.HasValue);

    private static WorkBuddyParseResult Invalid(
        WorkBuddyParseState state,
        WorkBuddyEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        null,
        state,
        new CollectorDiagnostic(
            code,
            message,
            context.Entity.SourceEntityId,
            line.ByteOffset));

    private sealed record WorkBuddyMappedUsage(
        long UncachedInput,
        long Output,
        MetricInclusion CacheInclusion,
        MetricInclusion ReasoningInclusion);

    private sealed record WorkBuddyUsageRead(
        bool HasAnyUsage,
        bool IsValid,
        TokenUsage? Tokens)
    {
        public static WorkBuddyUsageRead None { get; } = new(false, true, null);

        public static WorkBuddyUsageRead Invalid { get; } = new(true, false, null);
    }
}
