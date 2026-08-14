using System.Text.Json;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed record KimiCodeEventContext(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string SourceFingerprint,
    DateTimeOffset ImportedAtUtc,
    KimiCodeEntityMetadata Metadata);

public sealed record KimiCodeParseResult(
    UsageEvent? Event,
    UsageSessionMetadata? SessionMetadata,
    UsageTurnMetadata? TurnMetadata,
    IReadOnlyList<UsageEventToolMetadata> EventTools,
    KimiCodeParseState State,
    CollectorDiagnostic? Diagnostic);

public sealed class KimiCodeWireParser
{
    public const string CurrentParserVersion = "kimi-code-wire-v8";

    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 512;
    private const int MaxToolsPerCall = 256;
    private const int MaxTrackedTaskOrigins = 256;

    public KimiCodeParseResult ParseLine(
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                line.Utf8,
                new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return Incompatible(
                state,
                context,
                line,
                "kimi_code.invalid_json",
                "A Kimi Code wire record was not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Incompatible(
                    state,
                    context,
                    line,
                    "kimi_code.invalid_record",
                    "A Kimi Code wire record was not an object.");
            }

            string? type = KimiCodeTextNormalizer.ReadBoundedString(
                root,
                "type",
                128);
            if (string.Equals(type, "metadata", StringComparison.Ordinal))
            {
                return ParseMetadata(root, line, state, context);
            }

            if (!state.ProtocolConfirmed)
            {
                return Incompatible(
                    state,
                    context,
                    line,
                    "kimi_code.unsupported_protocol",
                    "Kimi Code wire protocol semantics could not be confirmed.");
            }

