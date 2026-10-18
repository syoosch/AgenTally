using System.Globalization;
using System.Text;
using System.Text.Json;
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
    CollectorDiagnostic? Diagnostic)
{
    public IReadOnlyList<UsageSessionMetadata> RelatedSessions { get; init; } = [];

    public IReadOnlyList<UsageTurnDispatch> Dispatches { get; init; } = [];
}

public sealed record ClaudeCodeTranscriptParserOptions(
    string ParserVersion,
    IReadOnlySet<string> AcceptedEntrypoints,
    bool AllowMissingEntrypoint,
    bool CapturePromptTurns,
    CompatibilityLevel MinimumCompatibility)
{
    public static ClaudeCodeTranscriptParserOptions Cli { get; } = new(
        ClaudeCodeTranscriptParser.CurrentParserVersion,
        new HashSet<string>(["cli", "claude-desktop"], StringComparer.Ordinal),
        AllowMissingEntrypoint: false,
        CapturePromptTurns: true,
        CompatibilityLevel.FullyCompatible);

    public static ClaudeCodeTranscriptParserOptions DesktopLocalAgent { get; } = new(
        "claude-code-desktop-local-agent-v5",
        new HashSet<string>(["local-agent", "desktop-local-agent"], StringComparer.Ordinal),
        AllowMissingEntrypoint: true,
        CapturePromptTurns: true,
        CompatibilityLevel.FullyCompatible);
}

public sealed class ClaudeCodeTranscriptParser
{
    public const string CurrentParserVersion = "claude-code-v5";

    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 128;
    private const int MaxSessionNameCharacters = 4_096;
    private const int MaxCausalNodes = 50_000;
    private const int MaxTurns = 10_000;
    private const int MaxCalls = 20_000;
    private const int MaxAgentDispatches = 5_000;
    private const int MaxToolsPerCall = 10_000;
    private const int MaxAgentToolUseIdsPerDispatch = 1_024;

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
        ArgumentNullException.ThrowIfNull(_options.AcceptedEntrypoints);
        if (_options.AcceptedEntrypoints.Count == 0)
        {
            throw new ArgumentException(
                "At least one Claude Code entrypoint is required.",
                nameof(options));
        }
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

            string? type = ReadBoundedString(root, "type", 64);
            string? recordEntrypoint = null;
            if (type is "user" or "assistant")
            {
                bool missingEntrypoint =
                    !root.TryGetProperty(
                        "entrypoint",
                        out JsonElement entrypointProperty) ||
                    entrypointProperty.ValueKind is
                        JsonValueKind.Null or JsonValueKind.Undefined;
                recordEntrypoint = missingEntrypoint
                    ? null
                    : ReadBoundedString(root, "entrypoint", 64);
                string? recordSessionId = ReadBoundedString(
                    root,
                    "sessionId",
                    MaxIdentityCharacters);
                if ((missingEntrypoint && !_options.AllowMissingEntrypoint) ||
                    (!missingEntrypoint &&
                     (recordEntrypoint is null ||
                      !_options.AcceptedEntrypoints.Contains(recordEntrypoint))) ||
                    recordSessionId is null ||
                    TryReadTimestamp(root, "timestamp") is null)
                {
                    return Incompatible(
                        state,
                        context,
                        line,
                        "claude_code.invalid_record_identity",
                        "A Claude Code user or assistant record lacked a supported entrypoint, session, or timestamp.");
                }

                if (state.Entrypoint is not null &&
                    !string.Equals(
                        state.Entrypoint,
                        recordEntrypoint,
                        StringComparison.Ordinal))
                {
                    return Incompatible(
                        state,
                        context,
                        line,
                        "claude_code.invalid_entrypoint_identity",
                        "A Claude Code transcript file changed entrypoint identity.");
                }
            }

            CommonStateResult common = UpdateCommonState(
                root,
                state,
                context,
                recordEntrypoint,
                recognizedDialogueRecord: type is "user" or "assistant");
            if (common.Diagnostic is not null)
            {
                return Incompatible(
                    common.State,
                    context,
                    line,
                    common.Diagnostic.Code,
                    common.Diagnostic.Message);
            }

            ClaudeCodeParseState nextState = common.State;
            if (type is null)
            {
                return Empty(nextState, null);
            }

            CausalResult causal = UpdateCausalState(
                root,
                type,
                nextState,
                context,
                line);
            if (causal.Diagnostic?.Code.Contains(
                    ".invalid_",
                    StringComparison.Ordinal) is true)
            {
                return Incompatible(
                    causal.State,
                    context,
                    line,
                    causal.Diagnostic.Code,
                    causal.Diagnostic.Message);
            }

