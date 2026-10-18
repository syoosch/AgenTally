using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.DeepSeekHarness;

public sealed class DeepSeekHarnessCollector : IIncrementalFileCollector
{
    private const int MaxBatchRecords = 200;
    private const int MaxPassBatches = 25;
    private const int MaxCursorCharacters = 262144;
    private readonly string _home;
    private readonly TimeProvider _time;
    private readonly DeepSeekHarnessSourceResolver _resolver = new();
    private readonly DeepSeekHarnessParser _parser = new();

    public DeepSeekHarnessCollector(string home, TimeProvider? timeProvider = null)
    {
        _home = DeepSeekHarnessSourceIdentity.NormalizePath(home);
        _time = timeProvider ?? TimeProvider.System;
    }

    public string AgentId => DeepSeekHarnessSourceIdentity.AgentId;
    public string ParserVersion => DeepSeekHarnessParser.Version;
    public CompatibilityLevel MaintenanceCompatibilityLevel => CompatibilityLevel.PartiallyCompatible;
    public string WatchFilter => "session*.jsonl*";

    public bool IsRelevantChangePath(string normalizedChangePath) =>
        DeepSeekHarnessSourceIdentity.TryReadGeneration(Path.GetFileName(normalizedChangePath), out _);

    public ValueTask<SourceProbeResult> ProbeAsync(CollectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_home, cancellationToken);
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInstance(request.Instance);
        string path = DeepSeekHarnessSourceIdentity.NormalizePath(request.Entity.SourcePath);
        if (request.Entity.SourceInstanceId != request.Instance.SourceInstanceId ||
            request.Entity.SourceEntityId != DeepSeekHarnessSourceIdentity.EntityId(path))
            throw new ArgumentException("Invalid Harness entity identity.", nameof(request));
        if (request.Cursor is not null && request.Cursor.ParserVersion != ParserVersion)
            throw new AgentParserRebuildRequiredException(AgentId, request.Cursor.ParserVersion, ParserVersion);
        if (request.Cursor is not null && (request.Cursor.SourceInstanceId != request.Instance.SourceInstanceId ||
            request.Cursor.SourceEntityId != request.Entity.SourceEntityId))
            throw new ArgumentException("Invalid Harness cursor identity.", nameof(request));

