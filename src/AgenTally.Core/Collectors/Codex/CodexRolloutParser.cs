using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.Codex;

public sealed record CodexEventContext(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string SourceFingerprint,
    DateTimeOffset ImportedAtUtc);

public sealed record CodexParseResult(
    UsageEvent? Event,
    CodexParseState State,
    CollectorDiagnostic? Diagnostic)
{
    public UsageSessionMetadata? SessionMetadata { get; init; }

    public UsageTurnMetadata? TurnMetadata { get; init; }

    public IReadOnlyList<UsageEventToolMetadata> EventTools { get; init; } = [];

    public UsageTurnDispatch? Dispatch { get; init; }
}

// Codex field handling was informed by these public implementations:
// https://github.com/mm7894215/TokenTracker/blob/main/src/lib/codex-rollout-parser.js
// https://github.com/farion1231/cc-switch/blob/main/src-tauri/src/services/session_usage_codex.rs
// https://github.com/douglasmonsky/codex-usage-tracker/tree/b3765c27f6c3bf6068e1935ea33d0e9decf1e2f6
public sealed partial class CodexRolloutParser
{
    public const string CurrentParserVersion = "codex-canonical-v11";

    private const string InvalidJsonCode = "codex.invalid_json";
    private const string InvalidJsonMessage = "Codex log line is not valid JSON.";
    private const string InvalidEventCode = "codex.invalid_token_event";
    private const string InvalidEventMessage = "Codex token event contains invalid structural data.";
    private const string MissingIdentityCode = "codex.missing_thread_identity";
    private const string MissingIdentityMessage = "Codex token event has no session identity and was skipped.";
    private const string CacheClampedCode = "codex.cached_input_clamped";
    private const string CacheClampedMessage =
        "Codex cached input exceeded reported input and was clamped.";
    private const string InvalidStateMetadataCode = "codex.invalid_state_metadata";
    private const string InvalidStateMetadataMessage =
        "Codex state metadata contained an invalid value and was cleared.";
    private const string FilesMentionedHeader =
        "# Files mentioned by the user:";
    private const string UserRequestHeader =
        "## My request for Codex:";
    public CodexParseResult ParseLine(
        JsonlLine line,
        CodexParseState state,
        CodexEventContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using JsonDocument document = JsonDocument.Parse(line.Utf8);
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return Diagnostic(state, context, line, InvalidEventCode, InvalidEventMessage);
            }

            string? type = StringProperty(root, "type");
            JsonElement payload = ObjectProperty(root, "payload");

            if (string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                CodexParseState metadataState = ParseSessionMeta(
                    payload,
                    state,
                    out bool invalidMetadata);
                var result = new CodexParseResult(
                    null,
                    metadataState,
                    invalidMetadata
                        ? NewDiagnostic(
                            context,
                            line,
                            InvalidStateMetadataCode,
                            InvalidStateMetadataMessage)
                        : null);
                return result with
                {
                    SessionMetadata = CreateSessionMetadata(
                        root,
                        metadataState,
                        context)
                };
            }

            if (string.Equals(type, "turn_context", StringComparison.Ordinal))
            {
                CodexParseState metadataState = ParseTurnContext(
                    root,
                    payload,
                    state,
                    out bool invalidMetadata,
                    out bool restoredReplayTarget);
                var result = new CodexParseResult(
                    null,
                    metadataState,
                    invalidMetadata
                        ? NewDiagnostic(
                            context,
                            line,
                            InvalidStateMetadataCode,
                            InvalidStateMetadataMessage)
                        : null);
                return result with
                {
                    TurnMetadata = metadataState.IsHistoryReplay ||
                        metadataState.IsReplayTargetContextPending
                        ? null
                        : CreateTurnMetadata(metadataState, context),
                    SessionMetadata = restoredReplayTarget
                        ? CreateSessionMetadata(root, metadataState, context)
                        : null
                };
            }

            if (string.Equals(type, "event_msg", StringComparison.Ordinal))
            {
                string? eventType = StringProperty(payload, "type");
                if (string.Equals(eventType, "task_started", StringComparison.Ordinal))
                {
                    return ParseTaskStarted(root, payload, state, context);
                }

                if (string.Equals(eventType, "user_message", StringComparison.Ordinal))
                {
                    return ParseUserMessage(root, payload, state, context);
                }

                if (string.Equals(eventType, "task_complete", StringComparison.Ordinal))
                {
                    return ParseTaskComplete(root, payload, state, context);
                }
            }

            if (string.Equals(type, "response_item", StringComparison.Ordinal) &&
                string.Equals(
                    StringProperty(payload, "type"),
                    "function_call",
                    StringComparison.Ordinal))
            {
                return ParseFunctionCall(root, payload, line, state, context);
            }

            if (IsReplayBoundary(type, payload))
            {
                return new CodexParseResult(
                    null,
                    CompleteReplayBoundary(state),
                    null);
            }

            if (!string.Equals(type, "event_msg", StringComparison.Ordinal) ||
                !string.Equals(StringProperty(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                return new CodexParseResult(null, state, null);
            }

            return ParseTokenEvent(root, payload, line, state, context);
        }
        catch (JsonException)
        {
            return Diagnostic(state, context, line, InvalidJsonCode, InvalidJsonMessage);
        }
    }

    private static CodexParseResult ParseTokenEvent(
        JsonElement root,
        JsonElement payload,
        JsonlLine line,
        CodexParseState state,
        CodexEventContext context)
    {
        long tokenIndex;
        try
        {
            tokenIndex = checked(state.TokenEventIndex + 1);
        }
        catch (OverflowException)
        {
            return Diagnostic(state, context, line, InvalidEventCode, InvalidEventMessage);
        }

        string[] pendingTools = state.PendingToolNames ?? [];
        CodexParseState indexedState = state with
        {
            TokenEventIndex = tokenIndex,
            PendingToolNames = null
        };
        JsonElement info = ObjectProperty(payload, "info");
        UsageRead lastRead = ReadUsage(info, "last_token_usage");
        UsageRead totalRead = ReadUsage(info, "total_token_usage");
        if (!lastRead.IsValid || !totalRead.IsValid)
        {
            return Diagnostic(indexedState, context, line, InvalidEventCode, InvalidEventMessage);
        }

        CodexTokenCounters? currentTotal = totalRead.Counters;
        bool cumulativeReset = currentTotal is not null &&
            indexedState.PreviousCumulative is not null &&
            DidReset(currentTotal, indexedState.PreviousCumulative);
        CodexParseState nextState = indexedState with
        {
            PreviousCumulative = NextCumulativeBaseline(
                currentTotal,
                indexedState.PreviousCumulative,
                cumulativeReset)
        };

        CodexTokenCounters? selected = SelectUsage(
            lastRead.Counters,
            currentTotal,
            indexedState.PreviousCumulative);
        bool inheritedSideBaseline =
            indexedState.SessionKind is SessionKind.Side &&
            indexedState.PreviousCumulative is null &&
            lastRead.Counters is null &&
            currentTotal is not null;
        if (selected is null ||
            indexedState.IsHistoryReplay ||
            indexedState.IsReplayTargetContextPending ||
            inheritedSideBaseline)
        {
            return new CodexParseResult(null, nextState, null);
        }

        if (string.IsNullOrWhiteSpace(indexedState.ThreadId))
        {
            return Diagnostic(
                nextState,
                context,
                line,
                MissingIdentityCode,
                MissingIdentityMessage);
        }

        if (!TryTimestamp(root, out DateTimeOffset occurredAtUtc))
        {
            return Diagnostic(nextState, context, line, InvalidEventCode, InvalidEventMessage);
        }

        CanonicalIdentityRead canonicalIdentity = CanonicalDedupKey(
            root,
            payload,
            info,
            indexedState,
            occurredAtUtc,
            lastRead.Counters,
            currentTotal);
        if (!canonicalIdentity.IsValid)
        {
            return Diagnostic(nextState, context, line, InvalidEventCode, InvalidEventMessage);
        }

        bool cacheClamped = selected.Input.HasValue &&
            selected.CachedInput.HasValue &&
            selected.CachedInput.Value > selected.Input.Value;
        long? input = selected.Input;
        long? cacheRead = selected.CachedInput.HasValue
            ? input.HasValue
                ? Math.Min(input.Value, selected.CachedInput.Value)
                : selected.CachedInput
            : null;
        long? uncached = input.HasValue && cacheRead.HasValue
            ? input.Value - cacheRead.Value
            : null;
        long? cacheWrite = selected.CacheWrite;
        long? normalizedTotal = input.HasValue && selected.Output.HasValue
            ? SaturatingAdd(
                input.Value,
                cacheWrite.GetValueOrDefault(),
                selected.Output.Value)
            : null;

        var tokens = new TokenUsage
        {
            InputReported = ExactMetric(input),
            UncachedInput = DerivedMetric(uncached),
            CacheRead = ExactMetric(cacheRead),
            CacheWrite = ExactMetric(cacheWrite),
            Output = ExactMetric(selected.Output),
            Reasoning = ExactMetric(selected.Reasoning),
            Tool = TokenMetric.Unavailable,
            ReportedTotal = ExactMetric(selected.Total),
            NormalizedTotal = DerivedMetric(normalizedTotal),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Included
        };
        var model = new ModelIdentity
        {
            RawModel = indexedState.CurrentRawModel,
            NormalizedModel = NormalizeModel(indexedState.CurrentRawModel),
            ProviderId = indexedState.CurrentProviderId,
            ResolutionOrigin = string.IsNullOrWhiteSpace(indexedState.CurrentRawModel)
                ? ModelResolutionOrigin.Unknown
                : ModelResolutionOrigin.LogConfirmed
        };
        string threadId = indexedState.ThreadId;
        string eventIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"{context.Entity.SourceEntityId}:token:{tokenIndex}");
        var usageEvent = new UsageEvent(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            eventIdentity,
            canonicalIdentity.Value!,
            context.Instance.SourceKind,
            occurredAtUtc,
            context.ImportedAtUtc.ToUniversalTime(),
            model,
            tokens,
            CompletionState.Completed,
            DataQuality.Derived,
            CurrentParserVersion,
            context.SourceFingerprint,
            tokenIndex)
        {
            SessionId = threadId,
            ParentSessionId = indexedState.ParentSessionId,
            TurnIdHash = indexedState.CurrentTurnIdHash,
            ProjectId = indexedState.ProjectId,
            ProjectPath = indexedState.ProjectPath,
            ProjectRepositoryIdentityHash =
                indexedState.ProjectRepositoryIdentityHash
        };

