using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.Mock;

internal sealed class MockCollector : IParserVersionedCollector
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private const string CurrentParserVersion = "mock-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly IncrementalJsonlReader _reader;

    public MockCollector(
        string path,
        TimeProvider? timeProvider = null,
        IncrementalJsonlReader? reader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reader = reader ?? new IncrementalJsonlReader();

        string rootPath = Path.GetDirectoryName(_path) ?? _path;
        Instance = new SourceInstanceDescriptor(
            $"mock:windows:{StablePathHash(rootPath, 16)}",
            AgentId,
            SourceKind.Jsonl,
            "模拟数据",
            rootPath);
        Entity = new SourceEntityDescriptor(
            Instance.SourceInstanceId,
            $"mock:jsonl:{StablePathHash(_path, 24)}",
            _path);
    }

    public string AgentId => "mock-agent";

    public string ParserVersion => CurrentParserVersion;

    public SourceInstanceDescriptor Instance { get; }

    public SourceEntityDescriptor Entity { get; }

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_path))
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [],
                [],
                [new CollectorDiagnostic(
                    "mock.source_missing",
                    "指定的模拟日志不存在。",
                    Entity.SourceEntityId)]));
        }

        return ValueTask.FromResult(new SourceProbeResult(
            [Instance],
            [Entity],
            []));
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        JsonlCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null && !CursorBelongsToSource(request.Cursor))
        {
            cursor = JsonlCursor.Start;
            cursorDiagnostic = CursorSourceMismatchDiagnostic();
        }
        else
        {
            cursor = JsonlCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                out cursorDiagnostic);
        }

        int batchCount = 0;
        while (true)
        {
            JsonlReadBatch readBatch = await _reader.ReadBatchAsync(
                _path,
                cursor,
                MaxBatchLines,
                cancellationToken);

            // 在首个完整行出现前没有稳定指纹，也没有可安全提交的游标。
            if (string.IsNullOrWhiteSpace(readBatch.NextCursor.SourceFingerprint))
            {
                if (readBatch.Diagnostic?.Code is "jsonl.first_line_too_long")
                {
                    throw new InvalidDataException(readBatch.Diagnostic.Message);
                }

                yield break;
            }

            var events = new List<UsageEvent>(readBatch.Lines.Count);
            var diagnostics = new List<CollectorDiagnostic>();

            if (cursorDiagnostic is not null)
            {
                diagnostics.Add(cursorDiagnostic with
                {
                    SourceEntityId = Entity.SourceEntityId
                });
                cursorDiagnostic = null;
            }

            if (readBatch.Diagnostic is not null)
            {
                diagnostics.Add(readBatch.Diagnostic with
                {
                    SourceEntityId = Entity.SourceEntityId
                });
            }

            foreach (JsonlLine line in readBatch.Lines)
            {
                ParseLine(
                    line,
                    readBatch.NextCursor.SourceFingerprint,
                    events,
                    diagnostics);
            }

            batchCount++;
            bool collectionLimitReached =
                batchCount >= MaxCollectionBatches && !readBatch.EndOfFile;
            if (collectionLimitReached)
            {
                diagnostics.Add(CollectionLimitDiagnostic());
            }

            yield return new CollectedBatch(
                Instance,
                Entity,
                events,
                readBatch.NextCursor.Serialize(),
                readBatch.NextCursor.SourceFingerprint,
                CurrentParserVersion,
                diagnostics);

            if (readBatch.EndOfFile || collectionLimitReached)
            {
                yield break;
            }

            cursor = readBatch.NextCursor;
        }
    }

    private void ValidateRequest(CollectionRequest request)
    {
        if (!string.Equals(
                request.Instance.SourceInstanceId,
                Instance.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Entity.SourceEntityId,
                Entity.SourceEntityId,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(request.Entity.SourcePath),
                _path,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "采集请求不属于此 Mock fixture。",
                nameof(request));
        }
    }

    private bool CursorBelongsToSource(AgenTally.Storage.Writing.StoredCursor cursor)
    {
        if (!string.Equals(
                cursor.SourceInstanceId,
                Instance.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                cursor.SourceEntityId,
                Entity.SourceEntityId,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizePath(cursor.SourcePath),
                NormalizePath(_path),
                StringComparison.Ordinal);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void ParseLine(
        JsonlLine line,
        string sourceFingerprint,
        ICollection<UsageEvent> events,
        ICollection<CollectorDiagnostic> diagnostics)
    {
        try
        {
            MockLogEntry entry = JsonSerializer.Deserialize<MockLogEntry>(line.Utf8, JsonOptions)
                ?? throw new JsonException("日志行为空。");
            events.Add(Map(entry, line.LineNumber, sourceFingerprint));
        }
        catch (Exception exception)
            when (exception is JsonException or FormatException or ArgumentException or OverflowException)
        {
            diagnostics.Add(new CollectorDiagnostic(
                "mock.invalid_json",
                "模拟日志行不是有效 JSON。",
                Entity.SourceEntityId,
                line.ByteOffset));
        }
    }

    private UsageEvent Map(
        MockLogEntry entry,
        long lineNumber,
        string sourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Agent);

        if (!string.Equals(entry.Agent, AgentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("agent 与采集器不匹配。", nameof(entry));
        }

        if (!DateTimeOffset.TryParse(
            entry.Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset occurredAt))
        {
            throw new FormatException("timestamp 不是有效的 ISO 8601 时间。");
        }

        string? model = string.IsNullOrWhiteSpace(entry.Model) ? null : entry.Model;
        var modelIdentity = new ModelIdentity
        {
            RawModel = model,
            NormalizedModel = model,
            ProviderId = "mock",
            ResolutionOrigin = model is null
                ? ModelResolutionOrigin.Unknown
                : ModelResolutionOrigin.LogConfirmed
        };
        var tokens = new TokenUsage
        {
            InputReported = Metric(entry.RawInput),
            UncachedInput = Metric(entry.FreshInput),
            CacheRead = Metric(entry.CacheRead),
            CacheWrite = Metric(entry.CacheWrite),
            Output = Metric(entry.Output),
            Reasoning = Metric(entry.Reasoning),
            Tool = Metric(entry.Tool),
            ReportedTotal = Metric(entry.TotalProcessed),
            NormalizedTotal = DerivedMetric(entry.TotalProcessed),
            CacheIncludedInInput = MetricInclusion.Unknown,
            ReasoningIncludedInOutput = MetricInclusion.Unknown
        };

        return new UsageEvent(
            AgentId,
            Instance.SourceInstanceId,
            Entity.SourceEntityId,
            entry.RecordId,
            $"mock:{Entity.SourceEntityId}:{entry.RecordId}",
            SourceKind.Jsonl,
            occurredAt.ToUniversalTime(),
            _timeProvider.GetUtcNow(),
            modelIdentity,
            tokens,
            CompletionState.Finalized,
            DataQuality.Exact,
            CurrentParserVersion,
            sourceFingerprint,
            lineNumber);
    }

    private static TokenMetric Metric(long? value) =>
        value.HasValue ? TokenMetric.Exact(value.Value) : TokenMetric.Unavailable;

    private static TokenMetric DerivedMetric(long? value) =>
        value.HasValue
            ? new TokenMetric(value.Value, MetricOrigin.Derived)
            : TokenMetric.Unavailable;

    private static string StablePathHash(string path, int length)
    {
        string normalized = NormalizePath(path);
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return hash[..length].ToLowerInvariant();
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .ToUpperInvariant();

    private CollectorDiagnostic CursorSourceMismatchDiagnostic() => new(
        "collector.cursor_source_mismatch",
        "已忽略不属于当前来源的读取游标，并从头重新读取。",
        Entity.SourceEntityId);

    private CollectorDiagnostic CollectionLimitDiagnostic() => new(
        "collector.batch_limit_reached",
        "单次采集已达到 25 批（最多 5000 行）上限，将从当前游标继续。",
        Entity.SourceEntityId);

    private sealed record MockLogEntry(
        string? RecordId,
        string? Timestamp,
        string? Agent,
        string? Model,
        long? RawInput,
        long? FreshInput,
        long? CacheRead,
        long? CacheWrite,
        long? Output,
        long? Reasoning,
        long? Tool,
        long? TotalProcessed);
}