            ClaudeCodeParseResult result = type switch
            {
                "user" => ParseUser(root, line, causal, context),
                "assistant" => ParseAssistant(root, line, causal, context),
                "ai-title" => ParseTitle(
                    root,
                    line,
                    causal.State,
                    context,
                    "aiTitle",
                    custom: false),
                "custom-title" => ParseTitle(
                    root,
                    line,
                    causal.State,
                    context,
                    "customTitle",
                    custom: true),
                _ => Empty(causal.State, null)
            };
            return result.Diagnostic is null && causal.Diagnostic is not null
                ? result with { Diagnostic = causal.Diagnostic }
                : result;
        }
    }

    public IReadOnlyList<ClaudeCodeParseResult> FlushCompletedCalls(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        bool endOfFile = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        if (endOfFile && state.RecognizedRecordCount == 0)
        {
            return
            [
                Empty(
                    state,
                    new CollectorDiagnostic(
                        "claude_code.invalid_missing_entrypoint_records",
                        "A Claude Code transcript did not contain any supported user or assistant records.",
                        context.Entity.SourceEntityId))
            ];
        }

        if (state.Calls is null || state.Calls.Count == 0)
        {
            return [];
        }

        var results = new List<ClaudeCodeParseResult>();
        foreach (string dedupKey in state.Calls
                     .OrderBy(static value => value.Value.LastSourceRevision)
                     .ThenBy(static value => value.Key, StringComparer.Ordinal)
                     .Select(static value => value.Key)
                     .ToArray())
        {
            ClaudeCodeCallState call = state.Calls[dedupKey];
            if (call.InvalidDiagnosticCode is not null ||
                !call.HasUsage || !call.Terminal ||
                call.LastSourceRevision <= call.EmittedSourceRevision)
            {
                if (endOfFile && call.InvalidDiagnosticCode is null &&
                    call.HasUsage && !call.Terminal &&
                    call.LastSourceRevision > call.EmittedSourceRevision)
                {
                    results.Add(Empty(
                        state,
                        new CollectorDiagnostic(
                            "claude_code.incomplete_terminal_call",
                            "A Claude Code streamed call had no terminal final record and was not counted.",
                            context.Entity.SourceEntityId)));
                }

                continue;
            }

            if (!TryAddUsage(
                    call.Input,
                    call.CacheRead,
                    call.CacheWrite,
                    call.Output,
                    out long totalInput,
                    out long normalizedTotal))
            {
                state.Calls[dedupKey] = call with
                {
                    InvalidDiagnosticCode = "claude_code.invalid_stream_usage"
                };
                continue;
            }

            var tokens = new TokenUsage
            {
                InputReported = TokenMetric.Exact(totalInput),
                UncachedInput = TokenMetric.Exact(call.Input),
                CacheRead = TokenMetric.Exact(call.CacheRead),
                CacheWrite = TokenMetric.Exact(call.CacheWrite),
                Output = TokenMetric.Exact(call.Output),
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
                $"{context.Entity.SourceEntityId}:call:{dedupKey}",
                dedupKey,
                context.Instance.SourceKind,
                call.OccurredAtUtc,
                context.ImportedAtUtc.ToUniversalTime(),
                new ModelIdentity
                {
                    RawModel = call.Model,
                    NormalizedModel = call.Model.Trim().ToLowerInvariant(),
                    ProviderId = null,
                    ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
                },
                tokens,
                CompletionState.Completed,
                DataQuality.Exact,
                ParserVersion,
                context.SourceFingerprint,
                call.LastSourceRevision)
            {
                SessionId = call.SessionId,
                TurnIdHash = call.TurnIdHash,
                ProjectId = call.ProjectId,
                ProjectPath = call.ProjectPath,
                ProjectRepositoryIdentityHash =
                    call.ProjectRepositoryIdentityHash
            };

            UsageTurnMetadata? turn = CreateTurn(
                state,
                context,
                call.SessionId,
                call.TurnIdHash,
                call.OccurredAtUtc);
            IReadOnlyList<UsageEventToolMetadata> tools = call.Tools.Values
                .OrderBy(static value => value.Ordinal)
                .Select(value => new UsageEventToolMetadata(
                    context.Instance.AgentId,
                    context.Instance.SourceInstanceId,
                    context.Entity.SourceEntityId,
                    dedupKey,
                    value.Ordinal,
                    value.Name,
                    ParserVersion))
                .ToArray();
            state.Calls[dedupKey] = call with
            {
                EmittedSourceRevision = call.LastSourceRevision
            };
            results.Add(new ClaudeCodeParseResult(
                usageEvent,
                CreateSessionForCall(state, context, call),
                turn,
                tools,
                state,
                null));
        }

        return results;
    }

    private ClaudeCodeParseResult ParseUser(
        JsonElement root,
        JsonlLine line,
        CausalResult causal,
        ClaudeCodeEventContext context)
    {
        ClaudeCodeParseState state = causal.State;
        UsageSessionMetadata? session = CreateSession(
            state,
            context,
            Compatibility(state, causal.TurnIdHash is not null));
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

        UsageTurnMetadata? turn = causal.IsDirectPrompt
            ? CreateTurn(
                state,
                context,
                state.SessionId,
                causal.TurnIdHash,
                completedAtUtc: null)
            : null;
        ClaudeCodeParseResult result = new(
            null,
            session,
            turn,
            [],
            state,
            null);

        SubagentLinkResult link = TryCreateSubagentLink(
            root,
            message,
            state,
            context,
            line);
        if (link.Diagnostic is not null)
        {
            return result with { Diagnostic = link.Diagnostic };
        }

        return result with
        {
            RelatedSessions = link.Session is null ? [] : [link.Session],
            Dispatches = link.Dispatch is null ? [] : [link.Dispatch]
        };
    }

    private ClaudeCodeParseResult ParseAssistant(
        JsonElement root,
        JsonlLine line,
        CausalResult causal,
        ClaudeCodeEventContext context)
    {
        ClaudeCodeParseState state = causal.State;
        if (!TryGetMessage(root, "assistant", out JsonElement message))
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_assistant_record",
                "A Claude Code assistant record did not contain a valid message.");
        }

        string? model = ReadBoundedString(message, "model", MaxModelCharacters);
        if (string.Equals(model, "<synthetic>", StringComparison.Ordinal) &&
            (root.TryGetProperty("error", out _) ||
             message.TryGetProperty("error", out _)))
        {
            return Empty(state, null) with
            {
                SessionMetadata = CreateSession(
                    state,
                    context,
                    Compatibility(state, causal.TurnIdHash is not null))
            };
        }

        string? messageId = ReadBoundedString(
            message,
            "id",
            MaxIdentityCharacters);
        if (messageId is null || model is null || state.SessionId is null ||
            state.LastTimestampUtc is null)
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_call_identity",
                "A Claude Code assistant record did not provide reliable call identity, model, session, or time.");
        }

        string sessionId = state.SessionId;
        DateTimeOffset occurredAtUtc = state.LastTimestampUtc.Value;

        bool usageValid = TryReadUsage(
            message,
            out long input,
            out long cacheRead,
            out long cacheWrite,
            out long output);
        string dedupKey = CallIdentityHash(sessionId, messageId);
        Dictionary<string, ClaudeCodeCallState> calls = state.Calls ??
            new Dictionary<string, ClaudeCodeCallState>(StringComparer.Ordinal);
        state = state.Calls is null ? state with { Calls = calls } : state;
        if (!calls.TryGetValue(dedupKey, out ClaudeCodeCallState? existing) &&
            calls.Count >= MaxCalls)
        {
            return Incompatible(
                state,
                context,
                line,
                "claude_code.invalid_call_capacity",
                "A Claude Code transcript exceeded the bounded call index.");
        }

        Dictionary<string, ClaudeCodeToolState> tools = existing?.Tools ??
            new Dictionary<string, ClaudeCodeToolState>(StringComparer.Ordinal);
        int nextToolOrdinal = existing?.NextToolOrdinal ?? 0;
        ToolMergeResult merged = MergeTools(
            message,
            sessionId,
            tools,
            nextToolOrdinal);
        string? invalidDiagnosticCode = existing?.InvalidDiagnosticCode;
        if (invalidDiagnosticCode is null && !usageValid)
        {
            invalidDiagnosticCode = "claude_code.invalid_stream_usage";
        }

        invalidDiagnosticCode ??= merged.InvalidDiagnosticCode;
        if (invalidDiagnosticCode is null && existing is not null)
        {
            if (!string.Equals(existing.Model, model, StringComparison.Ordinal))
            {
                invalidDiagnosticCode =
                    "claude_code.invalid_stream_model_identity";
            }
            else if (input < existing.Input ||
                     cacheRead < existing.CacheRead ||
                     cacheWrite < existing.CacheWrite ||
                     output < existing.Output)
            {
                invalidDiagnosticCode =
                    "claude_code.invalid_stream_token_order";
            }
            else if (existing.TurnIdHash is not null &&
                     causal.TurnIdHash is not null &&
                     !string.Equals(
                         existing.TurnIdHash,
                         causal.TurnIdHash,
                         StringComparison.Ordinal))
            {
                invalidDiagnosticCode =
                    "claude_code.invalid_stream_prompt_identity";
            }
        }
        bool terminal =
            ReadBoundedString(message, "stop_reason", 64) is not null ||
            HasNonNullProperty(message, "stop_details");
        string? turnIdHash = existing?.TurnIdHash ?? causal.TurnIdHash;
        var call = new ClaudeCodeCallState(
            sessionId,
            existing?.Model ?? model,
            input,
            cacheRead,
            cacheWrite,
            output,
            usageValid,
            invalidDiagnosticCode,
            terminal,
            turnIdHash,
            occurredAtUtc,
            state.ProjectId,
            state.ProjectPath,
            state.ProjectRepositoryIdentityHash,
            line.LineNumber,
            existing?.EmittedSourceRevision ?? -1,
            merged.NextOrdinal,
            tools);
        calls[dedupKey] = call;

        if (merged.AgentToolUseIdHashes.Count > 0 &&
            causal.NodeHash is not null)
        {
            Dictionary<string, ClaudeCodeAgentDispatchState> dispatches =
                state.AgentDispatches ??
                new Dictionary<string, ClaudeCodeAgentDispatchState>(
                    StringComparer.Ordinal);
            if (!dispatches.ContainsKey(causal.NodeHash) &&
                dispatches.Count >= MaxAgentDispatches)
            {
                invalidDiagnosticCode ??=
                    "claude_code.invalid_stream_dispatch_capacity";
                call = call with
                {
                    InvalidDiagnosticCode = invalidDiagnosticCode
                };
                calls[dedupKey] = call;
            }
            else
            {
                string[] agentToolUseIdHashes = dispatches.TryGetValue(
                        causal.NodeHash,
                        out ClaudeCodeAgentDispatchState? existingDispatch)
                    ? existingDispatch.AgentToolUseIdHashes
                        .Concat(merged.AgentToolUseIdHashes)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : merged.AgentToolUseIdHashes.ToArray();
                if (agentToolUseIdHashes.Length >
                    MaxAgentToolUseIdsPerDispatch)
                {
                    invalidDiagnosticCode ??=
                        "claude_code.invalid_stream_dispatch_capacity";
                    call = call with
                    {
                        InvalidDiagnosticCode = invalidDiagnosticCode
                    };
                    calls[dedupKey] = call;
                }
                else
                {
                    dispatches[causal.NodeHash] =
                        new ClaudeCodeAgentDispatchState(
                            sessionId,
                            causal.TurnIdHash,
                            occurredAtUtc,
                            agentToolUseIdHashes);
                    if (state.AgentDispatches is null)
                    {
                        state = state with { AgentDispatches = dispatches };
                    }
                }
            }
        }

        if (invalidDiagnosticCode is not null)
        {
            return Incompatible(
                state,
                context,
                line,
                invalidDiagnosticCode,
                InvalidStreamDiagnosticMessage(invalidDiagnosticCode));
        }

        return new ClaudeCodeParseResult(
            null,
            CreateSession(
                state,
                context,
                Compatibility(state, causal.TurnIdHash is not null)),
            null,
            [],
            state,
            null);
    }

    private ClaudeCodeParseResult ParseTitle(
        JsonElement root,
        JsonlLine line,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        string propertyName,
        bool custom)
    {
        string? title = NormalizeSessionName(
            ReadBoundedString(root, propertyName, MaxSessionNameCharacters));
        if (title is null)
        {
            return Empty(
                state,
                Diagnostic(context, line, "claude_code.invalid_session_title",
                    "A Claude Code session title was invalid."));
        }

        if (!custom && state.HasCustomSessionName)
        {
            title = state.SessionName;
        }
        else
        {
            state = state with
            {
                SessionName = title,
                HasCustomSessionName = custom || state.HasCustomSessionName
            };
        }

        return new ClaudeCodeParseResult(
            null,
            CreateSession(
                state,
                context,
                Compatibility(
                    state,
                    state.SessionId is not null && state.Turns?.Count > 0),
                title),
            null,
            [],
            state,
            null);
    }

    private static CommonStateResult UpdateCommonState(
        JsonElement root,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        string? recordEntrypoint,
        bool recognizedDialogueRecord)
    {
        DateTimeOffset? timestamp = TryReadTimestamp(root, "timestamp");
        string? sourceSessionId = ReadBoundedString(
            root,
            "sessionId",
            MaxIdentityCharacters) ?? state.SourceSessionId;
        if (state.SourceSessionId is not null && sourceSessionId is not null &&
            !string.Equals(
                state.SourceSessionId,
                sourceSessionId,
                StringComparison.Ordinal))
        {
            return new CommonStateResult(
                state,
                new CollectorDiagnostic(
                    "claude_code.invalid_session_identity",
                    "A Claude Code transcript file changed session identity."));
        }

        bool subagentTranscript = IsSubagentTranscript(context);
        string? subagentHash = state.SubagentHash;
        if (subagentTranscript)
        {
            string? agentId = ReadBoundedString(
                root,
                "agentId",
                MaxIdentityCharacters);
            string? observedHash = agentId is null
                ? null
                : AgentIdentityHash(agentId);
            if (subagentHash is not null && observedHash is not null &&
                !string.Equals(subagentHash, observedHash, StringComparison.Ordinal))
            {
                return new CommonStateResult(
                    state,
                    new CollectorDiagnostic(
                        "claude_code.invalid_subagent_identity",
                        "A Claude Code subagent transcript changed agent identity."));
            }

            subagentHash ??= observedHash;
            if (subagentHash is null ||
                !ReadBoolean(root, "isSidechain"))
            {
                return new CommonStateResult(
                    state,
                    new CollectorDiagnostic(
                        "claude_code.invalid_subagent_identity",
                        "A Claude Code subagent transcript lacked its bounded sidechain identity."));
            }
        }

        string? sessionId = sourceSessionId;
        if (subagentTranscript && sourceSessionId is not null &&
            subagentHash is not null)
        {
            sessionId = SubagentSessionId(sourceSessionId, subagentHash);
        }

        ClaudeCodeParseState next = state with
        {
            SessionId = sessionId,
            SourceSessionId = sourceSessionId,
            Entrypoint = recordEntrypoint ?? state.Entrypoint,
            RecognizedRecordCount = recognizedDialogueRecord
                ? checked(state.RecognizedRecordCount + 1)
                : state.RecognizedRecordCount,
            SubagentHash = subagentHash,
            LastTimestampUtc = timestamp ?? state.LastTimestampUtc,
            NodeTurnIds = state.NodeTurnIds ??
                new Dictionary<string, string?>(StringComparer.Ordinal),
            Turns = state.Turns ??
                new Dictionary<string, ClaudeCodeTurnState>(StringComparer.Ordinal),
            Calls = state.Calls ??
                new Dictionary<string, ClaudeCodeCallState>(StringComparer.Ordinal),
            AgentDispatches = state.AgentDispatches ??
                new Dictionary<string, ClaudeCodeAgentDispatchState>(
                    StringComparer.Ordinal)
        };

        string? cwd = ReadBoundedString(
            root,
            "cwd",
            CodexProjectIdentity.MaxProjectPathCharacters);
        if (cwd is not null &&
            CodexProjectIdentity.TryCreate(cwd, out CodexProjectIdentity project))
        {
            next = next with
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash
            };
        }

        return new CommonStateResult(next, null);
    }

    private CausalResult UpdateCausalState(
        JsonElement root,
        string type,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        JsonlLine line)
    {
        bool causalRecord = type is "user" or "assistant" ||
            root.TryGetProperty("uuid", out _);
        if (!causalRecord || state.SourceSessionId is null)
        {
            return new CausalResult(state, null, null, false, null);
        }

        string? uuid = ReadBoundedString(root, "uuid", MaxIdentityCharacters);
        if (uuid is null)
        {
            CollectorDiagnostic? diagnostic = type is "user" or "assistant"
                ? Diagnostic(
                    context,
                    line,
                    "claude_code.causal_uuid_unavailable",
                    "A Claude Code record did not expose causal UUID metadata; usage remains available without Prompt attribution.")
                : null;
            return new CausalResult(
                state,
                state.SubagentHash is null
                    ? null
                    : EnsureSubagentTurn(state),
                null,
                false,
                diagnostic);
        }

        string nodeHash = NodeIdentityHash(state.SourceSessionId, uuid);
        Dictionary<string, string?> nodes = state.NodeTurnIds!;
        if (nodes.ContainsKey(nodeHash))
        {
            return InvalidCausal(
                state,
                nodeHash,
                context,
                line,
                "claude_code.invalid_duplicate_uuid",
                "A Claude Code transcript reused a causal UUID within one session.");
        }

        if (nodes.Count >= MaxCausalNodes)
        {
            return InvalidCausal(
                state,
                nodeHash,
                context,
                line,
                "claude_code.invalid_causal_graph_capacity",
                "A Claude Code transcript exceeded the bounded causal graph.");
        }

        string? inheritedTurn = null;
        bool hasParent = false;
        CollectorDiagnostic? causalDiagnostic = null;
        if (root.TryGetProperty("parentUuid", out JsonElement parent) &&
            parent.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (parent.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(parent.GetString()))
            {
                return InvalidCausal(
                    state,
                    nodeHash,
                    context,
                    line,
                    "claude_code.invalid_parent_uuid",
                    "A Claude Code causal parent UUID was invalid.");
            }

            string parentHash = NodeIdentityHash(
                state.SourceSessionId,
                parent.GetString()!);
            if (!nodes.TryGetValue(parentHash, out inheritedTurn))
            {
                causalDiagnostic = Diagnostic(
                    context,
                    line,
                    "claude_code.causal_parent_unavailable",
                    "A Claude Code causal parent was not retained in the transcript; usage remains available without Prompt attribution.");
            }

            hasParent = true;
        }

        bool directPrompt = false;
        string? turnIdHash = inheritedTurn;
        if (_options.CapturePromptTurns &&
            type == "user" &&
            state.SubagentHash is null &&
            !ReadBoolean(root, "isSidechain") &&
            !ReadBoolean(root, "isMeta") &&
            TryGetMessage(root, "user", out JsonElement message) &&
            IsDirectPrompt(message))
        {
            directPrompt = true;
            turnIdHash = ClaudeCodeSourceIdentity.HashIdentity(
                "claude-code-turn",
                $"{state.SourceSessionId}\0{uuid}");
            if (!state.Turns!.ContainsKey(turnIdHash))
            {
                if (state.Turns.Count >= MaxTurns)
                {
                    return InvalidCausal(
                        state,
                        nodeHash,
                        context,
                        line,
                        "claude_code.invalid_turn_capacity",
                        "A Claude Code transcript exceeded the bounded Prompt index.");
                }

                state.Turns[turnIdHash] = new ClaudeCodeTurnState(
                    state.LastTimestampUtc ??
                        context.ImportedAtUtc.ToUniversalTime(),
                    BuildPromptPreview(message),
                    1);
            }
        }
        else if (state.SubagentHash is not null)
        {
            turnIdHash ??= EnsureSubagentTurn(state);
        }

        if (!hasParent &&
            ((type == "assistant") ||
             (type == "user" && !directPrompt && state.SubagentHash is null)))
        {
            causalDiagnostic ??= Diagnostic(
                context,
                line,
                "claude_code.causal_parent_unavailable",
                "A Claude Code non-root record did not expose a retained causal parent; usage remains available without Prompt attribution.");
        }

        if (state.SubagentHash is null &&
            turnIdHash is null &&
            (type == "assistant" || (type == "user" && !directPrompt)))
        {
            causalDiagnostic ??= Diagnostic(
                context,
                line,
                "claude_code.prompt_attribution_unavailable",
                "A Claude Code record did not resolve to one direct user Prompt; usage remains available without guessed attribution.");
        }

        nodes.Add(nodeHash, turnIdHash);
        return new CausalResult(
            state,
            turnIdHash,
            nodeHash,
            directPrompt,
            causalDiagnostic);
    }

    private static CausalResult InvalidCausal(
        ClaudeCodeParseState state,
        string nodeHash,
        ClaudeCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        state,
        null,
        nodeHash,
        false,
        Diagnostic(context, line, code, message));

    private static string? EnsureSubagentTurn(ClaudeCodeParseState state)
    {
        if (state.SessionId is null || state.SubagentHash is null ||
            state.Turns is null)
        {
            return null;
        }

        string turnIdHash = ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-subagent-turn",
            state.SessionId);
        if (!state.Turns.ContainsKey(turnIdHash))
        {
            state.Turns[turnIdHash] = new ClaudeCodeTurnState(
                state.LastTimestampUtc ?? DateTimeOffset.UnixEpoch,
                null,
                0);
        }

        return turnIdHash;
    }

    private SubagentLinkResult TryCreateSubagentLink(
        JsonElement root,
        JsonElement message,
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        JsonlLine line)
    {
        string? sourceAssistantUuid = ReadBoundedString(
            root,
            "sourceToolAssistantUUID",
            MaxIdentityCharacters);
        string? childAgentId = null;
        bool hasToolUseResult = root.TryGetProperty(
            "toolUseResult",
            out JsonElement toolUseResult) &&
            toolUseResult.ValueKind == JsonValueKind.Object;
        if (hasToolUseResult)
        {
            childAgentId = ReadBoundedString(
                toolUseResult,
                "agentId",
                MaxIdentityCharacters);
        }

        if (childAgentId is null)
        {
            return SubagentLinkResult.Empty;
        }

        if (sourceAssistantUuid is null || childAgentId is null ||
            state.SourceSessionId is null || state.AgentDispatches is null)
        {
            return InvalidSubagentLink(
                context,
                line,
                "A Claude Code Agent result lacked the complete parent identity chain.");
        }

        string assistantNodeHash = NodeIdentityHash(
            state.SourceSessionId,
            sourceAssistantUuid);
        if (!state.AgentDispatches.TryGetValue(
                assistantNodeHash,
                out ClaudeCodeAgentDispatchState? parent))
        {
            return InvalidSubagentLink(
                context,
                line,
                "A Claude Code Agent result did not resolve to one parent assistant.");
        }

        string[] resultToolHashes = ReadToolResultIdHashes(
            message,
            parent.SessionId);
        string[] matches = resultToolHashes
            .Where(parent.AgentToolUseIdHashes.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1)
        {
            return InvalidSubagentLink(
                context,
                line,
                "A Claude Code Agent result did not match exactly one Agent tool call.");
        }

        string childHash = AgentIdentityHash(childAgentId);
        string childSessionId = SubagentSessionId(parent.SessionId, childHash);
        CompatibilityLevel compatibility =
            state.ProjectId is null
                ? CompatibilityLevel.PartiallyCompatible
                : CompatibilityLevel.FullyCompatible;
        var childSession = new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            childSessionId,
            SessionKind.Side,
            parent.SessionId,
            null,
            SessionRelationOrigin.SourceAgentParent,
            SessionRelationState.Confirmed,
            ReplayState.Active,
            compatibility,
            parent.OccurredAtUtc,
            ParserVersion)
        {
            ProjectId = state.ProjectId,
            ProjectPath = state.ProjectPath,
            ProjectRepositoryIdentityHash =
                state.ProjectRepositoryIdentityHash,
            SessionRole = SessionRole.Subagent,
            AgentPathHash = childHash,
            AgentLeafHash = childHash
        };
        UsageTurnDispatch? dispatch = parent.TurnIdHash is null
            ? null
            : new UsageTurnDispatch(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                parent.SessionId,
                parent.TurnIdHash,
                ClaudeCodeSourceIdentity.HashIdentity(
                    "claude-code-agent-dispatch",
                    $"{parent.SessionId}\0{matches[0]}"),
                childHash,
                TurnDispatchKind.Spawn,
                DispatchTargetKind.AgentLeaf,
                parent.OccurredAtUtc,
                ParserVersion);
        return new SubagentLinkResult(childSession, dispatch, null);
    }

    private static SubagentLinkResult InvalidSubagentLink(
        ClaudeCodeEventContext context,
        JsonlLine line,
        string message) => new(
        null,
        null,
        Diagnostic(
            context,
            line,
            "claude_code.invalid_subagent_relation",
            message));

    private static ToolMergeResult MergeTools(
        JsonElement message,
        string sessionId,
        Dictionary<string, ClaudeCodeToolState> tools,
        int nextOrdinal)
    {
        if (!message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return new ToolMergeResult(null, nextOrdinal, []);
        }

        string? invalidDiagnosticCode = null;
        var agentToolUseIdHashes = new HashSet<string>(StringComparer.Ordinal);
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
                invalidDiagnosticCode ??=
                    "claude_code.invalid_stream_tool_identity";
                continue;
            }

            string toolHash = ToolIdentityHash(sessionId, toolId);
            if (tools.TryGetValue(toolHash, out ClaudeCodeToolState? existing))
            {
                if (!string.Equals(
                        existing.Name,
                        toolName,
                        StringComparison.Ordinal))
                {
                    invalidDiagnosticCode ??=
                        "claude_code.invalid_stream_tool_identity";
                }
            }
            else
            {
                if (tools.Count >= MaxToolsPerCall)
                {
                    invalidDiagnosticCode ??=
                        "claude_code.invalid_stream_tool_capacity";
                    continue;
                }

                tools.Add(
                    toolHash,
                    new ClaudeCodeToolState(nextOrdinal, toolName));
                nextOrdinal = checked(nextOrdinal + 1);
            }

            if (string.Equals(toolName, "Agent", StringComparison.Ordinal))
            {
                if (!agentToolUseIdHashes.Contains(toolHash) &&
                    agentToolUseIdHashes.Count >=
                        MaxAgentToolUseIdsPerDispatch)
                {
                    invalidDiagnosticCode ??=
                        "claude_code.invalid_stream_dispatch_capacity";
                }
                else
                {
                    _ = agentToolUseIdHashes.Add(toolHash);
                }
            }
        }

        return new ToolMergeResult(
            invalidDiagnosticCode,
            nextOrdinal,
            agentToolUseIdHashes.ToArray());
    }

    private static string InvalidStreamDiagnosticMessage(string code) =>
        code switch
        {
            "claude_code.invalid_stream_usage" =>
                "A Claude Code call did not expose four bounded nonnegative Token counters.",
            "claude_code.invalid_stream_model_identity" =>
                "A streamed Claude Code call changed model identity.",
            "claude_code.invalid_stream_token_order" =>
                "A streamed Claude Code call decreased a Token counter.",
            "claude_code.invalid_stream_prompt_identity" =>
                "A streamed Claude Code call changed Prompt identity.",
            "claude_code.invalid_stream_tool_identity" =>
                "A streamed Claude Code call lacked stable tool identity.",
            "claude_code.invalid_stream_tool_capacity" =>
                "A streamed Claude Code call exceeded the bounded tool index.",
            "claude_code.invalid_stream_dispatch_capacity" =>
                "A streamed Claude Code call exceeded the bounded Agent dispatch index.",
            _ => throw new InvalidOperationException(
                "Unsupported Claude Code invalid-stream diagnostic.")
        };

    private static string[] ReadToolResultIdHashes(
        JsonElement message,
        string sessionId)
    {
        if (!message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hashes = new List<string>();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    ReadBoundedString(block, "type", 64),
                    "tool_result",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string? toolUseId = ReadBoundedString(
                block,
                "tool_use_id",
                MaxIdentityCharacters);
            if (toolUseId is not null)
            {
                hashes.Add(ToolIdentityHash(sessionId, toolUseId));
            }
        }

        return hashes.ToArray();
    }

    private UsageSessionMetadata? CreateSession(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        CompatibilityLevel compatibilityLevel,
        string? sessionName = null)
    {
        if (state.SessionId is null)
        {
            return null;
        }

        bool subagent = state.SubagentHash is not null;
        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.SessionId,
            subagent ? SessionKind.Side : SessionKind.Primary,
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
            SessionRole = subagent ? SessionRole.Subagent : SessionRole.Main,
            AgentPathHash = state.SubagentHash,
            AgentLeafHash = state.SubagentHash,
            SessionName = sessionName ?? state.SessionName,
            SessionNameUpdatedAtUtc = null
        };
    }

    private UsageSessionMetadata CreateSessionForCall(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        ClaudeCodeCallState call)
    {
        bool subagent = state.SubagentHash is not null;
        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            call.SessionId,
            subagent ? SessionKind.Side : SessionKind.Primary,
            null,
            null,
            SessionRelationOrigin.None,
            SessionRelationState.None,
            ReplayState.Active,
            call.ProjectId is null || call.TurnIdHash is null
                ? CompatibilityLevel.PartiallyCompatible
                : CompatibilityLevel.FullyCompatible,
            call.OccurredAtUtc,
            ParserVersion)
        {
            ProjectId = call.ProjectId,
            ProjectPath = call.ProjectPath,
            ProjectRepositoryIdentityHash =
                call.ProjectRepositoryIdentityHash,
            SessionRole = subagent ? SessionRole.Subagent : SessionRole.Main,
            AgentPathHash = state.SubagentHash,
            AgentLeafHash = state.SubagentHash
        };
    }

    private UsageTurnMetadata? CreateTurn(
        ClaudeCodeParseState state,
        ClaudeCodeEventContext context,
        string? sessionId,
        string? turnIdHash,
        DateTimeOffset? completedAtUtc)
    {
        if (sessionId is null || turnIdHash is null || state.Turns is null ||
            !state.Turns.TryGetValue(
                turnIdHash,
                out ClaudeCodeTurnState? turn))
        {
            return null;
        }

        return new UsageTurnMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            sessionId,
            turnIdHash,
            turn.StartedAtUtc,
            completedAtUtc,
            turn.PromptPreview,
            turn.UserMessageCount,
            ParserVersion);
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
        out long output)
    {
        input = cacheRead = cacheWrite = output = 0;
        return message.TryGetProperty("usage", out JsonElement usage) &&
               usage.ValueKind == JsonValueKind.Object &&
               TryReadNonnegativeInt64(usage, "input_tokens", out input) &&
               TryReadNonnegativeInt64(
                   usage,
                   "cache_read_input_tokens",
                   out cacheRead) &&
               TryReadNonnegativeInt64(
                   usage,
                   "cache_creation_input_tokens",
                   out cacheWrite) &&
               TryReadNonnegativeInt64(usage, "output_tokens", out output) &&
               TryAddUsage(
                   input,
                   cacheRead,
                   cacheWrite,
                   output,
                   out _,
                   out _);
    }

    private static bool TryAddUsage(
        long input,
        long cacheRead,
        long cacheWrite,
        long output,
        out long totalInput,
        out long normalizedTotal)
    {
        totalInput = normalizedTotal = 0;
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
                    "text" when block.TryGetProperty(
                            "text",
                            out JsonElement text) &&
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

    private static string? NormalizeSessionName(string? value) =>
        NormalizePreview(value);

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
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
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

    private static bool IsSubagentTranscript(ClaudeCodeEventContext context)
    {
        try
        {
            string projectsRoot = ClaudeCodeSourceIdentity.NormalizePath(
                Path.Combine(context.Instance.RootPath, "projects"));
            string sourcePath = ClaudeCodeSourceIdentity.NormalizePath(
                context.Entity.SourcePath);
            string relative = Path.GetRelativePath(projectsRoot, sourcePath);
            string[] parts = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 &&
                string.Equals(parts[2], "subagents", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.GetExtension(parts[3]),
                    ".jsonl",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static string NodeIdentityHash(string sessionId, string uuid) =>
        ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-node",
            $"{sessionId}\0{uuid}");

    private static string CallIdentityHash(string sessionId, string messageId) =>
        ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-message",
            $"{sessionId}\0{messageId}");

    private static string ToolIdentityHash(string sessionId, string toolId) =>
        ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-tool",
            $"{sessionId}\0{toolId}");

    private static string AgentIdentityHash(string agentId) =>
        ClaudeCodeSourceIdentity.HashIdentity("claude-code-agent", agentId);

    private static string SubagentSessionId(
        string parentSessionId,
        string agentHash) =>
        ClaudeCodeSourceIdentity.HashIdentity(
            "claude-code-subagent-session",
            $"{parentSessionId}\0{agentHash}");

    private static CollectorDiagnostic Diagnostic(
        ClaudeCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        code,
        message,
        context.Entity.SourceEntityId,
        line.ByteOffset);

    private sealed record CommonStateResult(
        ClaudeCodeParseState State,
        CollectorDiagnostic? Diagnostic);

    private sealed record CausalResult(
        ClaudeCodeParseState State,
        string? TurnIdHash,
        string? NodeHash,
        bool IsDirectPrompt,
        CollectorDiagnostic? Diagnostic);

    private sealed record ToolMergeResult(
        string? InvalidDiagnosticCode,
        int NextOrdinal,
        IReadOnlyList<string> AgentToolUseIdHashes);

    private sealed record SubagentLinkResult(
        UsageSessionMetadata? Session,
        UsageTurnDispatch? Dispatch,
        CollectorDiagnostic? Diagnostic)
    {
        public static SubagentLinkResult Empty { get; } = new(null, null, null);
    }
}
