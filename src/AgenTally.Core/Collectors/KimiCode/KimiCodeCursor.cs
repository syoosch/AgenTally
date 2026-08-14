using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed record KimiCodeCursor(
    JsonlCursor Jsonl,
    KimiCodeParseState State)
{
    private const int MaxStateStringCharacters = 1024;
    private const int MaxToolsPerCall = 256;
    private const int MaxTaskOrigins = 256;
    private const int MaxSerializedCursorCharacters =
        JsonlCursor.MaxSerializedCursorCharacters + 131_072;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static KimiCodeCursor Start { get; } = new(
        JsonlCursor.Start,
        new KimiCodeParseState());

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException(
                "Kimi Code collection cursor is invalid and cannot be serialized.");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        return json.Length <= MaxSerializedCursorCharacters
            ? json
            : throw new InvalidOperationException(
                "Kimi Code collection cursor exceeds its serialized size limit.");
    }

    public static KimiCodeCursor DeserializeOrStart(
        string? cursorJson,
        bool hasStoredCursor,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
        {
            diagnostic = hasStoredCursor ? InvalidCursorDiagnostic() : null;
            return Start;
        }

        if (cursorJson.Length > MaxSerializedCursorCharacters)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }

        try
        {
            KimiCodeCursor? cursor = JsonSerializer.Deserialize<KimiCodeCursor>(
                cursorJson,
                JsonOptions);
            if (cursor is null || !cursor.IsValid())
            {
                throw new JsonException("Cursor fields are invalid.");
            }

            diagnostic = null;
            return cursor;
        }
        catch (Exception exception)
            when (exception is JsonException
                or NotSupportedException
                or ArgumentException
                or FormatException)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }
    }

    private bool IsValid()
    {
        if (Jsonl is null ||
            State is null ||
            !Jsonl.TryGetPendingBytes(out _) ||
            !ValidHash(State.CurrentTurnIdHash) ||
            !ValidHash(State.CurrentPromptOriginTurnIdHash) ||
            !ValidUtc(State.CurrentTurnStartedAtUtc) ||
            !ValidPreview(State.CurrentPromptPreview) ||
            State.CurrentUserMessageCount < 0 ||
            !ValidGoal(State.ActiveGoal) ||
            !ValidHash(State.PendingGoalIdHash) ||
            !ValidHash(State.PendingGoalContinuationOriginTurnIdHash) ||
            !ValidHash(State.PendingBackgroundTaskOriginTurnIdHash) ||
            !ValidTaskOrigins(State.TaskOrigins) ||
            !ValidHash(State.PendingTaskOriginTurnIdHash) ||
            !ValidStep(State.PendingStep) ||
            !ValidUsage(State.PendingUsage) ||
            !ValidCall(State.PendingCall) ||
            !ValidUtc(State.LastTimestampUtc) ||
            !ValidFingerprint(Jsonl))
        {
            return false;
        }

        return Jsonl.ByteOffset != 0 ||
               Jsonl.LineNumber != 0 ||
               State == new KimiCodeParseState();
    }

    private static bool ValidStep(KimiCodePendingStep? step) =>
        step is null ||
        (ValidHash(step.StepIdHash) &&
         ValidHash(step.TurnIdHash) &&
         ValidUtc(step.StartedAtUtc) &&
         ValidOptionalString(step.RequestModel) &&
         ValidTools(step.Tools));

    private static bool ValidGoal(KimiCodeActiveGoal? goal) =>
        goal is null ||
        (ValidRequiredHash(goal.GoalIdHash) &&
         ValidRequiredHash(goal.PromptOriginTurnIdHash));

    private static bool ValidTaskOrigins(
        IReadOnlyList<KimiCodeTaskOrigin>? taskOrigins) =>
        taskOrigins is null ||
        (taskOrigins.Count <= MaxTaskOrigins &&
         taskOrigins.All(static task =>
             task is not null &&
             ValidRequiredHash(task.TaskIdHash) &&
             ValidHash(task.PromptOriginTurnIdHash)) &&
         taskOrigins.Select(static task => task.TaskIdHash)
             .Distinct(StringComparer.Ordinal)
             .Count() == taskOrigins.Count);

    private static bool ValidCall(KimiCodePendingCall? call) =>
        call is null ||
        (ValidHash(call.EventDedupKey) &&
         ValidHash(call.TurnIdHash) &&
         ValidUtc(call.CompletedAtUtc) &&
         call.InputOther >= 0 &&
         call.InputCacheRead >= 0 &&
         call.InputCacheCreation >= 0 &&
         call.Output >= 0 &&
         ValidTools(call.Tools));

    private static bool ValidUsage(KimiCodePendingUsage? usage)
    {
        if (usage is null)
        {
            return true;
        }

        try
        {
            long totalInput = checked(
                usage.InputOther +
                usage.InputCacheRead +
                usage.InputCacheCreation);
            return ValidOptionalString(usage.Model) &&
                   usage.InputOther >= 0 &&
                   usage.InputCacheRead >= 0 &&
                   usage.InputCacheCreation >= 0 &&
                   usage.Output >= 0 &&
                   usage.TotalInput == totalInput &&
                   usage.NormalizedTotal == checked(totalInput + usage.Output);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidTools(IReadOnlyList<KimiCodeToolReference>? tools) =>
        tools is not null &&
        tools.Count <= MaxToolsPerCall &&
        tools.All(static tool =>
            tool is not null &&
            tool.Ordinal >= 0 &&
            tool.Name is { Length: > 0 and <= 128 } &&
            !tool.Name.Any(char.IsControl)) &&
        tools.Select(static tool => tool.Ordinal).Distinct().Count() == tools.Count;

    private static bool ValidOptionalString(string? value) =>
        value is null ||
        (value.Length is > 0 and <= MaxStateStringCharacters &&
         !string.IsNullOrWhiteSpace(value) &&
         !value.Any(char.IsControl));

    private static bool ValidHash(string? value) =>
        value is null ||
        ValidRequiredHash(value);

    private static bool ValidRequiredHash(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidPreview(string? value) =>
        value is null ||
        (value.Length is > 0 and <= 240 && !value.Any(char.IsControl));

    private static bool ValidUtc(DateTimeOffset? value) =>
        !value.HasValue || value.Value.Offset == TimeSpan.Zero;

    private static bool ValidFingerprint(JsonlCursor jsonl) =>
        jsonl == JsonlCursor.Start ||
        (jsonl.SourceFingerprint.Length == 64 &&
         jsonl.SourceFingerprint.All(static character =>
             character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "kimi_code.invalid_cursor",
        "Kimi Code collection cursor was invalid and has been reset.");
}
