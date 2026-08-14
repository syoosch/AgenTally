namespace AgenTally.Core.Collectors.WorkBuddy;

public sealed record WorkBuddyToolReference(
    string CallIdHash,
    string Name);

public sealed record WorkBuddyParseState(
    string? SessionId = null,
    string? CurrentTurnIdHash = null,
    DateTimeOffset? CurrentTurnStartedAtUtc = null,
    string? CurrentPromptPreview = null,
    int CurrentUserMessageCount = 0,
    string? ProjectId = null,
    string? ProjectPath = null,
    string? ProjectRepositoryIdentityHash = null,
    string? SessionName = null,
    DateTimeOffset? SessionNameUpdatedAtUtc = null,
    string[]? CurrentTurnRecordIdHashes = null,
    WorkBuddyToolReference[]? PendingTools = null,
    DateTimeOffset? LastTimestampUtc = null);