        DeepSeekHarnessCursor? cursor = null;
        DeepSeekHarnessLogReader? reader = null;
        CollectorDiagnostic? failure = null;
        var diagnostics = new List<CollectorDiagnostic>();
        string safeCursor = request.Cursor?.CursorJson ?? Serialize(new(0, 0, new()));
        try
        {
            DeepSeekHarnessSourceIdentity.AssertSafePath(_home, path);
            if (!string.Equals(DeepSeekHarnessSourceResolver.SelectArtifact(Path.GetDirectoryName(path)!),
                    path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Harness source generation changed during discovery.");
            reader = new(path);
            var headerFrame = reader.Read(0, cancellationToken);
            if (headerFrame is null || headerFrame.Incomplete)
                failure = Diagnostic("incomplete_frame", request);
            else
            {
                ReadOnlyMemory<byte> firstLine = headerFrame.Lines().First();
                DeepSeekHarnessHeader header = DeepSeekHarnessParser.ReadHeader(firstLine, path);
                cursor = Deserialize(request.Cursor?.CursorJson);
                if (cursor is not null && cursor.State.SourceFingerprint == header.Fingerprint &&
                    (cursor.State.SessionId != header.Id || cursor.State.CreatedAt != header.CreatedAt ||
                     cursor.State.Cwd != header.Cwd || cursor.State.ParentSessionId != header.ParentId ||
                     cursor.State.IsChild != header.IsChild || cursor.State.Seeded != header.Seeded))
                    throw DeepSeekHarnessParser.Invalid();
                if (cursor is null || cursor.State.SourceFingerprint != header.Fingerprint || cursor.Offset > reader.Length)
                {
                    long nextRevision = cursor?.State.NextRevision ?? 1;
                    long cut = header.Seeded ? ReadInheritedCut(reader, cancellationToken) : 0;
                    cursor = new(0, 0, DeepSeekHarnessParser.Start(header, cut, nextRevision));
                    if (request.Cursor is not null) diagnostics.Add(new("jsonl.source_reset",
                        "The Harness source changed; canonical identities are retained.", request.Entity.SourceEntityId));
                }
                safeCursor = Serialize(cursor);
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
            failure = Diagnostic(exception is DeepSeekHarnessUnsupportedException ? "unsupported_format" : "invalid_source", request);
        }
        if (failure is not null)
        {
            reader?.Dispose();
            yield return Failure(request, safeCursor, cursor?.State.SourceFingerprint ?? "unavailable", failure);
            yield break;
        }

        using (DeepSeekHarnessLogReader source = reader!)
        {
            for (int batch = 0; batch < MaxPassBatches; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projection = new DeepSeekHarnessProjection(request.Instance, request.Entity, _time.GetUtcNow());
                int records = 0;
                bool end = false;
                failure = null;
                try
                {
                    while (records < MaxBatchRecords)
                    {
                        DeepSeekHarnessFrame? frame = source.Read(cursor!.Offset, cancellationToken);
                        if (frame is null) { end = true; break; }
                        if (frame.Incomplete)
                        {
                            diagnostics.Add(Diagnostic("incomplete_frame", request));
                            end = true;
                            break;
                        }
                        var lines = frame.Lines().ToArray();
                        if (cursor.Line >= lines.Length) throw DeepSeekHarnessParser.Invalid();
                        while (cursor.Line < lines.Length && records < MaxBatchRecords)
                        {
                            _parser.Parse(lines[cursor.Line], cursor.State, projection);
                            cursor = cursor with { Line = cursor.Line + 1 };
                            records++;
                        }
                        if (cursor.Line == lines.Length) cursor = cursor with { Offset = frame.End, Line = 0 };
                    }
                    DeepSeekHarnessSourceIdentity.AssertSafePath(_home, path);
                }
                catch (Exception exception) when (Expected(exception))
                {
                    failure = Diagnostic("invalid_record", request);
                }
                if (failure is not null)
                {
                    yield return Failure(request, safeCursor, cursor!.State.SourceFingerprint, failure);
                    yield break;
                }
                if (records == 0 && diagnostics.Count == 0) yield break;
                if (!end && batch == MaxPassBatches - 1)
                    diagnostics.Add(new("collector.batch_limit_reached", "Harness collection reached its bounded pass limit.", request.Entity.SourceEntityId));
                safeCursor = Serialize(cursor!);
                yield return new(request.Instance, request.Entity, projection.Events, safeCursor,
                    cursor!.State.SourceFingerprint, ParserVersion, diagnostics.ToArray())
                {
                    Sessions = [DeepSeekHarnessParser.Session(cursor.State, projection)],
                    Turns = projection.Turns, EventTools = projection.Tools, Dispatches = projection.Dispatches
                };
                diagnostics.Clear();
                if (end || cursor.Offset == source.Length) yield break;
                await Task.Yield();
            }
        }
    }

    private static long ReadInheritedCut(DeepSeekHarnessLogReader reader, CancellationToken token)
    {
        long offset = 0;
        long cut = -1;
        while (offset < reader.Length)
        {
            var frame = reader.Read(offset, token);
            if (frame is null || frame.Incomplete) throw new InvalidDataException("The fork prefix is incomplete.");
            foreach (ReadOnlyMemory<byte> bytes in frame.Lines())
            {
                using var document = DeepSeekHarnessParser.Document(bytes);
                JsonElement root = document.RootElement;
                if (DeepSeekHarnessParser.String(root, "type", 128) == "session/end-seed" &&
                    root.TryGetProperty("data", out var data) && data.TryGetProperty("inherited", out var inherited) &&
                    inherited.ValueKind == JsonValueKind.True)
                    cut = DeepSeekHarnessParser.Counter(root, "seq");
            }
            offset = frame.End;
        }
        return cut >= 0 ? cut : throw new InvalidDataException("The fork has no exact inherited cut.");
    }

    public IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [Path.Combine(_home, "sessions")];
    }
    public string NormalizeSourcePath(string path) => DeepSeekHarnessSourceIdentity.NormalizePath(path);
    public string GetSourceEntityId(string normalizedChangePath) => DeepSeekHarnessSourceIdentity.EntityId(normalizedChangePath);
    public bool IsWithinMonitoredRoots(SourceInstanceDescriptor instance, string normalizedChangePath)
    {
        ValidateInstance(instance);
        string relative = Path.GetRelativePath(Path.Combine(_home, "sessions"), normalizedChangePath);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        return !Path.IsPathRooted(relative) && parts.Length == 3 && !parts.Contains("..") &&
            DeepSeekHarnessSourceIdentity.TryReadGeneration(parts[^1], out _);
    }
    public bool TryGetCursorByteOffset(StoredCursor cursor, out long byteOffset)
    {
        try { var parsed = Deserialize(cursor.CursorJson); byteOffset = parsed?.Offset ?? 0; return parsed is not null; }
        catch (Exception exception) when (Expected(exception)) { byteOffset = 0; return false; }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (instance.AgentId != AgentId || instance.SourceKind != SourceKind.Jsonl ||
            instance.SourceInstanceId != DeepSeekHarnessSourceIdentity.InstanceId(_home) ||
            !string.Equals(NormalizeSourcePath(instance.RootPath), _home, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid Harness source instance.", nameof(instance));
    }

    private CollectedBatch Failure(CollectionRequest request, string cursor, string fingerprint, CollectorDiagnostic diagnostic) =>
        new(request.Instance, request.Entity, [], cursor, fingerprint, ParserVersion, [diagnostic]);
    private static CollectorDiagnostic Diagnostic(string suffix, CollectionRequest request) =>
        new("deepseek-harness." + suffix, "Harness source accounting could not be completed safely.", request.Entity.SourceEntityId);
    private static bool Expected(Exception exception) => exception is IOException or InvalidDataException or JsonException or
        DecoderFallbackException or ArgumentException or InvalidOperationException or OverflowException or
        UnauthorizedAccessException or SecurityException or NotSupportedException;

    private static DeepSeekHarnessCursor? Deserialize(string? json)
    {
        if (json is null) return null;
        if (json.Length > MaxCursorCharacters) throw DeepSeekHarnessParser.Invalid();
        var cursor = JsonSerializer.Deserialize<DeepSeekHarnessCursor>(json, DeepSeekHarnessParser.CursorOptions);
        if (cursor is null || cursor.State is null || cursor.Offset < 0 || cursor.Line is < 0 or > 20000)
            throw DeepSeekHarnessParser.Invalid();
        DeepSeekHarnessState state = cursor.State;
        if (cursor.Offset == 0 && cursor.Line == 0 && state.SessionId == "" &&
            state.SourceFingerprint == "" && state.LastSeq == -1) return null;
        static bool Hash(string? value) => value is null || value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool Text(string? value, int max) => value is null || value.Length <= max && !value.Any(char.IsControl);
        if (!Text(state.SessionId, 1024) || string.IsNullOrWhiteSpace(state.SessionId) ||
            state.SourceFingerprint is not { Length: 64 } || !Hash(state.SourceFingerprint) ||
            state.LastSeq < -1 || state.NextRevision < 1 || state.InheritedCut < 0 ||
            state.ActiveTurn < 0 || state.ActiveStep < 0 || state.UserMessages < 0 || state.GoalRevision < 0 ||
            !Text(state.Cwd, 32767) || !Text(state.ParentSessionId, 1024) ||
            !Text(state.Model, 512) || !Text(state.Provider, 512) ||
            state.Preview != DeepSeekHarnessParser.Preview(state.Preview) ||
            state.Title != DeepSeekHarnessParser.Preview(state.Title) ||
            !Hash(state.TurnHash) || !Hash(state.OriginTurnHash) || !Hash(state.FirstUserHash) ||
            !Hash(state.GoalIdHash) || !Hash(state.GoalOriginHash) ||
            state.ToolCalls is null || state.ToolCalls.Count > 256 ||
            state.ToolCalls.Any(pair => !Hash(pair.Key) || !Text(pair.Value, 128)) ||
            state.CreatedAt.Offset != TimeSpan.Zero ||
            new[] { state.LastTime, state.TurnStarted, state.TitleTime }.Any(value =>
                value.HasValue && value.Value.Offset != TimeSpan.Zero))
            throw DeepSeekHarnessParser.Invalid();
        return cursor;
    }
    private static string Serialize(DeepSeekHarnessCursor cursor)
    {
        string json = JsonSerializer.Serialize(cursor, DeepSeekHarnessParser.CursorOptions);
        if (json.Length > MaxCursorCharacters) throw DeepSeekHarnessParser.Invalid();
        return json;
    }
}

internal sealed record DeepSeekHarnessCursor(long Offset, int Line, DeepSeekHarnessState State);
