using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.DeepSeekHarness;

// Independent metadata projection of the official v4 contract at
// deepseek-ai/deepseek-harness@687f282e8625c6b8d11ff58211a90524be2e4317.
// Bodies, replayState, tool arguments/results and request payloads never enter state.
internal sealed class DeepSeekHarnessParser
{
    internal const string Version = "deepseek-harness-v4-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly JsonSerializerOptions CursorOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    internal static JsonDocument Document(ReadOnlyMemory<byte> bytes)
    {
        StrictUtf8.GetCharCount(bytes.Span);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }

    internal static DeepSeekHarnessHeader ReadHeader(ReadOnlyMemory<byte> bytes, string path)
    {
        using JsonDocument document = Document(bytes);
        JsonElement root = document.RootElement;
        if (String(root, "type", 64) != "session" || !root.TryGetProperty("version", out var version) ||
            !version.TryGetInt32(out int format) || format != 4 ||
            !DeepSeekHarnessSourceIdentity.TryReadGeneration(Path.GetFileName(path), out int generation) ||
            generation != format)
            throw new DeepSeekHarnessUnsupportedException();
        string id = String(root, "id", 1024) ?? throw Invalid();
        if (!string.Equals(DeepSeekHarnessSourceIdentity.EncodeSegment(id),
                Path.GetFileName(Path.GetDirectoryName(path)), StringComparison.Ordinal)) throw Invalid();
        if (!root.TryGetProperty("isSeeded", out var seeded) ||
            seeded.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        string? parent = String(root, "parentSession", 1024);
        if (parent == id || seeded.GetBoolean() && parent is null) throw Invalid();
        string? cwd = String(root, "cwd", CodexProjectIdentity.MaxProjectPathCharacters);
        if (cwd is not null && !CodexProjectIdentity.TryCreate(cwd, out _)) throw Invalid();
        return new(id, Timestamp(root, "createdAt"), cwd, parent, seeded.GetBoolean(),
            String(root, "origin", 64) == "subagent",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.Span)).ToLowerInvariant());
    }

    internal static DeepSeekHarnessState Start(DeepSeekHarnessHeader header, long inheritedCut, long revision)
        => new()
        {
            SessionId = header.Id, CreatedAt = header.CreatedAt, Cwd = header.Cwd,
            ParentSessionId = header.ParentId, IsChild = header.IsChild, Seeded = header.Seeded,
            InheritedCut = inheritedCut, SourceFingerprint = header.Fingerprint,
            NextRevision = Math.Max(1, revision)
        };

    internal void Parse(ReadOnlyMemory<byte> bytes, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection)
    {
        using JsonDocument document = Document(bytes);
        JsonElement root = document.RootElement;
        string type = String(root, "type", 128) ?? throw Invalid();
        if (type == "session")
        {
            if (state.LastSeq != -1) throw Invalid();
            return;
        }
        long seq = Counter(root, "seq");
        if (seq != state.LastSeq + 1) throw Invalid();
        DateTimeOffset timestamp = Timestamp(root, "time");
        state.LastSeq = seq;
        state.LastTime = timestamp;
        root.TryGetProperty("data", out var data);
        // Inherited records establish no calls, tools, Prompts or child dispatches.
        if (seq < state.InheritedCut) return;
        bool structural = type is "turn/start" or "turn/end" or "step/start" or "step/end" or
            "user/message" or "assistant/message" or "assistant/attempt" or "session/title" or
            "request/header" or "tool/call" or "tool/result" or "subagent/catalog" or "goal/change";
        if (structural && data.ValueKind != JsonValueKind.Object) throw Invalid();

        switch (type)
        {
            case "turn/start":
                state.ActiveTurn = Counter(data, "turn");
                state.TurnStarted = timestamp;
                state.ActiveStep = null;
                state.TurnHash = null;
                state.OriginTurnHash = null;
                state.Preview = null;
                state.UserMessages = 0;
                state.FirstUserHash = null;
                state.ToolCalls.Clear();
                break;
            case "step/start":
                if (state.ActiveTurn != Counter(data, "turn")) throw Invalid();
                state.ActiveStep = Counter(data, "step");
                state.ToolCalls.Clear();
                break;
            case "step/end":
                state.ActiveStep = null;
                state.ToolCalls.Clear();
                break;
            case "turn/end":
                if (state.ActiveTurn == Counter(data, "turn"))
                {
                    AddTurn(state, projection, timestamp);
                    state.ActiveTurn = null;
                    state.ActiveStep = null;
                    state.ToolCalls.Clear();
                }
                break;
            case "user/message":
                ParseUser(root, data, state, projection);
                break;
            case "assistant/message":
                ParseAssistant(data, state, projection, seq, timestamp);
                break;
            case "assistant/attempt":
                ParseAttempt(data, state, projection, seq, timestamp);
                break;
            case "session/title":
                state.Title = Preview(String(data, "title", DeepSeekHarnessLogReader.MaxLineBytes, allowWhitespace: true));
                state.TitleTime = timestamp;
                break;
            case "request/header":
                if (data.TryGetProperty("header", out var header) &&
                    header.TryGetProperty("config", out var config))
                {
                    state.Model = String(config, "model", 512);
                    state.Provider = String(config, "provider", 512);
                }
                break;
            case "tool/call":
                if (state.ActiveTurn != Counter(data, "turn") ||
                    state.ActiveStep != Counter(data, "step")) throw Invalid();
                if (state.ToolCalls.Count >= 256) throw Invalid();
                state.ToolCalls[Hash("tool-call", String(data, "callId", 1024) ?? throw Invalid())] =
                    String(data, "name", 128) ?? throw Invalid();
                break;
            case "tool/result":
                if (data.TryGetProperty("message", out var resultMessage) &&
                    resultMessage.TryGetProperty("source", out var resultSource) &&
                    String(resultSource, "callId", 1024) is { } callId)
                    state.ToolCalls.Remove(Hash("tool-call", callId));
                break;
            case "subagent/catalog":
                ParseDispatch(data, state, projection, timestamp);
                break;
            case "goal/change":
                ParseGoal(data, state);
                break;
            // Unrelated events are not a reason to reject reliable accounting.
        }
    }

    private static void ParseUser(JsonElement root, JsonElement data, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection)
    {
        if (state.ActiveTurn is null || !data.TryGetProperty("source", out var source)) return;
        if (root.TryGetProperty("surfaceOp", out var operation) &&
            operation.ValueKind == JsonValueKind.Object && String(operation, "kind", 64) == "replace") return;
        string? kind = String(source, "kind", 128);
        string turn = Hash("turn", state.SessionId + "\0" + state.ActiveTurn.Value);
        if (kind == "user")
        {
            string id = Hash("user-message", String(data, "id", 1024) ?? throw Invalid());
            if (state.FirstUserHash == id) return;
            state.TurnHash = turn;
            state.OriginTurnHash = null;
            if (!state.IsChild)
            {
                if (state.UserMessages == 0)
                {
                    state.FirstUserHash = id;
                    state.Preview = data.TryGetProperty("content", out var content) ? Prompt(content) : null;
                }
                state.UserMessages = checked(state.UserMessages + 1);
            }
            // A subagent's initial task is source-marked user input to its model,
            // but the subagent header proves it is delegated input, not a human Prompt.
        }
        else if (kind == "goal" && state.GoalIdHash is { } goal &&
                 String(source, "goalId", 1024) is { } goalId && Hash("goal", goalId) == goal &&
                 state.GoalOriginHash is not null && Counter(source, "revision") <= state.GoalRevision)
        {
            state.TurnHash = turn;
            state.OriginTurnHash = state.GoalOriginHash;
        }
        AddTurn(state, projection, null);
    }

    private static void ParseAssistant(JsonElement data, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection, long seq, DateTimeOffset timestamp)
    {
        if (!data.TryGetProperty("usage", out var usage)) return;
        if (!data.TryGetProperty("message", out var message) ||
            String(message, "role", 32) != "assistant" ||
            !message.TryGetProperty("source", out var source) || String(source, "kind", 64) != "model")
            throw Invalid();
        string id = String(message, "id", 1024) ?? throw Invalid();
        string model = String(source, "model", 512) ?? throw Invalid();
        string? provider = String(source, "provider", 512);
        string dedup = Hash("call", state.SessionId + "\0" + id);
        var value = Event(data, usage, state, projection, timestamp, model, provider, dedup);
        projection.Events.Add(value);
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            int ordinal = 0;
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (String(block, "type", 64) != "tool-call") continue;
                string name = String(block, "name", 128) ?? throw Invalid();
                if (ordinal >= 1024) throw Invalid();
                projection.Tools.Add(new(projection.Instance.AgentId,
                    projection.Instance.SourceInstanceId, projection.Entity.SourceEntityId,
                    dedup, ordinal++, name, Version));
            }
        }
        state.Model = model;
        state.Provider = provider;
    }

    private static void ParseAttempt(JsonElement data, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection, long seq, DateTimeOffset timestamp)
    {
        if (!data.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Array) throw Invalid();
        JsonElement? usage = null;
        foreach (JsonElement record in stream.EnumerateArray())
        {
            if (String(record, "type", 64) == "chunk" && record.TryGetProperty("chunk", out var chunk) &&
                String(chunk, "type", 64) == "usage" && chunk.TryGetProperty("usage", out var reported)) usage = reported;
        }
        if (usage is null) return; // No reported usage is not zero usage.
        projection.Events.Add(Event(data, usage.Value, state, projection, timestamp,
            state.Model ?? throw Invalid(), state.Provider,
            Hash("attempt", state.SessionId + "\0" + seq)));
    }

    private static UsageEvent Event(JsonElement data, JsonElement usage, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection, DateTimeOffset timestamp, string model, string? provider, string dedup)
    {
        long turn = Counter(data, "turn");
        _ = Counter(data, "step");
        TokenUsage tokens = Tokens(usage);
        var value = new UsageEvent(projection.Instance.AgentId, projection.Instance.SourceInstanceId,
            projection.Entity.SourceEntityId, "deepseek-harness:" + dedup, dedup, SourceKind.Jsonl,
            timestamp, projection.ImportedAt, new ModelIdentity
            {
                RawModel = model, NormalizedModel = model, ProviderId = provider,
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            }, tokens, CompletionState.Completed, DataQuality.Exact, Version,
            state.SourceFingerprint, checked(state.NextRevision++))
        {
            SessionId = state.SessionId,
            ParentSessionId = state.IsChild ? state.ParentSessionId : null,
            TurnIdHash = state.ActiveTurn == turn ? state.TurnHash : null
        };
        if (CodexProjectIdentity.TryCreate(state.Cwd, out var project))
            value = value with { ProjectId = project.ProjectId, ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash };
        AddTurn(state, projection, null);
        return value;
    }

    internal static TokenUsage Tokens(JsonElement usage)
    {
        long input = Counter(usage, "inputTokens");
        long output = Counter(usage, "outputTokens");
        long? read = OptionalCounter(usage, "cacheReadTokens");
        long? write = OptionalCounter(usage, "cacheWriteTokens");
        long? total = OptionalCounter(usage, "totalTokens");
        long? reasoning = OptionalCounter(usage, "reasoningTokens");
        long known = checked(input + output + (read ?? 0) + (write ?? 0));
        MetricInclusion reasoningInclusion = MetricInclusion.Unknown;
        if (reasoning is > 0)
        {
            if (!read.HasValue || !write.HasValue) throw Invalid();
            if (total == known && reasoning <= output)
            {
                output -= reasoning.Value;
                reasoningInclusion = MetricInclusion.Included;
            }
            else if (total == checked(known + reasoning.Value))
            {
                known = total.Value;
                reasoningInclusion = MetricInclusion.Separate;
            }
            else throw Invalid();
        }
        if (total < known || read.HasValue && write.HasValue && total.HasValue && total != known)
            throw Invalid();
        // A complete exact total can prove that an omitted cache category is zero.
        bool missingIsZero = total == known;
        TokenMetric readMetric = Metric(read, missingIsZero);
        TokenMetric writeMetric = Metric(write, missingIsZero);
        long? normalized = total ?? (read.HasValue && write.HasValue ? known : null);
        return new TokenUsage
        {
            InputReported = total is { } exactTotal
                ? new(exactTotal - (output + (reasoning ?? 0)), MetricOrigin.Derived)
                : read.HasValue && write.HasValue ? new(checked(input + read.Value + write.Value), MetricOrigin.Derived)
                : TokenMetric.Unavailable,
            UncachedInput = TokenMetric.Exact(input), CacheRead = readMetric, CacheWrite = writeMetric,
            Output = TokenMetric.Exact(output), Reasoning = Metric(reasoning, false),
            ReportedTotal = Metric(total, false),
            NormalizedTotal = normalized.HasValue ? new(normalized, total.HasValue ? MetricOrigin.Exact : MetricOrigin.Derived)
                : TokenMetric.Unavailable,
            CacheIncludedInInput = MetricInclusion.Included, ReasoningIncludedInOutput = reasoningInclusion
        };
    }

    private static TokenMetric Metric(long? value, bool zero) => value.HasValue ? TokenMetric.Exact(value.Value)
        : zero ? new(0, MetricOrigin.Derived) : TokenMetric.Unavailable;

    private static void ParseGoal(JsonElement data, DeepSeekHarnessState state)
    {
        string? operation = String(data, "operation", 64);
        if (operation == "clear") { state.GoalIdHash = null; state.GoalOriginHash = null; return; }
        if (!data.TryGetProperty("goal", out var goal)) return;
        string id = Hash("goal", String(goal, "id", 1024) ?? throw Invalid());
        if (operation == "create")
        {
            state.GoalIdHash = id;
            state.GoalOriginHash = state.ToolCalls.Values.Contains("create_goal", StringComparer.Ordinal)
                ? state.OriginTurnHash ?? state.TurnHash : null;
        }
        if (state.GoalIdHash == id) state.GoalRevision = Counter(goal, "revision");
    }

    private static void ParseDispatch(JsonElement data, DeepSeekHarnessState state,
        DeepSeekHarnessProjection projection, DateTimeOffset timestamp)
    {
        if (state.TurnHash is null || state.ActiveStep is null ||
            !state.ToolCalls.Values.Contains("subagent", StringComparer.Ordinal)) return;
        long version = Counter(data, "version");
        if (version is not (0 or 1) || String(data, "mode", 64) is not ("continuable" or "one-shot")) return;
        string child = String(data, "childId", 1024) ?? throw Invalid();
        projection.Dispatches.Add(new(projection.Instance.AgentId, projection.Instance.SourceInstanceId,
            projection.Entity.SourceEntityId, state.SessionId, state.OriginTurnHash ?? state.TurnHash,
            Hash("dispatch", state.SessionId + "\0" + child), Hash("session-agent", child),
            TurnDispatchKind.Spawn, DispatchTargetKind.AgentPath, timestamp, Version));
    }

    internal static UsageSessionMetadata Session(DeepSeekHarnessState state, DeepSeekHarnessProjection projection)
    {
        bool child = state.IsChild && state.ParentSessionId is not null;
        var session = new UsageSessionMetadata(projection.Instance.AgentId, projection.Instance.SourceInstanceId,
            projection.Entity.SourceEntityId, state.SessionId, child ? SessionKind.Side : SessionKind.Primary,
            child ? state.ParentSessionId : null, state.Seeded ? state.ParentSessionId : null,
            child ? SessionRelationOrigin.SourceAgentParent : SessionRelationOrigin.None,
            child ? SessionRelationState.Confirmed : SessionRelationState.None, ReplayState.Active,
            CompatibilityLevel.PartiallyCompatible, state.LastTime ?? state.CreatedAt, Version)
        {
            SessionRole = child ? SessionRole.Subagent : SessionRole.Main,
            AgentPathHash = Hash("session-agent", state.SessionId),
            AgentLeafHash = Hash("session-agent", state.SessionId),
            SessionName = state.Title, SessionNameUpdatedAtUtc = state.TitleTime
        };
        if (CodexProjectIdentity.TryCreate(state.Cwd, out var project))
            session = session with { ProjectId = project.ProjectId, ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash };
        return session;
    }

    private static void AddTurn(DeepSeekHarnessState state, DeepSeekHarnessProjection projection,
        DateTimeOffset? completed)
    {
        if (state.TurnHash is null || state.TurnStarted is null) return;
        projection.Turns.Add(new(projection.Instance.AgentId, projection.Instance.SourceInstanceId,
            projection.Entity.SourceEntityId, state.SessionId, state.TurnHash, state.TurnStarted.Value,
            completed, state.Preview, state.UserMessages, Version, state.OriginTurnHash));
    }

    private static string? Prompt(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return null;
        var text = new StringBuilder(256);
        foreach (JsonElement block in content.EnumerateArray())
        {
            string? type = String(block, "type", 64);
            string? part = type switch
            {
                "text" => String(block, "text", DeepSeekHarnessLogReader.MaxLineBytes, allowWhitespace: true),
                "image" => "[图片]", "audio" => "[音频]", _ => null
            };
            if (part is not null)
            {
                text.Append(' ').Append(Preview(part));
                string? preview = Preview(text.ToString());
                if (preview?.EnumerateRunes().Count() >= 120) return preview;
            }
        }
        return Preview(text.ToString());
    }

    internal static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new StringBuilder(240);
        int count = 0;
        bool space = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            { space = result.Length > 0; continue; }
            if (space && count < 120) { result.Append(' '); count++; }
            space = false;
            if (count >= 120) break;
            result.Append(rune.ToString()); count++;
        }
        return result.Length == 0 ? null : result.ToString();
    }

    internal static string? String(JsonElement value, string name, int maximum, bool allowWhitespace = false)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw Invalid();
        string result = property.GetString()!;
        if (result.Length > maximum || !allowWhitespace && (string.IsNullOrWhiteSpace(result) || result.Any(char.IsControl)))
            throw Invalid();
        return result;
    }

    internal static long Counter(JsonElement value, string name) => OptionalCounter(value, name) ?? throw Invalid();
    private static long? OptionalCounter(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out long count) ||
            count < 0 || count > 9007199254740991L) throw Invalid();
        return count;
    }
    private static DateTimeOffset Timestamp(JsonElement value, string name) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Counter(value, name));
    private static string Hash(string kind, string value) => DeepSeekHarnessSourceIdentity.Hash(kind, value);
    internal static InvalidDataException Invalid() => new("The Harness record has invalid accounting or structural metadata.");
}