        CollectorDiagnostic? diagnostic = cacheClamped
            ? NewDiagnostic(context, line, CacheClampedCode, CacheClampedMessage)
            : null;
        var result = new CodexParseResult(usageEvent, nextState, diagnostic);
        if (pendingTools.Length == 0)
        {
            return result;
        }

        return result with
        {
            EventTools = pendingTools.Select((toolName, ordinal) =>
                new UsageEventToolMetadata(
                    context.Instance.AgentId,
                    context.Instance.SourceInstanceId,
                    context.Entity.SourceEntityId,
                    usageEvent.DedupKey,
                    ordinal,
                    toolName,
                    CurrentParserVersion)).ToArray()
        };
    }

    private static CodexParseState ParseSessionMeta(
        JsonElement payload,
        CodexParseState state,
        out bool invalidMetadata)
    {
        StateStringRead thread = ReadStateString(payload, "id");
        StateStringRead topLevelParent = ReadStateString(payload, "parent_thread_id");
        StateStringRead forkedFrom = ReadStateString(payload, "forked_from_id");
        StateStringRead session = ReadStateString(payload, "session_id");
        StateStringRead provider = ReadStateString(payload, "model_provider");
        StateStringRead subagentParent = ReadSubagentParentSessionId(payload);
        (SessionRole sessionRole, string? agentPathHash, string? agentLeafHash) =
            ReadSessionRoleAndAgentPath(payload);
        bool invalidThread = thread.IsPresent && !thread.IsValid;
        bool invalidDirectParent = IsInvalid(topLevelParent) ||
            IsInvalid(subagentParent);
        bool invalidReplayIdentity = IsInvalid(forkedFrom) ||
            IsInvalid(session);
        bool changedThread = thread.IsValid &&
            state.ThreadId is not null &&
            !string.Equals(thread.Value, state.ThreadId, StringComparison.Ordinal);
        bool recoveredThread = thread.IsValid &&
            state.ThreadId is null &&
            (state.IsHistoryReplay || HasSessionScopedState(state));
        bool identityBoundary = invalidThread || changedThread || recoveredThread;
        string? repositoryIdentityFallback = identityBoundary
            ? null
            : state.ProjectRepositoryIdentityHash;
        ProjectRead project = ReadProject(
            payload,
            repositoryIdentityFallback);
        invalidMetadata = invalidThread ||
            invalidDirectParent ||
            invalidReplayIdentity ||
            IsInvalid(provider) ||
            IsInvalid(project);

        if (invalidThread)
        {
            // An invalid identity makes every other session-scoped value on this
            // metadata line untrustworthy. Keep only the monotonic event index.
            return state with
            {
                ThreadId = null,
                ParentSessionId = null,
                ForkedFromSessionId = null,
                SessionKind = SessionKind.Unknown,
                ParentRelationOrigin = SessionRelationOrigin.None,
                ParentRelationState = SessionRelationState.Uncertain,
                CompatibilityLevel = CompatibilityLevel.PartiallyCompatible,
                CurrentRawModel = null,
                CurrentProviderId = null,
                ProjectId = null,
                ProjectPath = null,
                ProjectRepositoryIdentityHash = null,
                PreviousCumulative = null,
                CurrentTurnIdHash = null,
                CurrentEffort = null,
                CurrentTurnTimestampUtc = null,
                CurrentTurnStartedAtUtc = null,
                CurrentTurnCompletedAtUtc = null,
                CurrentPromptPreview = null,
                CurrentUserMessageCount = 0,
                PendingToolNames = null,
                SessionRole = SessionRole.Unknown,
                AgentPathHash = null,
                AgentLeafHash = null,
                ReplayTarget = null,
                IsReplayTargetContextPending = false,
                IsHistoryReplay = true
            };
        }

        CodexParseState baseline = identityBoundary
            ? state with
            {
                ParentSessionId = null,
                ForkedFromSessionId = null,
                SessionKind = SessionKind.Unknown,
                ParentRelationOrigin = SessionRelationOrigin.None,
                ParentRelationState = SessionRelationState.None,
                CurrentRawModel = null,
                CurrentProviderId = null,
                ProjectId = null,
                ProjectPath = null,
                ProjectRepositoryIdentityHash = null,
                PreviousCumulative = null,
                CurrentTurnIdHash = null,
                CurrentEffort = null,
                CurrentTurnTimestampUtc = null,
                CurrentTurnStartedAtUtc = null,
                CurrentTurnCompletedAtUtc = null,
                CurrentPromptPreview = null,
                CurrentUserMessageCount = 0,
                PendingToolNames = null,
                SessionRole = SessionRole.Unknown,
                AgentPathHash = null,
                AgentLeafHash = null,
                IsHistoryReplay = state.IsHistoryReplay &&
                    (recoveredThread ||
                     state.ReplayTarget is not null)
            }
            : state;

        string? threadId = Resolve(thread, baseline.ThreadId);
        string? sessionId = session.IsValid ? session.Value : null;
        bool sessionDiffers = !string.IsNullOrWhiteSpace(threadId) &&
            !string.IsNullOrWhiteSpace(sessionId) &&
            !string.Equals(threadId, sessionId, StringComparison.Ordinal);
        bool hasSubagentSource = HasSubagentSource(payload);
        ParentRelationRead relation = ResolveDirectParent(
            threadId,
            topLevelParent,
            subagentParent,
            sessionDiffers ? session : StateStringRead.Missing);
        SessionKind sessionKind = hasSubagentSource ||
            topLevelParent.IsValid ||
            subagentParent.IsValid ||
            sessionDiffers
                ? SessionKind.Side
                : baseline.SessionKind is not SessionKind.Unknown
                    ? baseline.SessionKind
                    : threadId is not null
                    ? SessionKind.Primary
                    : SessionKind.Unknown;
        CodexReplayTargetState? existingReplayTarget = baseline.ReplayTarget;
        bool isKnownReplayTarget = forkedFrom.IsValid &&
            threadId is not null &&
            string.Equals(
                threadId,
                existingReplayTarget?.SessionId,
                StringComparison.Ordinal);
        bool repeatsActiveForkMetadata = forkedFrom.IsValid &&
            !baseline.IsHistoryReplay &&
            existingReplayTarget is null &&
            threadId is not null &&
            string.Equals(
                threadId,
                state.ThreadId,
                StringComparison.Ordinal);
        bool startsNewReplay = forkedFrom.IsValid &&
            !isKnownReplayTarget &&
            !repeatsActiveForkMetadata;
        bool completesReplayTarget = isKnownReplayTarget &&
            !baseline.IsHistoryReplay;
        bool isHistoryReplay = !completesReplayTarget &&
            (baseline.IsHistoryReplay ||
            invalidDirectParent ||
            invalidReplayIdentity ||
            startsNewReplay ||
            (sessionDiffers &&
             !topLevelParent.IsValid &&
             !subagentParent.IsValid));
        bool relationshipUncertain = invalidDirectParent ||
            relation.State is SessionRelationState.Uncertain;
        string? parentSessionId = relationshipUncertain
            ? null
            : relation.State is SessionRelationState.Confirmed
                ? relation.ParentSessionId
                : baseline.ParentSessionId;
        SessionRelationOrigin relationOrigin = relationshipUncertain
            ? SessionRelationOrigin.None
            : relation.State is SessionRelationState.Confirmed
                ? relation.Origin
                : baseline.ParentRelationOrigin;
        SessionRelationState relationState = relationshipUncertain
            ? SessionRelationState.Uncertain
            : relation.State is SessionRelationState.Confirmed
                ? relation.State
                : baseline.ParentRelationState;
        string? forkedFromSessionId = forkedFrom.IsValid
            ? forkedFrom.Value
            : invalidReplayIdentity
                ? null
                : baseline.ForkedFromSessionId;
        CompatibilityLevel compatibilityLevel =
            invalidMetadata || relationshipUncertain
                ? CompatibilityLevel.PartiallyCompatible
                : CompatibilityLevel.FullyCompatible;
        string? providerId = Resolve(provider, baseline.CurrentProviderId);
        SessionRole effectiveSessionRole = sessionRole is not SessionRole.Unknown
            ? sessionRole
            : sessionKind is SessionKind.Primary
                ? SessionRole.Main
                : baseline.SessionRole;
        string? effectiveAgentPathHash = agentPathHash ?? baseline.AgentPathHash;
        string? effectiveAgentLeafHash = agentLeafHash ?? baseline.AgentLeafHash;
        string? projectId = ResolveProjectId(project, baseline.ProjectId);
        string? projectPath = ResolveProjectPath(project, baseline.ProjectPath);
        string? repositoryIdentityHash =
            ResolveProjectRepositoryIdentityHash(
                project,
                baseline.ProjectRepositoryIdentityHash);
        CodexReplayTargetState? replayTarget = invalidReplayIdentity ||
            completesReplayTarget
                ? null
                : startsNewReplay && threadId is not null
                    ? new CodexReplayTargetState(
                        threadId,
                        parentSessionId,
                        forkedFromSessionId,
                        sessionKind,
                        relationOrigin,
                        relationState,
                        compatibilityLevel,
                        effectiveSessionRole,
                        effectiveAgentPathHash,
                        effectiveAgentLeafHash,
                        baseline.CurrentRawModel,
                        providerId,
                        projectId,
                        projectPath,
                        repositoryIdentityHash,
                        null)
                    : existingReplayTarget is not null &&
                      baseline.IsHistoryReplay &&
                      threadId is not null &&
                      !string.Equals(
                          threadId,
                          existingReplayTarget.SessionId,
                          StringComparison.Ordinal) &&
                      projectPath is not null
                        ? existingReplayTarget with
                        {
                            ReplaySourceProjectPath =
                                existingReplayTarget.ReplaySourceProjectPath ??
                                projectPath
                        }
                        : existingReplayTarget;

        return baseline with
        {
            ThreadId = threadId,
            ParentSessionId = parentSessionId,
            ForkedFromSessionId = forkedFromSessionId,
            SessionKind = sessionKind,
            ParentRelationOrigin = relationOrigin,
            ParentRelationState = relationState,
            CompatibilityLevel = compatibilityLevel,
            CurrentProviderId = providerId,
            SessionRole = effectiveSessionRole,
            AgentPathHash = effectiveAgentPathHash,
            AgentLeafHash = effectiveAgentLeafHash,
            ProjectId = projectId,
            ProjectPath = projectPath,
            ProjectRepositoryIdentityHash = repositoryIdentityHash,
            ReplayTarget = replayTarget,
            IsReplayTargetContextPending = completesReplayTarget
                ? false
                : baseline.IsReplayTargetContextPending,
            IsHistoryReplay = isHistoryReplay
        };
    }

    private static UsageSessionMetadata? CreateSessionMetadata(
        JsonElement root,
        CodexParseState state,
        CodexEventContext context)
    {
        if (state.ThreadId is null)
        {
            return null;
        }

        DateTimeOffset observedAtUtc = TryTimestamp(root, out DateTimeOffset timestamp)
            ? timestamp
            : context.ImportedAtUtc.ToUniversalTime();
        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.ThreadId,
            state.SessionKind,
            state.ParentSessionId,
            state.ForkedFromSessionId,
            state.ParentRelationOrigin,
            state.ParentRelationState,
            state.ReplayState,
            state.CompatibilityLevel,
            observedAtUtc,
            CurrentParserVersion)
        {
            ProjectId = state.ProjectId,
            ProjectPath = state.ProjectPath,
            ProjectRepositoryIdentityHash =
                state.ProjectRepositoryIdentityHash,
            SessionRole = state.SessionRole,
            AgentPathHash = state.AgentPathHash,
            AgentLeafHash = state.AgentLeafHash
        };
    }

    private static CodexParseState ParseTurnContext(
        JsonElement root,
        JsonElement payload,
        CodexParseState state,
        out bool invalidMetadata,
        out bool restoredReplayTarget)
    {
        StateStringRead model = ReadStateString(payload, "model");
        StateStringRead provider = ReadStateString(payload, "model_provider");
        StateStringRead turnId = ReadStateString(root, "turn_id");
        if (!turnId.IsPresent)
        {
            turnId = ReadStateString(payload, "turn_id");
        }

        StateStringRead effort = ReadStateString(payload, "effort");
        if (!effort.IsPresent)
        {
            effort = ReadStateString(payload, "reasoning_effort");
        }

        ProjectRead project = ReadProject(
            payload,
            state.ProjectRepositoryIdentityHash);
        bool hasTimestamp = root.ValueKind is JsonValueKind.Object &&
            root.TryGetProperty("timestamp", out _);
        bool validTimestamp = TryTimestamp(root, out DateTimeOffset turnTimestampUtc);
        invalidMetadata = IsInvalid(model) ||
            IsInvalid(provider) ||
            IsInvalid(turnId) ||
            IsInvalid(effort) ||
            IsInvalid(project) ||
            (hasTimestamp && !validTimestamp);
        CodexReplayTargetState? replayTarget = state.ReplayTarget;
        restoredReplayTarget =
            replayTarget is not null &&
            replayTarget.ProjectPath is not null &&
            project.IsValid &&
            string.Equals(
                project.Path,
                replayTarget.ProjectPath,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                state.ProjectPath,
                replayTarget.ProjectPath,
                StringComparison.OrdinalIgnoreCase) &&
            (!string.Equals(
                 state.ThreadId,
                 replayTarget.SessionId,
                 StringComparison.Ordinal) ||
             state.IsHistoryReplay ||
             state.IsReplayTargetContextPending);
        bool resolvedPendingContext =
            state.IsReplayTargetContextPending && project.IsValid;
        CodexParseState baseline = restoredReplayTarget
            ? RestoreReplayTarget(state, replayTarget!)
            : resolvedPendingContext
                ? state with { IsReplayTargetContextPending = false }
                : state;
        string? nextTurnHash = turnId.IsValid
            ? HashSensitiveIdentifier(turnId.Value!)
            : null;
        bool changedTurn = !string.Equals(
            nextTurnHash,
            baseline.CurrentTurnIdHash,
            StringComparison.Ordinal);
        return baseline with
        {
            CurrentRawModel = Resolve(model, baseline.CurrentRawModel),
            CurrentProviderId = Resolve(provider, baseline.CurrentProviderId),
            ProjectId = ResolveProjectId(project, baseline.ProjectId),
            ProjectPath = ResolveProjectPath(project, baseline.ProjectPath),
            ProjectRepositoryIdentityHash =
                ResolveProjectRepositoryIdentityHash(
                    project,
                    baseline.ProjectRepositoryIdentityHash),
            CurrentTurnIdHash = nextTurnHash,
            CurrentEffort = effort.IsValid ? effort.Value : null,
            CurrentTurnTimestampUtc = validTimestamp ? turnTimestampUtc : null,
            CurrentTurnStartedAtUtc = changedTurn
                ? validTimestamp ? turnTimestampUtc : null
                : baseline.CurrentTurnStartedAtUtc,
            CurrentTurnCompletedAtUtc = changedTurn
                ? null
                : baseline.CurrentTurnCompletedAtUtc,
            CurrentPromptPreview = changedTurn
                ? null
                : baseline.CurrentPromptPreview,
            CurrentUserMessageCount = changedTurn
                ? 0
                : baseline.CurrentUserMessageCount,
            PendingToolNames = changedTurn ? null : baseline.PendingToolNames
        };
    }

    private static CodexParseResult ParseTaskStarted(
        JsonElement root,
        JsonElement payload,
        CodexParseState state,
        CodexEventContext context)
    {
        StateStringRead turnId = ReadStateString(payload, "turn_id");
        if (!turnId.IsValid)
        {
            return new CodexParseResult(null, state, null);
        }

        CodexReplayTargetState? replayTarget = state.ReplayTarget;
        bool restoredReplayTarget = replayTarget is not null &&
            ShouldRestoreReplayTargetAtTaskStarted(
                state,
                replayTarget,
                turnId.Value!);
        bool awaitingReplayTargetContext = replayTarget is not null &&
            ShouldAwaitReplayTargetContextAtTaskStarted(
                state,
                replayTarget,
                turnId.Value!);
        CodexParseState baseline = restoredReplayTarget
            ? RestoreReplayTarget(state, replayTarget!)
            : awaitingReplayTargetContext
                ? state with
                {
                    IsHistoryReplay = false,
                    IsReplayTargetContextPending = true
                }
                : state;
        string turnHash = HashSensitiveIdentifier(turnId.Value!);
        bool changedTurn = !string.Equals(
            turnHash,
            baseline.CurrentTurnIdHash,
            StringComparison.Ordinal);
        DateTimeOffset startedAtUtc = TryTimestamp(root, out DateTimeOffset timestamp)
            ? timestamp
            : context.ImportedAtUtc.ToUniversalTime();
        CodexParseState nextState = baseline with
        {
            CurrentTurnIdHash = turnHash,
            CurrentTurnStartedAtUtc = startedAtUtc,
            CurrentTurnCompletedAtUtc = null,
            CurrentPromptPreview = changedTurn ? null : baseline.CurrentPromptPreview,
            CurrentUserMessageCount = changedTurn
                ? 0
                : baseline.CurrentUserMessageCount,
            PendingToolNames = changedTurn ? null : baseline.PendingToolNames
        };
        return new CodexParseResult(null, nextState, null)
        {
            TurnMetadata = nextState.IsHistoryReplay ||
                nextState.IsReplayTargetContextPending
                ? null
                : CreateTurnMetadata(nextState, context),
            SessionMetadata = restoredReplayTarget
                ? CreateSessionMetadata(root, nextState, context)
                : null
        };
    }

    private static CodexParseResult ParseUserMessage(
        JsonElement root,
        JsonElement payload,
        CodexParseState state,
        CodexEventContext context)
    {
        if (state.IsHistoryReplay ||
            state.IsReplayTargetContextPending ||
            state.ThreadId is null ||
            state.CurrentTurnIdHash is null)
        {
            return new CodexParseResult(null, state, null);
        }

        int messageCount;
        try
        {
            messageCount = checked(state.CurrentUserMessageCount + 1);
        }
        catch (OverflowException)
        {
            return new CodexParseResult(null, state, null);
        }

        string? preview = messageCount == 1
            ? BuildPromptPreview(payload)
            : state.CurrentPromptPreview;
        DateTimeOffset startedAtUtc = state.CurrentTurnStartedAtUtc ??
            (TryTimestamp(root, out DateTimeOffset timestamp)
                ? timestamp
                : context.ImportedAtUtc.ToUniversalTime());
        CodexParseState nextState = state with
        {
            CurrentTurnStartedAtUtc = startedAtUtc,
            CurrentPromptPreview = preview,
            CurrentUserMessageCount = messageCount
        };
        return new CodexParseResult(null, nextState, null)
        {
            TurnMetadata = CreateTurnMetadata(nextState, context)
        };
    }

    private static CodexParseResult ParseTaskComplete(
        JsonElement root,
        JsonElement payload,
        CodexParseState state,
        CodexEventContext context)
    {
        if (state.IsHistoryReplay || state.IsReplayTargetContextPending)
        {
            return new CodexParseResult(null, state, null);
        }

        StateStringRead turnId = ReadStateString(payload, "turn_id");
        string? turnHash = turnId.IsValid
            ? HashSensitiveIdentifier(turnId.Value!)
            : state.CurrentTurnIdHash;
        if (state.ThreadId is null || turnHash is null)
        {
            return new CodexParseResult(null, state, null);
        }

        DateTimeOffset completedAtUtc = TryTimestamp(root, out DateTimeOffset timestamp)
            ? timestamp
            : context.ImportedAtUtc.ToUniversalTime();
        bool changedTurn = !string.Equals(
            turnHash,
            state.CurrentTurnIdHash,
            StringComparison.Ordinal);
        CodexParseState nextState = state with
        {
            CurrentTurnIdHash = turnHash,
            CurrentTurnStartedAtUtc = changedTurn
                ? completedAtUtc
                : state.CurrentTurnStartedAtUtc ?? completedAtUtc,
            CurrentTurnCompletedAtUtc = completedAtUtc,
            CurrentPromptPreview = changedTurn ? null : state.CurrentPromptPreview,
            CurrentUserMessageCount = changedTurn
                ? 0
                : state.CurrentUserMessageCount,
            PendingToolNames = null
        };
        return new CodexParseResult(null, nextState, null)
        {
            TurnMetadata = CreateTurnMetadata(nextState, context)
        };
    }

    private static CodexParseResult ParseFunctionCall(
        JsonElement root,
        JsonElement payload,
        JsonlLine line,
        CodexParseState state,
        CodexEventContext context)
    {
        if (state.IsHistoryReplay || state.IsReplayTargetContextPending)
        {
            return new CodexParseResult(null, state, null);
        }

        StateStringRead nameRead = ReadStateString(payload, "name");
        string? toolName = nameRead.IsValid &&
            nameRead.Value!.Length <= 128
                ? nameRead.Value
                : null;
        string[]? pendingTools = state.PendingToolNames;
        if (toolName is not null && (pendingTools?.Length ?? 0) < 64)
        {
            pendingTools = [.. pendingTools ?? [], toolName];
        }

        CodexParseState nextState = state with { PendingToolNames = pendingTools };
        UsageTurnDispatch? dispatch = CreateDispatch(
            root,
            payload,
            line,
            nextState,
            context,
            toolName);
        return new CodexParseResult(null, nextState, null)
        {
            Dispatch = dispatch
        };
    }

    private static UsageTurnDispatch? CreateDispatch(
        JsonElement root,
        JsonElement payload,
        JsonlLine line,
        CodexParseState state,
        CodexEventContext context,
        string? toolName)
    {
        if (state.ThreadId is null ||
            state.CurrentTurnIdHash is null ||
            toolName is null)
        {
            return null;
        }

        TurnDispatchKind dispatchKind;
        DispatchTargetKind targetKind;
        string argumentName;
        if (string.Equals(toolName, "spawn_agent", StringComparison.Ordinal))
        {
            dispatchKind = TurnDispatchKind.Spawn;
            targetKind = DispatchTargetKind.AgentLeaf;
            argumentName = "task_name";
        }
        else if (string.Equals(toolName, "followup_task", StringComparison.Ordinal))
        {
            dispatchKind = TurnDispatchKind.FollowUp;
            targetKind = DispatchTargetKind.AgentPath;
            argumentName = "target";
        }
        else
        {
            return null;
        }

        string? target = ReadFunctionArgument(payload, argumentName);
        if (target is null)
        {
            return null;
        }

        string normalizedTarget = targetKind is DispatchTargetKind.AgentLeaf
            ? NormalizeAgentLeaf(target)
            : NormalizeAgentPath(target);
        if (normalizedTarget.Length == 0)
        {
            return null;
        }

        StateStringRead callId = ReadStateString(payload, "call_id");
        string dispatchIdentity = callId.IsValid
            ? callId.Value!
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{context.Entity.SourceEntityId}:{line.LineNumber}:{toolName}");
        DateTimeOffset occurredAtUtc = TryTimestamp(root, out DateTimeOffset timestamp)
            ? timestamp
            : context.ImportedAtUtc.ToUniversalTime();
        return new UsageTurnDispatch(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.ThreadId,
            state.CurrentTurnIdHash,
            HashSensitiveIdentifier(dispatchIdentity),
            HashSensitiveIdentifier(normalizedTarget),
            dispatchKind,
            targetKind,
            occurredAtUtc,
            CurrentParserVersion);
    }

    private static string? ReadFunctionArgument(
        JsonElement payload,
        string propertyName)
    {
        string? arguments = StringProperty(payload, "arguments");
        if (string.IsNullOrWhiteSpace(arguments) ||
            arguments.Length > 1024 * 1024)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            StateStringRead value = ReadStateString(document.RootElement, propertyName);
            return value.IsValid ? value.Value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageTurnMetadata? CreateTurnMetadata(
        CodexParseState state,
        CodexEventContext context)
    {
        if (state.ThreadId is null || state.CurrentTurnIdHash is null)
        {
            return null;
        }

        DateTimeOffset startedAtUtc = state.CurrentTurnStartedAtUtc ??
            state.CurrentTurnTimestampUtc ??
            context.ImportedAtUtc.ToUniversalTime();
        return new UsageTurnMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            state.ThreadId,
            state.CurrentTurnIdHash,
            startedAtUtc,
            state.CurrentTurnCompletedAtUtc,
            state.CurrentPromptPreview,
            state.CurrentUserMessageCount,
            CurrentParserVersion);
    }

    private static string? BuildPromptPreview(JsonElement payload)
    {
        var source = new StringBuilder(256);
        bool hasImages = HasCollectionValue(payload, "images") ||
            HasCollectionValue(payload, "local_images");
        bool hasAudio = HasCollectionValue(payload, "audio") ||
            HasCollectionValue(payload, "local_audio");
        if (hasImages)
        {
            source.Append("[图片] ");
        }

        if (hasAudio)
        {
            source.Append("[音频] ");
        }

        string? message = StringProperty(payload, "message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            source.Append(
                hasImages || hasAudio
                    ? StripAttachmentEnvelope(payload, message)
                    : message);
        }

        var normalized = new StringBuilder(160);
        bool pendingSpace = false;
        foreach (Rune value in source.ToString().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(value) || Rune.IsControl(value))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(value);
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        var truncated = new StringBuilder(160);
        int runeCount = 0;
        foreach (Rune value in normalized.ToString().EnumerateRunes())
        {
            if (runeCount++ == 120)
            {
                break;
            }

            truncated.Append(value);
        }

        return truncated.ToString();
    }

    private static string StripAttachmentEnvelope(
        JsonElement payload,
        string message)
    {
        int filesIndex = message.IndexOf(
            FilesMentionedHeader,
            StringComparison.Ordinal);
        if (filesIndex >= 0)
        {
            int requestIndex = message.IndexOf(
                UserRequestHeader,
                filesIndex + FilesMentionedHeader.Length,
                StringComparison.Ordinal);
            string prefix = message[..filesIndex];
            message = requestIndex >= 0
                ? string.Concat(
                    prefix,
                    " ",
                    message[(requestIndex + UserRequestHeader.Length)..])
                : prefix;
        }

        message = AttachmentMarkupRegex().Replace(message, " ");
        foreach (string propertyName in new[]
        {
            "images",
            "local_images",
            "audio",
            "local_audio"
        })
        {
            message = RemoveAttachmentIdentities(
                payload,
                propertyName,
                message);
        }

        return message;
    }

    private static string RemoveAttachmentIdentities(
        JsonElement payload,
        string propertyName,
        string message)
    {
        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out JsonElement values))
        {
            return message;
        }

        IEnumerable<JsonElement> elements = values.ValueKind switch
        {
            JsonValueKind.Array => values.EnumerateArray(),
            JsonValueKind.String => [values],
            _ => []
        };
        foreach (JsonElement element in elements)
        {
            if (element.ValueKind is not JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()))
            {
                continue;
            }

            string identity = element.GetString()!;
            message = message.Replace(
                identity,
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            message = message.Replace(
                identity.Replace('\\', '/'),
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            message = message.Replace(
                identity.Replace('/', '\\'),
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            int separator = identity.LastIndexOfAny(['\\', '/']);
            if (separator >= 0 && separator + 1 < identity.Length)
            {
                message = message.Replace(
                    identity[(separator + 1)..],
                    string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return message;
    }

    private static bool HasCollectionValue(
        JsonElement parent,
        string propertyName)
    {
        if (parent.ValueKind is not JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => false
        };
    }

    private static (
        SessionRole SessionRole,
        string? AgentPathHash,
        string? AgentLeafHash) ReadSessionRoleAndAgentPath(JsonElement payload)
    {
        JsonElement source = ObjectProperty(payload, "source");
        SessionRole role = SessionRole.Unknown;
        if (source.ValueKind is JsonValueKind.Object &&
            source.TryGetProperty("subagent", out JsonElement subagent))
        {
            if (subagent.ValueKind is JsonValueKind.Object &&
                subagent.TryGetProperty("other", out JsonElement other) &&
                other.ValueKind is JsonValueKind.String)
            {
                role = string.Equals(
                    other.GetString(),
                    "guardian",
                    StringComparison.OrdinalIgnoreCase)
                    ? SessionRole.Guardian
                    : SessionRole.Internal;
            }
            else
            {
                role = SessionRole.Subagent;
            }
        }

        StateStringRead agentPath = ReadStateString(payload, "agent_path");
        if (!agentPath.IsValid)
        {
            return (role, null, null);
        }

        string normalizedPath = NormalizeAgentPath(agentPath.Value!);
        string normalizedLeaf = NormalizeAgentLeaf(normalizedPath);
        return (
            role,
            HashSensitiveIdentifier(normalizedPath),
            HashSensitiveIdentifier(normalizedLeaf));
    }

    private static string NormalizeAgentPath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeAgentLeaf(string value)
    {
        string normalized = NormalizeAgentPath(value);
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static CodexTokenCounters? SelectUsage(
        CodexTokenCounters? last,
        CodexTokenCounters? total,
        CodexTokenCounters? previous)
    {
        if (total is not null && previous is not null)
        {
            if (DidReset(total, previous))
            {
                return last ?? total;
            }

            if (!Advanced(total, previous))
            {
                return null;
            }

            return last ?? Delta(total, previous);
        }

        if (last is not null)
        {
            return last;
        }

        return total;
    }

    private static bool Advanced(CodexTokenCounters current, CodexTokenCounters previous)
    {
        if (current.Total.HasValue && previous.Total.HasValue)
        {
            return current.Total.Value > previous.Total.Value;
        }

        return Increased(current.Input, previous.Input) ||
        Increased(current.CachedInput, previous.CachedInput) ||
        Increased(current.Output, previous.Output) ||
        Increased(current.Reasoning, previous.Reasoning) ||
        Increased(current.CacheWrite, previous.CacheWrite) ||
        Increased(current.Total, previous.Total);
    }

    private static bool Increased(long? current, long? previous) =>
        current.HasValue && current.Value > previous.GetValueOrDefault();

    private static CanonicalIdentityRead CanonicalDedupKey(
        JsonElement root,
        JsonElement payload,
        JsonElement info,
        CodexParseState state,
        DateTimeOffset occurredAtUtc,
        CodexTokenCounters? last,
        CodexTokenCounters? total)
    {
        ExplicitIdentityRead explicitIdentity = ReadExplicitIdentity(root, payload, info);
        if (!explicitIdentity.IsValid)
        {
            return CanonicalIdentityRead.Invalid;
        }

        var canonical = new StringBuilder(512);
        AppendCanonical(canonical, "schema", "codex-logical-call-v2");
        AppendCanonical(canonical, "session_id", state.ThreadId);
        if (explicitIdentity.IsPresent)
        {
            AppendCanonical(canonical, "kind", "explicit");
            AppendCanonical(
                canonical,
                "path",
                $"{explicitIdentity.Scope}.{explicitIdentity.FieldName}");
            AppendCanonical(canonical, "field", explicitIdentity.FieldName);
            AppendCanonical(canonical, "value", explicitIdentity.Value);
        }
        else if (state.CurrentTurnIdHash is not null)
        {
            AppendCanonical(canonical, "kind", "turn");
            AppendCanonical(canonical, "turn_hash", state.CurrentTurnIdHash);
            AppendCanonical(canonical, "model", state.CurrentRawModel);
            AppendCanonical(canonical, "effort", state.CurrentEffort);
            AppendCounters(canonical, "last", last);
            AppendCounters(canonical, "total", total);
        }
        else
        {
            AppendCanonical(canonical, "kind", "strict-time-fallback");
            AppendCanonical(
                canonical,
                "event_utc_ticks",
                occurredAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture));
            AppendCanonical(
                canonical,
                "turn_utc_ticks",
                TimestampTicksString(state.CurrentTurnTimestampUtc));
            AppendCanonical(canonical, "model", state.CurrentRawModel);
            AppendCanonical(canonical, "effort", state.CurrentEffort);
            AppendCounters(canonical, "last", last);
            AppendCounters(canonical, "total", total);
        }

        return CanonicalIdentityRead.Valid(HashSensitiveIdentifier(canonical.ToString()));
    }

    private static ExplicitIdentityRead ReadExplicitIdentity(
        JsonElement root,
        JsonElement payload,
        JsonElement info)
    {
        (string Scope, JsonElement Value)[] scopes =
        [
            ("root", root),
            ("payload", payload),
            ("info", info)
        ];
        string[] fieldNames = ["usage_id", "event_id", "call_id"];

        foreach ((string scope, JsonElement value) in scopes)
        {
            foreach (string fieldName in fieldNames)
            {
                StateStringRead identity = ReadStateString(value, fieldName);
                if (!identity.IsPresent)
                {
                    continue;
                }

                return identity.IsValid
                    ? ExplicitIdentityRead.Valid(scope, fieldName, identity.Value!)
                    : ExplicitIdentityRead.Invalid;
            }
        }

        return ExplicitIdentityRead.Missing;
    }

    private static void AppendCounters(
        StringBuilder canonical,
        string prefix,
        CodexTokenCounters? counters)
    {
        AppendCanonical(canonical, $"{prefix}.input", CounterString(counters?.Input));
        AppendCanonical(
            canonical,
            $"{prefix}.cached_input",
            CounterString(counters?.CachedInput));
        AppendCanonical(canonical, $"{prefix}.output", CounterString(counters?.Output));
        AppendCanonical(
            canonical,
            $"{prefix}.reasoning",
            CounterString(counters?.Reasoning));
        AppendCanonical(
            canonical,
            $"{prefix}.cache_write",
            CounterString(counters?.CacheWrite));
        AppendCanonical(canonical, $"{prefix}.total", CounterString(counters?.Total));
    }

    private static string? CounterString(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string? TimestampTicksString(DateTimeOffset? value) =>
        value.HasValue
            ? value.Value.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)
            : null;

    private static void AppendCanonical(
        StringBuilder canonical,
        string name,
        string? value)
    {
        canonical.Append(name.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(name);
        canonical.Append('=');
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
        {
            canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(value);
        }

        canonical.Append(';');
    }

    private static string HashSensitiveIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static CodexTokenCounters? NextCumulativeBaseline(
        CodexTokenCounters? current,
        CodexTokenCounters? previous,
        bool reset)
    {
        if (current is null)
        {
            return previous;
        }

        if (previous is null || reset)
        {
            return current;
        }

        return new CodexTokenCounters(
            current.Input ?? previous.Input,
            current.CachedInput ?? previous.CachedInput,
            current.Output ?? previous.Output,
            current.Reasoning ?? previous.Reasoning,
            current.CacheWrite ?? previous.CacheWrite,
            current.Total ?? previous.Total);
    }

    private static bool DidReset(CodexTokenCounters current, CodexTokenCounters previous)
    {
        if (current.Total.HasValue && previous.Total.HasValue)
        {
            return current.Total.Value < previous.Total.Value;
        }

        return Decreased(current.Input, previous.Input) ||
        Decreased(current.CachedInput, previous.CachedInput) ||
        Decreased(current.Output, previous.Output) ||
        Decreased(current.Reasoning, previous.Reasoning) ||
        Decreased(current.CacheWrite, previous.CacheWrite) ||
        Decreased(current.Total, previous.Total);
    }

    private static bool Decreased(long? current, long? previous) =>
        current.HasValue && previous.HasValue && current.Value < previous.Value;

    private static CodexTokenCounters Delta(
        CodexTokenCounters current,
        CodexTokenCounters previous) => new(
            SaturatingDelta(current.Input, previous.Input),
            SaturatingDelta(current.CachedInput, previous.CachedInput),
            SaturatingDelta(current.Output, previous.Output),
            SaturatingDelta(current.Reasoning, previous.Reasoning),
            SaturatingDelta(current.CacheWrite, previous.CacheWrite),
            SaturatingDelta(current.Total, previous.Total));

    private static long? SaturatingDelta(long? current, long? previous)
    {
        if (!current.HasValue)
        {
            return null;
        }

        long previousValue = previous.GetValueOrDefault();
        return current.Value >= previousValue ? current.Value - previousValue : 0;
    }

    private static UsageRead ReadUsage(JsonElement info, string propertyName)
    {
        if (info.ValueKind is not JsonValueKind.Object ||
            !info.TryGetProperty(propertyName, out JsonElement usage) ||
            usage.ValueKind is JsonValueKind.Null)
        {
            return new UsageRead(null, true);
        }

        if (usage.ValueKind is not JsonValueKind.Object)
        {
            return new UsageRead(null, false);
        }

        if (!ReadCounter(usage, "input_tokens", out long? input) ||
            !ReadCounter(usage, "cached_input_tokens", out long? cachedInput) ||
            !ReadCounter(usage, "output_tokens", out long? output) ||
            !ReadCounter(usage, "reasoning_output_tokens", out long? reasoning) ||
            !ReadOptionalCounter(
                usage,
                "cache_write_tokens",
                "cache_creation_input_tokens",
                out long? cacheWrite) ||
            !ReadCounter(usage, "total_tokens", out long? total))
        {
            return new UsageRead(null, false);
        }

        var counters = new CodexTokenCounters(
            input,
            cachedInput,
            output,
            reasoning,
            cacheWrite,
            total);
        return new UsageRead(
            HasAnyCounter(counters) ? counters : null,
            true);
    }

    private static bool HasAnyCounter(CodexTokenCounters counters) =>
        counters.Input.HasValue ||
        counters.CachedInput.HasValue ||
        counters.Output.HasValue ||
        counters.Reasoning.HasValue ||
        counters.CacheWrite.HasValue ||
        counters.Total.HasValue;

    private static bool ReadOptionalCounter(
        JsonElement parent,
        string firstName,
        string secondName,
        out long? value)
    {
        if (parent.TryGetProperty(firstName, out _))
        {
            return ReadCounter(parent, firstName, out value);
        }

        return ReadCounter(parent, secondName, out value);
    }

    private static bool ReadCounter(
        JsonElement parent,
        string propertyName,
        out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind is not JsonValueKind.Number ||
            !property.TryGetInt64(out long parsed) ||
            parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static CodexParseState RestoreReplayTarget(
        CodexParseState state,
        CodexReplayTargetState target) => state with
        {
            ThreadId = target.SessionId,
            ParentSessionId = target.ParentSessionId,
            ForkedFromSessionId = target.ForkedFromSessionId,
            SessionKind = target.SessionKind,
            ParentRelationOrigin = target.ParentRelationOrigin,
            ParentRelationState = target.ParentRelationState,
            CompatibilityLevel = target.CompatibilityLevel,
            SessionRole = target.SessionRole,
            AgentPathHash = target.AgentPathHash,
            AgentLeafHash = target.AgentLeafHash,
            CurrentRawModel = target.CurrentRawModel,
            CurrentProviderId = target.CurrentProviderId,
            ProjectId = target.ProjectId,
            ProjectPath = target.ProjectPath,
            ProjectRepositoryIdentityHash = target.ProjectRepositoryIdentityHash,
            PreviousCumulative = null,
            CurrentTurnIdHash = null,
            CurrentEffort = null,
            CurrentTurnTimestampUtc = null,
            CurrentTurnStartedAtUtc = null,
            CurrentTurnCompletedAtUtc = null,
            CurrentPromptPreview = null,
            CurrentUserMessageCount = 0,
            PendingToolNames = null,
            ReplayTarget = null,
            IsReplayTargetContextPending = false,
            IsHistoryReplay = false
        };

    private static CodexParseState CompleteReplayBoundary(CodexParseState state)
    {
        if (!state.IsHistoryReplay)
        {
            return state;
        }

        CodexReplayTargetState? target = state.ReplayTarget;
        if (target is null)
        {
            return state with { IsHistoryReplay = false };
        }

        if (TryGetUuidV7TimestampMilliseconds(target.SessionId, out _))
        {
            return state;
        }

        return string.Equals(
            state.ThreadId,
            target.SessionId,
            StringComparison.Ordinal)
                ? RestoreReplayTarget(state, target)
                : state with { IsHistoryReplay = false };
    }

    private static bool ShouldRestoreReplayTargetAtTaskStarted(
        CodexParseState state,
        CodexReplayTargetState target,
        string turnId)
    {
        string? replaySourcePath =
            state.ProjectPath ?? target.ReplaySourceProjectPath;
        return IsProvablyActiveReplayTurn(state, target, turnId) &&
            (string.Equals(
                 state.ThreadId,
                 target.SessionId,
                 StringComparison.Ordinal) ||
             (target.ProjectPath is not null &&
              replaySourcePath is not null &&
              string.Equals(
                  target.ProjectPath,
                  replaySourcePath,
                  StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ShouldAwaitReplayTargetContextAtTaskStarted(
        CodexParseState state,
        CodexReplayTargetState target,
        string turnId)
    {
        string? replaySourcePath =
            state.ProjectPath ?? target.ReplaySourceProjectPath;
        return IsProvablyActiveReplayTurn(state, target, turnId) &&
            target.ProjectPath is not null &&
            replaySourcePath is not null &&
            !string.Equals(
                target.ProjectPath,
                replaySourcePath,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProvablyActiveReplayTurn(
        CodexParseState state,
        CodexReplayTargetState target,
        string turnId)
    {
        if (!state.IsHistoryReplay ||
            !TryGetUuidV7TimestampMilliseconds(
                target.SessionId,
                out long targetTimestamp) ||
            !TryGetUuidV7TimestampMilliseconds(turnId, out long turnTimestamp))
        {
            return false;
        }

        // Equal-millisecond UUIDv7 values do not prove creation order. Keep
        // replay suppression in that ambiguous case instead of guessing.
        return turnTimestamp > targetTimestamp;
    }

    private static bool TryGetUuidV7TimestampMilliseconds(
        string value,
        out long timestamp)
    {
        timestamp = 0;
        if (value.Length != 36 ||
            value[8] != '-' ||
            value[13] != '-' ||
            value[18] != '-' ||
            value[23] != '-' ||
            value[14] != '7' ||
            !Guid.TryParseExact(value, "D", out _))
        {
            return false;
        }

        Span<char> timestampHex = stackalloc char[12];
        value.AsSpan(0, 8).CopyTo(timestampHex);
        value.AsSpan(9, 4).CopyTo(timestampHex[8..]);
        return long.TryParse(
            timestampHex,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out timestamp);
    }

    private static bool IsReplayBoundary(string? type, JsonElement payload)
    {
        string? payloadType = StringProperty(payload, "type");
        return IsInterAgentFamily(type) ||
            IsInterAgentFamily(payloadType) ||
            (string.Equals(type, "event_msg", StringComparison.Ordinal) &&
             string.Equals(payloadType, "thread_settings_applied", StringComparison.Ordinal));
    }

    private static bool IsInterAgentFamily(string? value) =>
        value is not null && value.StartsWith(
            "inter_agent_communication",
            StringComparison.Ordinal);

    private static bool HasSubagentSource(JsonElement payload)
    {
        StateStringRead threadSource = ReadStateString(payload, "thread_source");
        if (threadSource.IsValid &&
            string.Equals(
                threadSource.Value,
                "subagent",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty("source", out JsonElement source))
        {
            return false;
        }

        if (source.ValueKind is JsonValueKind.Object &&
            source.TryGetProperty("subagent", out JsonElement subagent))
        {
            return subagent.ValueKind is not JsonValueKind.Null and not JsonValueKind.False;
        }

        return source.ValueKind is JsonValueKind.String &&
            source.GetString()?.Contains("subagent", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static StateStringRead ReadSubagentParentSessionId(JsonElement payload)
    {
        JsonElement source = ObjectProperty(payload, "source");
        if (source.ValueKind is not JsonValueKind.Object ||
            !source.TryGetProperty("subagent", out JsonElement subagent) ||
            subagent.ValueKind is not JsonValueKind.Object)
        {
            return StateStringRead.Missing;
        }

        StateStringRead direct = ReadStateString(subagent, "parent_thread_id");
        if (direct.IsPresent)
        {
            return direct;
        }

        if (!subagent.TryGetProperty("thread_spawn", out JsonElement threadSpawn))
        {
            return StateStringRead.Missing;
        }

        if (threadSpawn.ValueKind is not JsonValueKind.Object)
        {
            return StateStringRead.Invalid;
        }

        StateStringRead nested = ReadStateString(threadSpawn, "parent_thread_id");
        return nested.IsPresent ? nested : StateStringRead.Invalid;
    }

    private static bool HasSessionScopedState(CodexParseState state) =>
        state.ParentSessionId is not null ||
        state.ForkedFromSessionId is not null ||
        state.SessionKind is not SessionKind.Unknown ||
        state.CurrentRawModel is not null ||
        state.CurrentProviderId is not null ||
        state.ProjectId is not null ||
        state.ProjectPath is not null ||
        state.ProjectRepositoryIdentityHash is not null ||
        state.PreviousCumulative is not null ||
        state.CurrentTurnIdHash is not null ||
        state.CurrentEffort is not null ||
        state.CurrentTurnTimestampUtc is not null ||
        state.CurrentTurnStartedAtUtc is not null ||
        state.CurrentTurnCompletedAtUtc is not null ||
        state.CurrentPromptPreview is not null ||
        state.CurrentUserMessageCount != 0 ||
        state.PendingToolNames is not null ||
        state.SessionRole is not SessionRole.Unknown ||
        state.AgentPathHash is not null ||
        state.AgentLeafHash is not null ||
        state.ReplayTarget is not null ||
        state.IsReplayTargetContextPending;

    private static ParentRelationRead ResolveDirectParent(
        string? threadId,
        StateStringRead topLevel,
        StateStringRead nested,
        StateStringRead sessionFallback)
    {
        if (IsInvalid(topLevel) || IsInvalid(nested))
        {
            return ParentRelationRead.Uncertain;
        }

        if (topLevel.IsValid &&
            nested.IsValid &&
            !string.Equals(topLevel.Value, nested.Value, StringComparison.Ordinal))
        {
            return ParentRelationRead.Uncertain;
        }

        StateStringRead selected;
        SessionRelationOrigin origin;
        if (topLevel.IsValid)
        {
            selected = topLevel;
            origin = SessionRelationOrigin.TopLevelParentThreadId;
        }
        else if (nested.IsValid)
        {
            selected = nested;
            origin = SessionRelationOrigin.NestedSubagentParentThreadId;
        }
        else if (sessionFallback.IsValid)
        {
            selected = sessionFallback;
            origin = SessionRelationOrigin.SessionIdFallback;
        }
        else
        {
            return ParentRelationRead.None;
        }

        return string.Equals(threadId, selected.Value, StringComparison.Ordinal)
            ? ParentRelationRead.Uncertain
            : ParentRelationRead.Confirmed(selected.Value!, origin);
    }

    private static string? NormalizeModel(string? rawModel)
    {
        if (string.IsNullOrWhiteSpace(rawModel))
        {
            return null;
        }

        string normalized = rawModel.Trim().ToLowerInvariant();
        int separator = normalized.LastIndexOf('/');
        if (separator >= 0)
        {
            normalized = normalized[(separator + 1)..];
        }

        normalized = ModelDateSuffixRegex().Replace(normalized, string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static ProjectRead ReadProject(
        JsonElement payload,
        string? repositoryIdentityHash = null)
    {
        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty("cwd", out JsonElement property))
        {
            return ProjectRead.Missing;
        }

        if (property.ValueKind is not JsonValueKind.String)
        {
            return ProjectRead.Invalid;
        }

        string? repositoryUrl = null;
        if (payload.TryGetProperty("git", out JsonElement git) &&
            git.ValueKind is JsonValueKind.Object &&
            git.TryGetProperty(
                "repository_url",
                out JsonElement repositoryUrlProperty) &&
            repositoryUrlProperty.ValueKind is JsonValueKind.String)
        {
            repositoryUrl = repositoryUrlProperty.GetString();
        }

        CodexProjectIdentity project;
        bool created = repositoryUrl is not null
            ? CodexProjectIdentity.TryCreate(
                property.GetString(),
                repositoryUrl,
                out project)
            : repositoryIdentityHash is not null
                ? CodexProjectIdentity.TryCreateFromRepositoryHash(
                property.GetString(),
                repositoryIdentityHash,
                out project)
                : CodexProjectIdentity.TryCreate(
                    property.GetString(),
                    out project);
        return !created
            ? ProjectRead.Invalid
            : ProjectRead.Valid(
                project.ProjectId,
                project.ProjectPath,
                project.RepositoryIdentityHash);
    }

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        string? raw = StringProperty(root, "timestamp");
        return raw is not null &&
            ExplicitTimeZoneRegex().IsMatch(raw) &&
            DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp) &&
            (timestamp = timestamp.ToUniversalTime()).Offset == TimeSpan.Zero;
    }

    private static string? StringProperty(JsonElement parent, string propertyName) =>
        parent.ValueKind is JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static StateStringRead ReadStateString(
        JsonElement parent,
        string propertyName)
    {
        if (parent.ValueKind is not JsonValueKind.Object ||
            !parent.TryGetProperty(propertyName, out JsonElement property))
        {
            return StateStringRead.Missing;
        }

        if (property.ValueKind is not JsonValueKind.String)
        {
            return StateStringRead.Invalid;
        }

        string? value = property.GetString();
        return value is { Length: > 0 and <= CodexCursor.MaxStateStringCharacters } &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.Any(char.IsControl)
                ? StateStringRead.Valid(value)
                : StateStringRead.Invalid;
    }

    private static bool IsInvalid(StateStringRead value) =>
        value.IsPresent && !value.IsValid;

    private static string? Resolve(StateStringRead value, string? existing) =>
        !value.IsPresent
            ? existing
            : value.IsValid
                ? value.Value
                : null;

    private static bool IsInvalid(ProjectRead value) =>
        value.IsPresent && !value.IsValid;

    private static string? ResolveProjectId(ProjectRead value, string? existing) =>
        !value.IsPresent
            ? existing
            : value.IsValid
                ? value.Id
                : null;

    private static string? ResolveProjectPath(ProjectRead value, string? existing) =>
        !value.IsPresent
            ? existing
            : value.IsValid
                ? value.Path
                : null;

    private static string? ResolveProjectRepositoryIdentityHash(
        ProjectRead value,
        string? existing) =>
        !value.IsPresent
            ? existing
            : value.IsValid
                ? value.RepositoryIdentityHash
                : null;

    private static JsonElement ObjectProperty(JsonElement parent, string propertyName) =>
        parent.ValueKind is JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.Object
            ? value
            : default;

    private static TokenMetric ExactMetric(long? value) => value.HasValue
        ? TokenMetric.Exact(value.Value)
        : TokenMetric.Unavailable;

    private static TokenMetric DerivedMetric(long? value) => value.HasValue
        ? new TokenMetric(value.Value, MetricOrigin.Derived)
        : TokenMetric.Unavailable;

    private static long SaturatingAdd(long first, long second, long third)
    {
        long value = SaturatingAdd(first, second);
        return SaturatingAdd(value, third);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static CodexParseResult Diagnostic(
        CodexParseState state,
        CodexEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
            null,
            state,
            NewDiagnostic(context, line, code, message));

    private static CollectorDiagnostic NewDiagnostic(
        CodexEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
            code,
            message,
            context.Entity.SourceEntityId,
            line.ByteOffset);

    [GeneratedRegex("-(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|[0-9]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelDateSuffixRegex();

    [GeneratedRegex("(?:[zZ]|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitTimeZoneRegex();

    [GeneratedRegex(
        "<\\s*(?:image|audio)\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentMarkupRegex();

    private readonly record struct UsageRead(
        CodexTokenCounters? Counters,
        bool IsValid);

    private readonly record struct CanonicalIdentityRead(
        bool IsValid,
        string? Value)
    {
        public static CanonicalIdentityRead Invalid { get; } = new(false, null);

        public static CanonicalIdentityRead Valid(string value) => new(true, value);
    }

    private readonly record struct ExplicitIdentityRead(
        bool IsPresent,
        bool IsValid,
        string? Scope,
        string? FieldName,
        string? Value)
    {
        public static ExplicitIdentityRead Missing { get; } =
            new(false, true, null, null, null);

        public static ExplicitIdentityRead Invalid { get; } =
            new(true, false, null, null, null);

        public static ExplicitIdentityRead Valid(
            string scope,
            string fieldName,
            string value) => new(true, true, scope, fieldName, value);
    }

    private readonly record struct StateStringRead(
        bool IsPresent,
        bool IsValid,
        string? Value)
    {
        public static StateStringRead Missing { get; } = new(false, false, null);

        public static StateStringRead Invalid { get; } = new(true, false, null);

        public static StateStringRead Valid(string value) => new(true, true, value);
    }

    private readonly record struct ProjectRead(
        bool IsPresent,
        bool IsValid,
        string? Id,
        string? Path,
        string? RepositoryIdentityHash)
    {
        public static ProjectRead Missing { get; } =
            new(false, false, null, null, null);

        public static ProjectRead Invalid { get; } =
            new(true, false, null, null, null);

        public static ProjectRead Valid(
            string id,
            string path,
            string? repositoryIdentityHash) =>
            new(true, true, id, path, repositoryIdentityHash);
    }

    private readonly record struct ParentRelationRead(
        string? ParentSessionId,
        SessionRelationOrigin Origin,
        SessionRelationState State)
    {
        public static ParentRelationRead None { get; } = new(
            null,
            SessionRelationOrigin.None,
            SessionRelationState.None);

        public static ParentRelationRead Uncertain { get; } = new(
            null,
            SessionRelationOrigin.None,
            SessionRelationState.Uncertain);

        public static ParentRelationRead Confirmed(
            string parentSessionId,
            SessionRelationOrigin origin) => new(
                parentSessionId,
                origin,
                SessionRelationState.Confirmed);
    }
}
