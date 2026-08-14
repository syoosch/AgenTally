namespace AgenTally.Core.Collectors.KimiCode;

public sealed record KimiCodeToolReference(
    int Ordinal,
    string Name);

public sealed record KimiCodePendingStep(
    string StepIdHash,
    string TurnIdHash,
    DateTimeOffset StartedAtUtc,
    string? RequestModel,
    IReadOnlyList<KimiCodeToolReference> Tools);

public sealed record KimiCodePendingCall(
    string EventDedupKey,
    string TurnIdHash,
    DateTimeOffset CompletedAtUtc,
    string? RequestModel,
    long InputOther,
    long InputCacheRead,
    long InputCacheCreation,
    long Output,
    IReadOnlyList<KimiCodeToolReference> Tools);

public sealed record KimiCodePendingUsage(
    string Model,
    long InputOther,
    long InputCacheRead,
    long InputCacheCreation,
    long Output,
    long TotalInput,
    long NormalizedTotal);

public sealed record KimiCodeActiveGoal(
    string GoalIdHash,
    string PromptOriginTurnIdHash);

public sealed record KimiCodeTaskOrigin(
    string TaskIdHash,
    string? PromptOriginTurnIdHash);

public sealed record KimiCodeParseState(
    bool ProtocolConfirmed = false,
    string? CurrentTurnIdHash = null,
    string? CurrentPromptOriginTurnIdHash = null,
    DateTimeOffset? CurrentTurnStartedAtUtc = null,
    string? CurrentPromptPreview = null,
    int CurrentUserMessageCount = 0,
    KimiCodeActiveGoal? ActiveGoal = null,
    string? PendingGoalIdHash = null,
    bool GoalLifecycleAmbiguous = false,
    string? PendingGoalContinuationOriginTurnIdHash = null,
    bool PendingGoalContinuationAmbiguous = false,
    string? PendingBackgroundTaskOriginTurnIdHash = null,
    bool PendingBackgroundTaskAmbiguous = false,
    IReadOnlyList<KimiCodeTaskOrigin>? TaskOrigins = null,
    string? PendingTaskOriginTurnIdHash = null,
    bool PendingTaskOriginAmbiguous = false,
    KimiCodePendingStep? PendingStep = null,
    KimiCodePendingUsage? PendingUsage = null,
    KimiCodePendingCall? PendingCall = null,
    DateTimeOffset? LastTimestampUtc = null);