internal sealed class DeepSeekHarnessUnsupportedException : IOException { }

internal sealed record DeepSeekHarnessHeader(string Id, DateTimeOffset CreatedAt, string? Cwd,
    string? ParentId, bool Seeded, bool IsChild, string Fingerprint);

internal sealed class DeepSeekHarnessState
{
    public string SessionId { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? Cwd { get; set; }
    public string? ParentSessionId { get; set; }
    public bool IsChild { get; set; }
    public bool Seeded { get; set; }
    public long InheritedCut { get; set; }
    public long LastSeq { get; set; } = -1;
    public long NextRevision { get; set; } = 1;
    public DateTimeOffset? LastTime { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? TitleTime { get; set; }
    public long? ActiveTurn { get; set; }
    public long? ActiveStep { get; set; }
    public DateTimeOffset? TurnStarted { get; set; }
    public string? TurnHash { get; set; }
    public string? OriginTurnHash { get; set; }
    public string? Preview { get; set; }
    public string? FirstUserHash { get; set; }
    public int UserMessages { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public Dictionary<string, string> ToolCalls { get; set; } = [];
    public string? GoalIdHash { get; set; }
    public string? GoalOriginHash { get; set; }
    public long GoalRevision { get; set; }
}

internal sealed record DeepSeekHarnessProjection(SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity, DateTimeOffset ImportedAt)
{
    internal List<UsageEvent> Events { get; } = [];
    internal List<UsageTurnMetadata> Turns { get; } = [];
    internal List<UsageEventToolMetadata> Tools { get; } = [];
    internal List<UsageTurnDispatch> Dispatches { get; } = [];
}