            DateTimeOffset? timestamp = ReadUnixMilliseconds(root, "time");
            KimiCodeParseState nextState = timestamp.HasValue
                ? state with { LastTimestampUtc = timestamp }
                : state;
            return type switch
            {
                "turn.prompt" => ParsePrompt(
                    root,
                    line,
                    nextState,
                    context,
                    isSteer: false),
                "turn.steer" => ParsePrompt(
                    root,
                    line,
                    nextState,
                    context,
                    isSteer: true),
                "task.started" => ParseTaskStarted(root, nextState),
                "context.append_message" => ParseContextMessage(
                    root,
                    nextState),
                "goal.create" => ParseGoalCreate(root, nextState),
                "goal.clear" => Empty(nextState with
                {
                    ActiveGoal = null,
                    PendingGoalIdHash = null,
                    GoalLifecycleAmbiguous = false,
                    PendingGoalContinuationOriginTurnIdHash = null,
                    PendingGoalContinuationAmbiguous = false
                }),
                "context.append_loop_event" => ParseLoopEvent(
                    root,
                    line,
                    nextState,
                    context),
                "llm.request" => ParseRequest(root, nextState),
                "usage.record" => ParseUsage(
                    root,
                    line,
                    nextState,
                    context),
                _ => Empty(nextState)
            };
        }
    }

    private static KimiCodeParseResult ParseMetadata(
        JsonElement root,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        string? protocol = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "protocol_version",
            32);
        bool supported = protocol is not null &&
            protocol.Split('.', 2)[0] == "1";
        if (!supported)
        {
            return Incompatible(
                state with { ProtocolConfirmed = false },
                context,
                line,
                "kimi_code.unsupported_protocol",
                "Kimi Code wire protocol semantics could not be confirmed.");
        }

        KimiCodeParseState next = state with
        {
            ProtocolConfirmed = true,
            LastTimestampUtc = ReadUnixMilliseconds(root, "created_at") ??
                state.LastTimestampUtc
        };
        return new KimiCodeParseResult(
            null,
            CreateSession(next, context, CompatibilityLevel.FullyCompatible),
            null,
            [],
            next,
            null);
    }

    private static KimiCodeParseResult ParsePrompt(
        JsonElement root,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context,
        bool isSteer)
    {
        if (HasOriginKind(root, "background_task"))
        {
            return Empty(RegisterBackgroundTaskContinuation(state));
        }

        if (HasOriginKind(root, "task"))
        {
            return Empty(RegisterTaskContinuation(root, state));
        }

        if (!HasOriginKind(root, "user"))
        {
            return Empty(state);
        }

        if (!state.LastTimestampUtc.HasValue ||
            !root.TryGetProperty("input", out JsonElement input) ||
            input.ValueKind != JsonValueKind.Array)
        {
            return Empty(
                state,
                Diagnostic(
                    context,
                    line,
                    "kimi_code.invalid_prompt_boundary",
                    "A Kimi Code Prompt boundary did not provide reliable input shape or time."));
        }

        if (isSteer)
        {
            if (state.CurrentTurnIdHash is null ||
                !state.CurrentTurnStartedAtUtc.HasValue)
            {
                return Empty(state);
            }

            KimiCodeParseState steerNext = state with
            {
                CurrentUserMessageCount =
                    state.CurrentUserMessageCount == int.MaxValue
                        ? int.MaxValue
                        : state.CurrentUserMessageCount + 1
            };
            return new KimiCodeParseResult(
                null,
                CreateSession(
                    steerNext,
                    context,
                    CompatibilityLevel.FullyCompatible),
                CreateTurn(steerNext, context, completedAtUtc: null),
                [],
                steerNext,
                null);
        }

        KimiCodeParseState next = state with
        {
            CurrentTurnIdHash = null,
            CurrentPromptOriginTurnIdHash = null,
            CurrentTurnStartedAtUtc = state.LastTimestampUtc,
            CurrentPromptPreview =
                KimiCodeTextNormalizer.BuildPromptPreview(input),
            CurrentUserMessageCount = 1,
            PendingGoalIdHash = null,
            GoalLifecycleAmbiguous = state.GoalLifecycleAmbiguous ||
                state.PendingGoalIdHash is not null,
            PendingGoalContinuationOriginTurnIdHash = null,
            PendingGoalContinuationAmbiguous = false,
            PendingBackgroundTaskOriginTurnIdHash = null,
            PendingBackgroundTaskAmbiguous = false,
            PendingTaskOriginTurnIdHash = null,
            PendingTaskOriginAmbiguous = false,
            PendingStep = null,
            PendingUsage = null,
            PendingCall = null
        };
        return new KimiCodeParseResult(
            null,
            CreateSession(next, context, CompatibilityLevel.FullyCompatible),
            null,
            [],
            next,
            null);
    }

    private static KimiCodeParseResult ParseTaskStarted(
        JsonElement root,
        KimiCodeParseState state)
    {
        if (!root.TryGetProperty("info", out JsonElement info) ||
            info.ValueKind != JsonValueKind.Object)
        {
            return Empty(state);
        }

        string? taskId = KimiCodeTextNormalizer.ReadBoundedString(
            info,
            "taskId",
            MaxIdentityCharacters);
        if (taskId is null)
        {
            return Empty(state);
        }

        string taskIdHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-task",
            taskId);
        string? promptOriginTurnIdHash = CurrentCanonicalPromptTurnIdHash(state);
        IReadOnlyList<KimiCodeTaskOrigin> current = state.TaskOrigins ?? [];
        int existingIndex = current
            .Select((task, index) => (task, index))
            .Where(value => string.Equals(
                value.task.TaskIdHash,
                taskIdHash,
                StringComparison.Ordinal))
            .Select(value => value.index)
            .DefaultIfEmpty(-1)
            .Single();
        if (existingIndex >= 0)
        {
            KimiCodeTaskOrigin existing = current[existingIndex];
            string? resolvedOrigin =
                existing.PromptOriginTurnIdHash is not null &&
                promptOriginTurnIdHash is not null &&
                string.Equals(
                    existing.PromptOriginTurnIdHash,
                    promptOriginTurnIdHash,
                    StringComparison.Ordinal)
                    ? existing.PromptOriginTurnIdHash
                    : null;
            if (string.Equals(
                    resolvedOrigin,
                    existing.PromptOriginTurnIdHash,
                    StringComparison.Ordinal))
            {
                return Empty(state);
            }

            var updated = current.ToArray();
            updated[existingIndex] = existing with
            {
                PromptOriginTurnIdHash = resolvedOrigin
            };
            return Empty(state with { TaskOrigins = updated });
        }

        if (current.Count >= MaxTrackedTaskOrigins)
        {
            return Empty(state);
        }

        return Empty(state with
        {
            TaskOrigins =
            [.. current, new KimiCodeTaskOrigin(
                taskIdHash,
                promptOriginTurnIdHash)]
        });
    }

    private static KimiCodeParseState RegisterTaskContinuation(
        JsonElement root,
        KimiCodeParseState state)
    {
        string? taskId = root.TryGetProperty(
                "origin",
                out JsonElement origin) &&
            origin.ValueKind == JsonValueKind.Object
                ? KimiCodeTextNormalizer.ReadBoundedString(
                    origin,
                    "taskId",
                    MaxIdentityCharacters)
                : null;
        string? promptOriginTurnIdHash = null;
        string? taskIdHash = null;
        IReadOnlyList<KimiCodeTaskOrigin> current = state.TaskOrigins ?? [];
        if (taskId is not null)
        {
            taskIdHash = KimiCodeSourceIdentity.HashIdentity(
                "kimi-code-task",
                taskId);
            promptOriginTurnIdHash = current
                .Where(task => string.Equals(
                    task.TaskIdHash,
                    taskIdHash,
                    StringComparison.Ordinal))
                .Select(task => task.PromptOriginTurnIdHash)
                .SingleOrDefault();
        }

        bool conflictingOrigin =
            state.PendingTaskOriginTurnIdHash is not null &&
            !string.Equals(
                state.PendingTaskOriginTurnIdHash,
                promptOriginTurnIdHash,
                StringComparison.Ordinal);
        bool ambiguous = state.PendingTaskOriginAmbiguous ||
            promptOriginTurnIdHash is null ||
            conflictingOrigin;
        return state with
        {
            TaskOrigins = taskIdHash is null
                ? state.TaskOrigins
                : current
                    .Where(task => !string.Equals(
                        task.TaskIdHash,
                        taskIdHash,
                        StringComparison.Ordinal))
                    .ToArray(),
            PendingTaskOriginTurnIdHash = ambiguous
                ? null
                : promptOriginTurnIdHash,
            PendingTaskOriginAmbiguous = ambiguous
        };
    }

    private static string? CurrentCanonicalPromptTurnIdHash(
        KimiCodeParseState state) =>
        state.CurrentPromptOriginTurnIdHash ??
        (state.CurrentUserMessageCount > 0
            ? state.CurrentTurnIdHash
            : null);

    private static KimiCodeParseResult ParseContextMessage(
        JsonElement root,
        KimiCodeParseState state)
    {
        if (!root.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                KimiCodeTextNormalizer.ReadBoundedString(message, "role", 32),
                "user",
                StringComparison.Ordinal) ||
            !message.TryGetProperty("origin", out JsonElement origin) ||
            origin.ValueKind != JsonValueKind.Object)
        {
            return Empty(state);
        }

        string? originKind = KimiCodeTextNormalizer.ReadBoundedString(
            origin,
            "kind",
            64);
        if (string.Equals(
                originKind,
                "background_task",
                StringComparison.Ordinal))
        {
            return Empty(RegisterBackgroundTaskContinuation(state));
        }

        if (!string.Equals(
                originKind,
                "system_trigger",
                StringComparison.Ordinal) ||
            !string.Equals(
                KimiCodeTextNormalizer.ReadBoundedString(origin, "name", 128),
                "goal_continuation",
                StringComparison.Ordinal))
        {
            return Empty(state);
        }

        string? promptOriginTurnIdHash =
            state.GoalLifecycleAmbiguous
                ? null
                : state.ActiveGoal?.PromptOriginTurnIdHash;
        bool ambiguous = state.PendingGoalContinuationAmbiguous ||
            state.PendingGoalContinuationOriginTurnIdHash is not null;
        return Empty(state with
        {
            PendingGoalContinuationOriginTurnIdHash = ambiguous
                ? null
                : promptOriginTurnIdHash,
            PendingGoalContinuationAmbiguous = ambiguous ||
                promptOriginTurnIdHash is null
        });
    }

    private static KimiCodeParseState RegisterBackgroundTaskContinuation(
        KimiCodeParseState state)
    {
        string? promptOriginTurnIdHash =
            state.CurrentPromptOriginTurnIdHash ??
            (state.CurrentUserMessageCount > 0
                ? state.CurrentTurnIdHash
                : null);
        bool conflictingOrigin =
            state.PendingBackgroundTaskOriginTurnIdHash is not null &&
            !string.Equals(
                state.PendingBackgroundTaskOriginTurnIdHash,
                promptOriginTurnIdHash,
                StringComparison.Ordinal);
        bool ambiguous = state.PendingBackgroundTaskAmbiguous ||
            promptOriginTurnIdHash is null ||
            conflictingOrigin;
        return state with
        {
            PendingBackgroundTaskOriginTurnIdHash = ambiguous
                ? null
                : promptOriginTurnIdHash,
            PendingBackgroundTaskAmbiguous = ambiguous
        };
    }

    private static bool HasOriginKind(JsonElement root, string expectedKind)
    {
        if (!root.TryGetProperty("origin", out JsonElement origin))
        {
            return false;
        }

        string? kind = origin.ValueKind switch
        {
            JsonValueKind.String => origin.GetString(),
            JsonValueKind.Object =>
                KimiCodeTextNormalizer.ReadBoundedString(origin, "kind", 64),
            _ => null
        };
        return string.Equals(kind, expectedKind, StringComparison.Ordinal);
    }

    private static KimiCodeParseResult ParseGoalCreate(
        JsonElement root,
        KimiCodeParseState state)
    {
        string? goalId = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "goalId",
            MaxIdentityCharacters);
        if (goalId is null)
        {
            return Empty(state with
            {
                ActiveGoal = null,
                PendingGoalIdHash = null,
                GoalLifecycleAmbiguous = true
            });
        }

        string goalIdHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-goal",
            goalId);
        string? promptOriginTurnIdHash =
            state.CurrentPromptOriginTurnIdHash ??
            (state.CurrentUserMessageCount > 0
                ? state.CurrentTurnIdHash
                : null);
        if (state.GoalLifecycleAmbiguous)
        {
            return Empty(state with
            {
                ActiveGoal = null,
                PendingGoalIdHash = null
            });
        }

        if (state.ActiveGoal is not null)
        {
            bool sameGoal = string.Equals(
                state.ActiveGoal.GoalIdHash,
                goalIdHash,
                StringComparison.Ordinal);
            bool sameOrigin = promptOriginTurnIdHash is null ||
                string.Equals(
                    state.ActiveGoal.PromptOriginTurnIdHash,
                    promptOriginTurnIdHash,
                    StringComparison.Ordinal);
            return sameGoal && sameOrigin
                ? Empty(state)
                : Empty(state with
                {
                    ActiveGoal = null,
                    PendingGoalIdHash = null,
                    GoalLifecycleAmbiguous = true
                });
        }

        if (state.PendingGoalIdHash is not null)
        {
            return string.Equals(
                    state.PendingGoalIdHash,
                    goalIdHash,
                    StringComparison.Ordinal)
                ? Empty(state)
                : Empty(state with
                {
                    PendingGoalIdHash = null,
                    GoalLifecycleAmbiguous = true
                });
        }

        if (promptOriginTurnIdHash is not null)
        {
            return Empty(state with
            {
                ActiveGoal = new KimiCodeActiveGoal(
                    goalIdHash,
                    promptOriginTurnIdHash),
                PendingGoalIdHash = null
            });
        }

        bool hasPendingUserPrompt =
            state.CurrentTurnStartedAtUtc.HasValue &&
            state.CurrentUserMessageCount > 0;
        return Empty(state with
        {
            ActiveGoal = null,
            PendingGoalIdHash = hasPendingUserPrompt ? goalIdHash : null,
            GoalLifecycleAmbiguous = !hasPendingUserPrompt
        });
    }

    private static KimiCodeParseResult ParseLoopEvent(
        JsonElement root,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        if (!root.TryGetProperty("event", out JsonElement loopEvent) ||
            loopEvent.ValueKind != JsonValueKind.Object)
        {
            return Empty(state);
        }

        string? eventType = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "type",
            128);
        return eventType switch
        {
            "step.begin" => ParseStepBegin(
                loopEvent,
                line,
                state,
                context),
            "tool.call" => ParseToolCall(loopEvent, state),
            "step.end" => ParseStepEnd(
                loopEvent,
                line,
                state,
                context),
            _ => Empty(state)
        };
    }

    private static KimiCodeParseResult ParseStepBegin(
        JsonElement loopEvent,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        string? stepId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "uuid",
            MaxIdentityCharacters);
        string? turnId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "turnId",
            MaxIdentityCharacters);
        if (stepId is null || turnId is null || !state.LastTimestampUtc.HasValue)
        {
            return Incompatible(
                state,
                context,
                line,
                "kimi_code.invalid_call_identity",
                "A Kimi Code step did not provide reliable call, turn, or time identity.");
        }

        string turnIdHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-turn",
            turnId);
        bool continuingTurn = string.Equals(
            state.CurrentTurnIdHash,
            turnIdHash,
            StringComparison.Ordinal);
        bool hasPendingPrompt = state.CurrentTurnIdHash is null &&
            state.CurrentTurnStartedAtUtc.HasValue &&
            state.CurrentUserMessageCount > 0;
        string? continuationOriginTurnIdHash =
            !continuingTurn &&
            TryResolveContinuationOrigin(state, out string? resolvedOrigin)
                ? resolvedOrigin
                : null;
        string? promptOriginTurnIdHash = continuingTurn
            ? state.CurrentPromptOriginTurnIdHash
            : continuationOriginTurnIdHash;
        KimiCodeActiveGoal? activeGoal = state.ActiveGoal;
        if (!continuingTurn &&
            !state.GoalLifecycleAmbiguous &&
            state.PendingGoalIdHash is not null &&
            continuationOriginTurnIdHash is null)
        {
            activeGoal = new KimiCodeActiveGoal(
                state.PendingGoalIdHash,
                turnIdHash);
        }

        KimiCodeParseState next = state with
        {
            CurrentTurnIdHash = turnIdHash,
            CurrentPromptOriginTurnIdHash = promptOriginTurnIdHash,
            CurrentTurnStartedAtUtc = continuingTurn
                ? state.CurrentTurnStartedAtUtc
                : hasPendingPrompt
                    ? state.CurrentTurnStartedAtUtc
                    : state.LastTimestampUtc,
            CurrentPromptPreview = continuingTurn || hasPendingPrompt
                ? state.CurrentPromptPreview
                : null,
            CurrentUserMessageCount = continuingTurn
                ? state.CurrentUserMessageCount
                : hasPendingPrompt
                    ? state.CurrentUserMessageCount
                    : 0,
            ActiveGoal = activeGoal,
            PendingGoalIdHash = null,
            PendingGoalContinuationOriginTurnIdHash = null,
            PendingGoalContinuationAmbiguous = false,
            PendingBackgroundTaskOriginTurnIdHash = null,
            PendingBackgroundTaskAmbiguous = false,
            PendingTaskOriginTurnIdHash = null,
            PendingTaskOriginAmbiguous = false,
            PendingStep = new KimiCodePendingStep(
                KimiCodeSourceIdentity.HashIdentity(
                    "kimi-code-step",
                    stepId),
                turnIdHash,
                state.LastTimestampUtc.Value,
                null,
                []),
            PendingUsage = null,
            PendingCall = null
        };
        return new KimiCodeParseResult(
            null,
            CreateSession(next, context, CompatibilityLevel.FullyCompatible),
            CreateTurn(next, context, completedAtUtc: null),
            [],
            next,
            null);
    }

    private static bool TryResolveContinuationOrigin(
        KimiCodeParseState state,
        out string? promptOriginTurnIdHash)
    {
        promptOriginTurnIdHash = null;
        if (state.PendingGoalContinuationAmbiguous ||
            state.PendingBackgroundTaskAmbiguous ||
            state.PendingTaskOriginAmbiguous)
        {
            return false;
        }

        string?[] origins =
        [
            state.PendingGoalContinuationOriginTurnIdHash,
            state.PendingBackgroundTaskOriginTurnIdHash,
            state.PendingTaskOriginTurnIdHash
        ];
        string[] resolved = origins
            .Where(static origin => origin is not null)
            .Select(static origin => origin!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (resolved.Length != 1)
        {
            return false;
        }

        promptOriginTurnIdHash = resolved[0];
        return true;
    }

    private static KimiCodeParseResult ParseRequest(
        JsonElement root,
        KimiCodeParseState state)
    {
        if (state.PendingStep is null)
        {
            return Empty(state);
        }

        string? model = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "model",
            MaxModelCharacters);
        return Empty(state with
        {
            PendingStep = state.PendingStep with { RequestModel = model }
        });
    }

    private static KimiCodeParseResult ParseToolCall(
        JsonElement loopEvent,
        KimiCodeParseState state)
    {
        KimiCodePendingStep? step = state.PendingStep;
        if (step is null || step.Tools.Count >= MaxToolsPerCall)
        {
            return Empty(state);
        }

        string? stepId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "stepUuid",
            MaxIdentityCharacters);
        string? turnId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "turnId",
            MaxIdentityCharacters);
        string? toolCallId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "toolCallId",
            MaxIdentityCharacters);
        string? name = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "name",
            128);
        if (stepId is null || turnId is null || toolCallId is null || name is null ||
            !string.Equals(
                KimiCodeSourceIdentity.HashIdentity("kimi-code-step", stepId),
                step.StepIdHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                KimiCodeSourceIdentity.HashIdentity("kimi-code-turn", turnId),
                step.TurnIdHash,
                StringComparison.Ordinal))
        {
            return Empty(state);
        }

        int ordinal = KimiCodeSourceIdentity.StableOrdinal(toolCallId);
        if (step.Tools.Any(value => value.Ordinal == ordinal))
        {
            return Empty(state);
        }

        return Empty(state with
        {
            PendingStep = step with
            {
                Tools = [.. step.Tools, new KimiCodeToolReference(ordinal, name)]
            }
        });
    }

    private static KimiCodeParseResult ParseStepEnd(
        JsonElement loopEvent,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        KimiCodePendingStep? step = state.PendingStep;
        string? stepId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "uuid",
            MaxIdentityCharacters);
        string? turnId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "turnId",
            MaxIdentityCharacters);
        string? messageId = KimiCodeTextNormalizer.ReadBoundedString(
            loopEvent,
            "messageId",
            MaxIdentityCharacters);
        if (step is null || stepId is null || turnId is null || messageId is null ||
            !state.LastTimestampUtc.HasValue ||
            !string.Equals(
                KimiCodeSourceIdentity.HashIdentity("kimi-code-step", stepId),
                step.StepIdHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                KimiCodeSourceIdentity.HashIdentity("kimi-code-turn", turnId),
                step.TurnIdHash,
                StringComparison.Ordinal) ||
            !TryReadUsage(
                loopEvent,
                out long inputOther,
                out long cacheRead,
                out long cacheCreation,
                out long output,
                out _,
                out _))
        {
            return Incompatible(
                state with
                {
                    PendingStep = null,
                    PendingUsage = null,
                    PendingCall = null
                },
                context,
                line,
                "kimi_code.invalid_usage",
                "A Kimi Code completed step did not provide reliable identity or Token usage.");
        }

        KimiCodeParseState next = state with
        {
            PendingStep = null,
            PendingCall = null
        };
        var call = new KimiCodePendingCall(
            KimiCodeSourceIdentity.HashIdentity(
                "kimi-code-step-event",
                $"{context.Metadata.SessionId}\0{stepId}"),
            step.TurnIdHash,
            state.LastTimestampUtc.Value,
            step.RequestModel,
            inputOther,
            cacheRead,
            cacheCreation,
            output,
            step.Tools);
        if (state.PendingUsage is null)
        {
            return Empty(next with { PendingCall = call });
        }

        if (!UsageMatches(call, state.PendingUsage))
        {
            return Incompatible(
                next with { PendingUsage = null },
                context,
                line,
                "kimi_code.invalid_usage_record",
                "A Kimi Code turn usage record did not match its completed step.");
        }

        return CompleteUsage(
            line,
            next with { PendingUsage = null },
            context,
            call,
            state.PendingUsage);
    }

    private static KimiCodeParseResult ParseUsage(
        JsonElement root,
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context)
    {
        string? scope = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "usageScope",
            64);
        if (!string.Equals(scope, "turn", StringComparison.Ordinal))
        {
            return Empty(state);
        }

        KimiCodePendingCall? call = state.PendingCall;
        string? model = KimiCodeTextNormalizer.ReadBoundedString(
            root,
            "model",
            MaxModelCharacters);
        if (model is null ||
            !TryReadUsage(
                root,
                out long inputOther,
                out long cacheRead,
                out long cacheCreation,
                out long output,
                out long totalInput,
                out long normalizedTotal))
        {
            return Incompatible(
                state with { PendingUsage = null, PendingCall = null },
                context,
                line,
                "kimi_code.invalid_usage_record",
                "A Kimi Code turn usage record did not match its completed step.");
        }

        var usage = new KimiCodePendingUsage(
            model,
            inputOther,
            cacheRead,
            cacheCreation,
            output,
            totalInput,
            normalizedTotal);
        if (call is not null)
        {
            if (!UsageMatches(call, usage))
            {
                return Incompatible(
                    state with { PendingCall = null },
                    context,
                    line,
                    "kimi_code.invalid_usage_record",
                    "A Kimi Code turn usage record did not match its completed step.");
            }

            return CompleteUsage(
                line,
                state with { PendingCall = null },
                context,
                call,
                usage);
        }

        if (state.PendingStep is not null && state.PendingUsage is null)
        {
            return Empty(state with { PendingUsage = usage });
        }

        return Incompatible(
            state with { PendingUsage = null, PendingCall = null },
            context,
            line,
            "kimi_code.invalid_usage_record",
            "A Kimi Code turn usage record did not match its completed step.");
    }

    private static bool UsageMatches(
        KimiCodePendingCall call,
        KimiCodePendingUsage usage) =>
        usage.InputOther == call.InputOther &&
        usage.InputCacheRead == call.InputCacheRead &&
        usage.InputCacheCreation == call.InputCacheCreation &&
        usage.Output == call.Output;

    private static KimiCodeParseResult CompleteUsage(
        JsonlLine line,
        KimiCodeParseState state,
        KimiCodeEventContext context,
        KimiCodePendingCall call,
        KimiCodePendingUsage usage)
    {
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(usage.TotalInput),
            UncachedInput = TokenMetric.Exact(usage.InputOther),
            CacheRead = TokenMetric.Exact(usage.InputCacheRead),
            CacheWrite = TokenMetric.Exact(usage.InputCacheCreation),
            Output = TokenMetric.Exact(usage.Output),
            Reasoning = TokenMetric.Unavailable,
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Unavailable,
            NormalizedTotal = TokenMetric.Exact(usage.NormalizedTotal),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Unknown
        };
        string rawModel = call.RequestModel ?? usage.Model;
        var usageEvent = new UsageEvent(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            $"{context.Entity.SourceEntityId}:line:{line.LineNumber}",
            call.EventDedupKey,
            context.Instance.SourceKind,
            call.CompletedAtUtc,
            context.ImportedAtUtc.ToUniversalTime(),
            new ModelIdentity
            {
                RawModel = rawModel,
                NormalizedModel = ModelIdentityCanonicalizer.Canonicalize(
                    rawModel,
                    "kimi-code"),
                RouteModelId = string.Equals(
                    rawModel,
                    usage.Model,
                    StringComparison.Ordinal)
                    ? null
                    : usage.Model,
                ProviderId = null,
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            tokens,
            CompletionState.Completed,
            DataQuality.Exact,
            CurrentParserVersion,
            context.SourceFingerprint,
            line.LineNumber)
        {
            SessionId = context.Metadata.SessionId,
            TurnIdHash = call.TurnIdHash,
            ProjectId = context.Metadata.ProjectId,
            ProjectPath = context.Metadata.ProjectPath,
            ProjectRepositoryIdentityHash =
                context.Metadata.ProjectRepositoryIdentityHash
        };
        IReadOnlyList<UsageEventToolMetadata> tools = call.Tools
            .Select(tool => new UsageEventToolMetadata(
                context.Instance.AgentId,
                context.Instance.SourceInstanceId,
                context.Entity.SourceEntityId,
                call.EventDedupKey,
                tool.Ordinal,
                tool.Name,
                CurrentParserVersion))
            .ToArray();
        return new KimiCodeParseResult(
            usageEvent,
            CreateSession(state, context, CompatibilityLevel.FullyCompatible),
            CreateTurn(state, context, call.CompletedAtUtc),
            tools,
            state,
            null);
    }

    private static UsageSessionMetadata CreateSession(
        KimiCodeParseState state,
        KimiCodeEventContext context,
        CompatibilityLevel compatibilityLevel)
    {
        KimiCodeEntityMetadata metadata = context.Metadata;
        return new UsageSessionMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            metadata.SessionId,
            metadata.SessionKind,
            metadata.DirectParentSessionId,
            null,
            metadata.RelationOrigin,
            metadata.RelationState,
            ReplayState.Active,
            compatibilityLevel,
            state.LastTimestampUtc ?? context.ImportedAtUtc.ToUniversalTime(),
            CurrentParserVersion)
        {
            ProjectId = metadata.ProjectId,
            ProjectPath = metadata.ProjectPath,
            ProjectRepositoryIdentityHash =
                metadata.ProjectRepositoryIdentityHash,
            SessionRole = metadata.SessionRole,
            AgentPathHash = metadata.AgentPathHash,
            AgentLeafHash = metadata.AgentLeafHash,
            SessionName = metadata.SessionName,
            SessionNameUpdatedAtUtc = metadata.SessionNameUpdatedAtUtc
        };
    }

    private static UsageTurnMetadata? CreateTurn(
        KimiCodeParseState state,
        KimiCodeEventContext context,
        DateTimeOffset? completedAtUtc)
    {
        if (state.CurrentTurnIdHash is null ||
            !state.CurrentTurnStartedAtUtc.HasValue)
        {
            return null;
        }

        return new UsageTurnMetadata(
            context.Instance.AgentId,
            context.Instance.SourceInstanceId,
            context.Entity.SourceEntityId,
            context.Metadata.SessionId,
            state.CurrentTurnIdHash,
            state.CurrentTurnStartedAtUtc.Value,
            completedAtUtc,
            state.CurrentPromptPreview,
            state.CurrentUserMessageCount,
            CurrentParserVersion,
            state.CurrentPromptOriginTurnIdHash);
    }

    private static bool TryReadUsage(
        JsonElement value,
        out long inputOther,
        out long cacheRead,
        out long cacheCreation,
        out long output,
        out long totalInput,
        out long normalizedTotal)
    {
        inputOther = cacheRead = cacheCreation = output =
            totalInput = normalizedTotal = 0;
        if (!value.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !TryReadNonnegativeInt64(usage, "inputOther", out inputOther) ||
            !TryReadNonnegativeInt64(usage, "inputCacheRead", out cacheRead) ||
            !TryReadNonnegativeInt64(
                usage,
                "inputCacheCreation",
                out cacheCreation) ||
            !TryReadNonnegativeInt64(usage, "output", out output))
        {
            return false;
        }

        try
        {
            totalInput = checked(inputOther + cacheRead + cacheCreation);
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

    private static KimiCodeParseResult Incompatible(
        KimiCodeParseState state,
        KimiCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        null,
        CreateSession(state, context, CompatibilityLevel.TemporarilyIncompatible),
        null,
        [],
        state,
        Diagnostic(context, line, code, message));

    private static KimiCodeParseResult Empty(
        KimiCodeParseState state,
        CollectorDiagnostic? diagnostic = null) => new(
        null,
        null,
        null,
        [],
        state,
        diagnostic);

    private static CollectorDiagnostic Diagnostic(
        KimiCodeEventContext context,
        JsonlLine line,
        string code,
        string message) => new(
        code,
        message,
        context.Entity.SourceEntityId,
        line.ByteOffset);
}
