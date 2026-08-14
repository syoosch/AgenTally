using System.Globalization;
using System.Text.Json;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Collectors.GeminiCli;

internal static class GeminiCliParser
{
    private const long MaxSourceBytes = 64L * 1024 * 1024;
    private const int MaxLineCharacters = 4 * 1024 * 1024;
    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 512;

    internal static async Task<GeminiCliParseResult> ParseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length > MaxSourceBytes)
        {
            throw new InvalidDataException("The Gemini CLI transcript is missing or too large.");
        }

        DateTimeOffset fallback = new(info.LastWriteTimeUtc, TimeSpan.Zero);
        return string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)
            ? await ParseJsonlAsync(path, fallback, cancellationToken)
            : await ParseJsonAsync(path, fallback, cancellationToken);
    }

    private static async Task<GeminiCliParseResult> ParseJsonAsync(
        string path,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        byte[] payload = await BoundedFileReader.ReadAllBytesAsync(
            path,
            checked((int)MaxSourceBytes),
            cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            string sessionId = ReadIdentity(root, "sessionId", "session_id") ??
                SafeFileStem(path);
            string? projectHash = ReadIdentity(root, "projectHash", "project_hash");
            var records = new List<GeminiCliRecord>();
            var diagnostics = new List<CollectorDiagnostic>();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("messages", out JsonElement messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryParseDirect(
                            message,
                            sessionId,
                            projectHash,
                            fallback,
                            index,
                            modelHint: null,
                            out GeminiCliRecord? record))
                    {
                        records.Add(record!);
                    }
                    else if (message.ValueKind == JsonValueKind.Object &&
                             message.TryGetProperty("tokens", out _))
                    {
                        diagnostics.Add(InvalidRecord());
                    }
                    index++;
                }
            }
            else
            {
                records.AddRange(ParseHeadlessValue(
                    root,
                    sessionId,
                    projectHash,
                    fallback,
                    0,
                    modelHint: null,
                    diagnostics));
            }

            return Finalize(records, diagnostics);
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    private static async Task<GeminiCliParseResult> ParseJsonlAsync(
        string path,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        string sessionId = SafeFileStem(path);
        string? currentModel = null;
        var records = new List<GeminiCliRecord>();
        var diagnostics = new List<CollectorDiagnostic>();
        int lineNumber = 0;
        await foreach (BoundedTextLine boundedLine in
            BoundedUtf8LineReader.ReadLinesAsync(
                path,
                MaxLineCharacters,
                MaxSourceBytes,
                cancellationToken).ConfigureAwait(false))
        {
            lineNumber++;
            if (boundedLine.IsTooLong)
            {
                diagnostics.Add(InvalidRecord());
                continue;
            }

            string line = boundedLine.Text;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    line,
                    new JsonDocumentOptions { MaxDepth = 64 });
                JsonElement root = document.RootElement;
                string? eventType = ReadString(root, "type");
                sessionId = ReadIdentity(root, "session_id", "sessionId") ?? sessionId;
                currentModel = ReadModel(root, "model") ?? currentModel;
                if (string.Equals(eventType, "init", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                records.AddRange(ParseHeadlessValue(
                    root,
                    sessionId,
                    projectHash: null,
                    fallback,
                    lineNumber,
                    currentModel,
                    diagnostics));
            }
            catch (JsonException)
            {
                diagnostics.Add(InvalidRecord());
            }
        }

        return Finalize(records, diagnostics);
    }

    private static IEnumerable<GeminiCliRecord> ParseHeadlessValue(
        JsonElement root,
        string sessionId,
        string? projectHash,
        DateTimeOffset fallback,
        int sourceIndex,
        string? modelHint,
        List<CollectorDiagnostic> diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        string? eventType = ReadString(root, "type");
        if (string.Equals(eventType, "gemini", StringComparison.OrdinalIgnoreCase) ||
            root.TryGetProperty("tokens", out _))
        {
            if (TryParseDirect(
                    root,
                    sessionId,
                    projectHash,
                    fallback,
                    sourceIndex,
                    modelHint,
                    out GeminiCliRecord? direct))
            {
                yield return direct!;
            }
            else
            {
                diagnostics.Add(InvalidRecord());
            }
            yield break;
        }

        JsonElement stats;
        if (root.TryGetProperty("stats", out stats) ||
            root.TryGetProperty("result", out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("stats", out stats))
        {
            DateTimeOffset occurredAt = ReadTimestamp(root) ?? fallback;
            if (stats.ValueKind == JsonValueKind.Object &&
                stats.TryGetProperty("models", out JsonElement models) &&
                models.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty model in models.EnumerateObject())
                {
                    if (TryParseStatsTokens(model.Value, out TokenUsage? tokens))
                    {
                        yield return CreateRecord(
                            sourceIndex,
                            sessionId,
                            projectHash,
                            model.Name,
                            occurredAt,
                            tokens!,
                            stableId: null);
                    }
                    else
                    {
                        diagnostics.Add(InvalidRecord());
                    }
                }
                yield break;
            }

            if (modelHint is not null && TryParseStatsTokens(stats, out TokenUsage? aggregate))
            {
                yield return CreateRecord(
                    sourceIndex,
                    sessionId,
                    projectHash,
                    modelHint,
                    occurredAt,
                    aggregate!,
                    stableId: null);
            }
            else
            {
                diagnostics.Add(InvalidRecord());
            }
        }
    }

    private static bool TryParseDirect(
        JsonElement value,
        string sessionId,
        string? projectHash,
        DateTimeOffset fallback,
        int sourceIndex,
        string? modelHint,
        out GeminiCliRecord? record)
    {
        record = null;
        string? model = ReadModel(value, "model") ?? modelHint;
        if (model is null ||
            !value.TryGetProperty("tokens", out JsonElement tokenValue) ||
            !TryParseDirectTokens(tokenValue, out TokenUsage? tokens))
        {
            return false;
        }

        record = CreateRecord(
            sourceIndex,
            sessionId,
            projectHash,
            model,
            ReadTimestamp(value) ?? fallback,
            tokens!,
            ReadIdentity(value, "id"));
        return true;
    }

    private static bool TryParseDirectTokens(JsonElement value, out TokenUsage? usage)
    {
        usage = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool official = HasAnyProperty(
            value,
            "promptTokenCount",
            "candidatesTokenCount",
            "cachedContentTokenCount",
            "thoughtsTokenCount",
            "toolUsePromptTokenCount",
            "totalTokenCount");
        return official
            ? TryParseOfficialTokens(value, out usage)
            : TryParseLegacyTokens(value, out usage);
    }

    private static bool TryParseOfficialTokens(JsonElement value, out TokenUsage? usage)
    {
        usage = null;
        if (!TryReadCounter(value, out long input, "promptTokenCount") ||
            !TryReadOptionalCounter(value, out long output, out _, "candidatesTokenCount") ||
            !TryReadOptionalCounter(value, out long cached, out bool hasCached,
                "cachedContentTokenCount") ||
            !TryReadOptionalCounter(value, out long reasoning, out bool hasReasoning,
                "thoughtsTokenCount") ||
            !TryReadOptionalCounter(value, out long tool, out bool hasTool,
                "toolUsePromptTokenCount") ||
            !TryReadOptionalCounter(value, out long total, out bool hasTotal,
                "totalTokenCount") ||
            cached > input)
        {
            return false;
        }

        try
        {
            long normalized = checked(input + output + reasoning);
            if (hasTotal && total != normalized)
            {
                return false;
            }

            usage = new TokenUsage
            {
                InputReported = TokenMetric.Exact(input),
                UncachedInput = TokenMetric.Exact(input - cached),
                CacheRead = hasCached ? TokenMetric.Exact(cached) : TokenMetric.Unavailable,
                CacheWrite = TokenMetric.Unavailable,
                Output = TokenMetric.Exact(output),
                Reasoning = hasReasoning ? TokenMetric.Exact(reasoning) : TokenMetric.Unavailable,
                Tool = hasTool ? TokenMetric.Exact(tool) : TokenMetric.Unavailable,
                ReportedTotal = hasTotal ? TokenMetric.Exact(total) : TokenMetric.Unavailable,
                NormalizedTotal = TokenMetric.Exact(normalized),
                CacheIncludedInInput = hasCached
                    ? MetricInclusion.Included
                    : MetricInclusion.Unknown,
                ReasoningIncludedInOutput = hasReasoning
                    ? MetricInclusion.Separate
                    : MetricInclusion.Unknown
            };
            return normalized > 0 || tool > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseLegacyTokens(JsonElement value, out TokenUsage? usage)
    {
        usage = null;
        if (!TryReadOptionalCounter(value, out long input, out bool hasInput,
                "input", "prompt", "input_tokens", "prompt_tokens") ||
            !TryReadOptionalCounter(value, out long output, out bool hasOutput,
                "output", "candidates", "output_tokens", "completion_tokens",
                "candidates_tokens") ||
            !TryReadOptionalCounter(value, out long cached, out bool hasCached,
                "cached", "cached_tokens") ||
            !TryReadOptionalCounter(value, out long reasoning, out bool hasReasoning,
                "thoughts", "reasoning", "thoughts_tokens", "reasoning_tokens") ||
            !TryReadOptionalCounter(value, out long tool, out bool hasTool,
                "tool", "tool_tokens") ||
            !TryReadOptionalCounter(value, out long total, out bool hasTotal,
                "total", "total_tokens") ||
            !(hasInput || hasOutput || hasCached || hasReasoning || hasTool || hasTotal))
        {
            return false;
        }

        try
        {
            long inclusive = checked(input + output + reasoning + tool);
            long exclusive = checked(inclusive + cached);
            bool cacheIncluded;
            long uncached;
            if (hasTotal)
            {
                if (total == inclusive && (cached == 0 || cached <= input))
                {
                    cacheIncluded = cached > 0;
                    uncached = checked(input - cached + tool);
                }
                else if (total == exclusive)
                {
                    cacheIncluded = false;
                    uncached = checked(input + tool);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (cached != 0 || tool != 0)
                {
                    return false;
                }
                cacheIncluded = false;
                uncached = input;
                total = checked(input + output + reasoning);
            }

            usage = new TokenUsage
            {
                InputReported = hasInput || hasTool
                    ? TokenMetric.Exact(checked(input + tool))
                    : TokenMetric.Unavailable,
                UncachedInput = TokenMetric.Exact(uncached),
                CacheRead = hasCached ? TokenMetric.Exact(cached) : TokenMetric.Unavailable,
                CacheWrite = TokenMetric.Unavailable,
                Output = hasOutput ? TokenMetric.Exact(output) : TokenMetric.Unavailable,
                Reasoning = hasReasoning ? TokenMetric.Exact(reasoning) : TokenMetric.Unavailable,
                Tool = hasTool ? TokenMetric.Exact(tool) : TokenMetric.Unavailable,
                ReportedTotal = hasTotal ? TokenMetric.Exact(total) : TokenMetric.Unavailable,
                NormalizedTotal = TokenMetric.Exact(total),
                CacheIncludedInInput = hasCached
                    ? cacheIncluded ? MetricInclusion.Included : MetricInclusion.Separate
                    : MetricInclusion.Unknown,
                ReasoningIncludedInOutput = hasReasoning
                    ? MetricInclusion.Separate
                    : MetricInclusion.Unknown
            };
            return total > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseStatsTokens(JsonElement value, out TokenUsage? usage)
    {
        usage = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool wrapped = value.TryGetProperty("tokens", out JsonElement tokens) &&
            tokens.ValueKind == JsonValueKind.Object;
        if (!wrapped)
        {
            tokens = value;
        }

        bool hasPrompt = TryReadNamedCounter(tokens, out long input,
            "prompt", "input_tokens", "prompt_tokens");
        bool hasNetInput = TryReadNamedCounter(tokens, out long netInput, "input");
        if (!hasPrompt && hasNetInput)
        {
            input = netInput;
        }
        if (!TryReadOptionalCounter(tokens, out long output, out bool hasOutput,
                "candidates", "output", "output_tokens", "candidates_tokens") ||
            !TryReadOptionalCounter(tokens, out long cached, out bool hasCached,
                "cached", "cached_tokens") ||
            !TryReadOptionalCounter(tokens, out long reasoning, out bool hasReasoning,
                "thoughts", "thoughts_tokens", "reasoning", "reasoning_tokens") ||
            !(hasPrompt || hasNetInput || hasOutput || hasCached || hasReasoning))
        {
            return false;
        }

        bool cacheIncluded = hasPrompt || wrapped || !hasNetInput;
        if (cacheIncluded && cached > input)
        {
            return false;
        }

        try
        {
            long uncached = cacheIncluded ? input - cached : input;
            long normalized = checked(uncached + cached + output + reasoning);
            usage = new TokenUsage
            {
                InputReported = hasPrompt || hasNetInput
                    ? TokenMetric.Exact(input)
                    : TokenMetric.Unavailable,
                UncachedInput = TokenMetric.Exact(uncached),
                CacheRead = hasCached ? TokenMetric.Exact(cached) : TokenMetric.Unavailable,
                CacheWrite = TokenMetric.Unavailable,
                Output = hasOutput ? TokenMetric.Exact(output) : TokenMetric.Unavailable,
                Reasoning = hasReasoning ? TokenMetric.Exact(reasoning) : TokenMetric.Unavailable,
                Tool = TokenMetric.Unavailable,
                ReportedTotal = TokenMetric.Unavailable,
                NormalizedTotal = TokenMetric.Exact(normalized),
                CacheIncludedInInput = hasCached
                    ? cacheIncluded ? MetricInclusion.Included : MetricInclusion.Separate
                    : MetricInclusion.Unknown,
                ReasoningIncludedInOutput = hasReasoning
                    ? MetricInclusion.Separate
                    : MetricInclusion.Unknown
            };
            return normalized > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static GeminiCliRecord CreateRecord(
        int sourceIndex,
        string sessionId,
        string? projectHash,
        string model,
        DateTimeOffset occurredAtUtc,
        TokenUsage tokens,
        string? stableId)
    {
        string tokenIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}\0{model}\0{occurredAtUtc.ToUnixTimeMilliseconds()}\0{tokens.InputReported.Value}\0{tokens.UncachedInput.Value}\0{tokens.CacheRead.Value}\0{tokens.Output.Value}\0{tokens.Reasoning.Value}\0{tokens.Tool.Value}\0{tokens.NormalizedTotal.Value}");
        string stable = stableId ?? GeminiCliSourceIdentity.HashIdentity(
            "gemini-cli-token-record",
            tokenIdentity);
        string orderHash = GeminiCliSourceIdentity.HashIdentity("gemini-cli-order", stable);
        return new GeminiCliRecord(
            $"{sourceIndex:D10}-{orderHash[..16]}",
            stable,
            sessionId,
            projectHash,
            model,
            occurredAtUtc.ToUniversalTime(),
            tokens);
    }

    private static GeminiCliParseResult Finalize(
        List<GeminiCliRecord> records,
        List<CollectorDiagnostic> diagnostics)
    {
        GeminiCliRecord[] deduplicated = records
            .GroupBy(static record => $"{record.SessionId}\0{record.StableId}", StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static record => record.SourceKey, StringComparer.Ordinal)
            .ToArray();
        return new GeminiCliParseResult(deduplicated, diagnostics);
    }

    private static bool HasAnyProperty(JsonElement value, params string[] names) =>
        names.Any(name => value.TryGetProperty(name, out _));

    private static bool TryReadCounter(JsonElement value, out long result, params string[] names) =>
        TryReadNamedCounter(value, out result, names) && result >= 0;

    private static bool TryReadOptionalCounter(
        JsonElement value,
        out long result,
        out bool present,
        params string[] names)
    {
        present = names.Any(name => value.TryGetProperty(name, out _));
        if (!present)
        {
            result = 0;
            return true;
        }
        return TryReadCounter(value, out result, names);
    }

    private static bool TryReadNamedCounter(
        JsonElement value,
        out long result,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (value.TryGetProperty(name, out JsonElement property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out result) &&
                result >= 0)
            {
                return true;
            }
        }
        result = 0;
        return false;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement value)
    {
        foreach (string name in new[] { "timestamp", "created_at", "createdAt" })
        {
            if (!value.TryGetProperty(name, out JsonElement property))
            {
                continue;
            }
            if (property.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    property.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestamp))
            {
                return timestamp.ToUniversalTime();
            }
            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out long milliseconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static string? ReadIdentity(JsonElement value, params string[] names) =>
        ReadBoundedString(value, MaxIdentityCharacters, names);

    private static string? ReadModel(JsonElement value, params string[] names) =>
        ReadBoundedString(value, MaxModelCharacters, names);

    private static string? ReadString(JsonElement value, params string[] names) =>
        ReadBoundedString(value, MaxIdentityCharacters, names);

    private static string? ReadBoundedString(
        JsonElement value,
        int maxCharacters,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string name in names)
        {
            if (value.TryGetProperty(name, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String &&
                property.GetString() is string text &&
                text.Length is > 0 && text.Length <= maxCharacters &&
                !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl))
            {
                return text.Trim();
            }
        }
        return null;
    }

    private static string SafeFileStem(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length is > 0 and <= MaxIdentityCharacters && !stem.Any(char.IsControl)
            ? stem
            : GeminiCliSourceIdentity.HashIdentity("gemini-cli-file-session", path);
    }

    private static CollectorDiagnostic InvalidRecord() => new(
        "gemini-cli.unsupported_token_record",
        "A Gemini CLI Token record could not prove safe counter semantics and was skipped.");
}

internal sealed record GeminiCliParseResult(
    IReadOnlyList<GeminiCliRecord> Records,
    IReadOnlyList<CollectorDiagnostic> Diagnostics);

internal sealed record GeminiCliRecord(
    string SourceKey,
    string StableId,
    string SessionId,
    string? ProjectHash,
    string Model,
    DateTimeOffset OccurredAtUtc,
    TokenUsage Tokens);
