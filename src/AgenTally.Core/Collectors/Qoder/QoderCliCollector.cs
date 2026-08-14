using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Core.Collectors.QwenCode;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.Qoder;

// Current official Qoder CLI transcripts document message/session structure but
// no per-call Token counters. This collector deliberately indexes only safe
// session/turn structure and fails closed for Token until a real source contract
// proves the fields and semantics.
public sealed class QoderCliCollector : IIncrementalFileCollector
{
    public const string CurrentParserVersion = "qoder-cli-transcript-v1";
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private readonly string _home;
    private readonly TimeProvider _timeProvider;
    private readonly IncrementalJsonlReader _reader;

    public QoderCliCollector(
        string qoderHome,
        TimeProvider? timeProvider = null,
        IncrementalJsonlReader? reader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qoderHome);
        _home = QoderSourceIdentity.NormalizePath(qoderHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reader = reader ?? new IncrementalJsonlReader();
    }

    public string AgentId => "qoder";

    public string ParserVersion => CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.MissingCapability;

    public string? MaintenanceCompatibilityCode => "qoder_cli_token_usage_unavailable";

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        string instanceId = QoderSourceIdentity.CliInstanceId(_home);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            AgentId,
            SourceKind.Jsonl,
            "Qoder CLI (Windows)",
            _home);
        string projects = Path.Combine(_home, "projects");
        if (!Directory.Exists(projects))
        {
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
        }
        try
        {
            if (IsReparse(_home) || IsReparse(projects))
            {
                return ValueTask.FromResult(Reparse(instance));
            }
            var entities = new List<SourceEntityDescriptor>();
            foreach (string project in Directory.EnumerateDirectories(projects))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string transcript = Path.Combine(project, "transcript");
                if (IsReparse(project))
                {
                    return ValueTask.FromResult(Reparse(instance));
                }
                if (!Directory.Exists(transcript))
                {
                    continue;
                }
                if (IsReparse(transcript))
                {
                    return ValueTask.FromResult(Reparse(instance));
                }
                foreach (string file in Directory.EnumerateFiles(transcript, "*.jsonl"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparse(file))
                    {
                        return ValueTask.FromResult(Reparse(instance));
                    }
                    string path = QoderSourceIdentity.NormalizePath(file);
                    entities.Add(new SourceEntityDescriptor(
                        instanceId,
                        QoderSourceIdentity.CliEntityId(path),
                        path));
                }
            }
            return ValueTask.FromResult(new SourceProbeResult([instance], entities, []));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                [new CollectorDiagnostic(
                    "qoder-cli.source_unavailable",
                    "The Qoder CLI transcripts could not be inspected safely.")]));
        }
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.Cursor is not null &&
            !string.Equals(request.Cursor.ParserVersion, ParserVersion, StringComparison.Ordinal))
        {
            throw new AgentParserRebuildRequiredException(
                AgentId,
                request.Cursor.ParserVersion,
                ParserVersion);
        }
        QoderCliCursor cursor = QoderCliCursor.DeserializeOrStart(
            request.Cursor?.CursorJson,
            request.Cursor is not null,
            out CollectorDiagnostic? cursorDiagnostic);
        if (request.Cursor is not null &&
            (!string.Equals(request.Cursor.SourceInstanceId, request.Instance.SourceInstanceId,
                 StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceEntityId, request.Entity.SourceEntityId,
                 StringComparison.Ordinal)))
        {
            cursor = QoderCliCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "collector.cursor_source_mismatch",
                "The stored cursor did not belong to the Qoder CLI transcript and was reset.");
        }

        string expectedSessionId = Path.GetFileNameWithoutExtension(request.Entity.SourcePath);
        int batches = 0;
        while (true)
        {
            EnsureNoReparsePoints(request.Entity.SourcePath);
            JsonlReadBatch read = await _reader.ReadBatchAsync(
                request.Entity.SourcePath,
                cursor.Jsonl,
                MaxBatchLines,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(read.NextCursor.SourceFingerprint))
            {
                yield break;
            }

            QoderCliState state = read.Diagnostic?.Code == "jsonl.source_reset"
                ? new QoderCliState()
                : cursor.State;
            var sessions = new List<UsageSessionMetadata>();
            var turns = new List<UsageTurnMetadata>();
            var diagnostics = new List<CollectorDiagnostic>();
            if (cursorDiagnostic is not null)
            {
                diagnostics.Add(cursorDiagnostic with { SourceEntityId = request.Entity.SourceEntityId });
                cursorDiagnostic = null;
            }
            if (read.Diagnostic is not null)
            {
                diagnostics.Add(read.Diagnostic with { SourceEntityId = request.Entity.SourceEntityId });
            }
            foreach (JsonlLine line in read.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ParseLine(line, state, request, expectedSessionId,
                    out state, out UsageSessionMetadata? session,
                    out UsageTurnMetadata? turn, out CollectorDiagnostic? diagnostic);
                if (session is not null)
                {
                    sessions.Add(session);
                }
                if (turn is not null)
                {
                    turns.Add(turn);
                }
                if (diagnostic is not null)
                {
                    diagnostics.Add(diagnostic);
                }
            }
            cursor = new QoderCliCursor(read.NextCursor, state);
            batches++;
            bool limited = batches >= MaxCollectionBatches && !read.EndOfFile;
            if (limited)
            {
                diagnostics.Add(new CollectorDiagnostic(
                    "collector.batch_limit_reached",
                    "The Qoder CLI collection reached its bounded batch limit.",
                    request.Entity.SourceEntityId));
            }
            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                [],
                cursor.Serialize(),
                read.NextCursor.SourceFingerprint,
                ParserVersion,
                diagnostics)
            {
                Sessions = sessions,
                Turns = turns
            };
            if (read.EndOfFile || limited)
            {
                yield break;
            }
        }
    }

    public IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [Path.Combine(_home, "projects")];
    }

    public string NormalizeSourcePath(string path) => QoderSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        QoderSourceIdentity.CliEntityId(normalizedChangePath);

    public bool IsWithinMonitoredRoots(SourceInstanceDescriptor instance, string normalizedChangePath)
    {
        ValidateInstance(instance);
        return HasExpectedShape(normalizedChangePath);
    }

    public bool TryGetCursorByteOffset(StoredCursor cursor, out long byteOffset)
    {
        QoderCliCursor parsed = QoderCliCursor.DeserializeOrStart(
            cursor.CursorJson,
            true,
            out CollectorDiagnostic? diagnostic);
        byteOffset = parsed.Jsonl.ByteOffset;
        return diagnostic is null;
    }

    private void ParseLine(
        JsonlLine line,
        QoderCliState state,
        CollectionRequest request,
        string expectedSessionId,
        out QoderCliState next,
        out UsageSessionMetadata? session,
        out UsageTurnMetadata? turn,
        out CollectorDiagnostic? diagnostic)
    {
        next = state;
        session = null;
        turn = null;
        diagnostic = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.Utf8,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            string? type = ReadString(root, "type", 64);
            if (type is not ("user" or "assistant"))
            {
                return;
            }
            string? sourceSession = ReadString(root, "sessionId", 1024) ??
                ReadString(root, "session_id", 1024);
            if (sourceSession is not null &&
                !string.Equals(sourceSession, expectedSessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Qoder CLI session identity did not match its file.");
            }
            DateTimeOffset timestamp = ReadTimestamp(root) ??
                throw new InvalidDataException("Qoder CLI timestamp was invalid.");
            next = UpdateProject(root, state with { SessionId = expectedSessionId });
            if (type == "user")
            {
                string uuid = ReadString(root, "uuid", 1024) ??
                    throw new InvalidDataException("Qoder CLI user identity was missing.");
                string? preview = null;
                if (root.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    preview = QwenCodeJsonlParser.NormalizePreview(content.GetString());
                }
                next = next with
                {
                    TurnIdHash = QoderSourceIdentity.HashIdentity(
                        "qoder-cli-turn",
                        $"{expectedSessionId}\0{uuid}"),
                    TurnStartedAtUtc = timestamp,
                    PromptPreview = preview
                };
                turn = CreateTurn(next, request, null);
            }
            else
            {
                turn = CreateTurn(next, request, timestamp);
            }
            session = CreateSession(next, request, timestamp);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or ArgumentException or OverflowException)
        {
            diagnostic = new CollectorDiagnostic(
                "qoder-cli.invalid_transcript_record",
                "A Qoder CLI transcript record contained unsupported structural data.",
                request.Entity.SourceEntityId,
                line.ByteOffset);
        }
    }

    private static UsageSessionMetadata CreateSession(
        QoderCliState state,
        CollectionRequest request,
        DateTimeOffset observedAtUtc) => new(
        request.Instance.AgentId,
        request.Instance.SourceInstanceId,
        request.Entity.SourceEntityId,
        state.SessionId!,
        SessionKind.Primary,
        null,
        null,
        SessionRelationOrigin.None,
        SessionRelationState.None,
        ReplayState.Active,
        CompatibilityLevel.MissingCapability,
        observedAtUtc,
        CurrentParserVersion)
    {
        ProjectId = state.ProjectId,
        ProjectPath = state.ProjectPath,
        ProjectRepositoryIdentityHash = state.ProjectRepositoryIdentityHash,
        SessionRole = SessionRole.Main,
        SessionName = state.PromptPreview,
        SessionNameUpdatedAtUtc = state.PromptPreview is null ? null : state.TurnStartedAtUtc
    };

    private static UsageTurnMetadata? CreateTurn(
        QoderCliState state,
        CollectionRequest request,
        DateTimeOffset? completedAtUtc) =>
        state.SessionId is null || state.TurnIdHash is null || !state.TurnStartedAtUtc.HasValue
            ? null
            : new UsageTurnMetadata(
                request.Instance.AgentId,
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                state.SessionId,
                state.TurnIdHash,
                state.TurnStartedAtUtc.Value,
                completedAtUtc >= state.TurnStartedAtUtc ? completedAtUtc : null,
                state.PromptPreview,
                1,
                CurrentParserVersion);

    private static QoderCliState UpdateProject(JsonElement root, QoderCliState state)
    {
        string? cwd = ReadString(root, "cwd", CodexProjectIdentity.MaxProjectPathCharacters);
        return cwd is not null && CodexProjectIdentity.TryCreate(cwd, out CodexProjectIdentity project)
            ? state with
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                ProjectRepositoryIdentityHash = project.RepositoryIdentityHash
            }
            : state;
    }

    private void ValidateRequest(CollectionRequest request)
    {
        ValidateInstance(request.Instance);
        string path = QoderSourceIdentity.NormalizePath(request.Entity.SourcePath);
        if (!string.Equals(path, request.Entity.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Entity.SourceInstanceId, request.Instance.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(request.Entity.SourceEntityId, QoderSourceIdentity.CliEntityId(path),
                StringComparison.Ordinal) || !HasExpectedShape(path))
        {
            throw new ArgumentException("The Qoder CLI collection request was invalid.", nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (instance is null ||
            !string.Equals(instance.SourceInstanceId, QoderSourceIdentity.CliInstanceId(_home),
                StringComparison.Ordinal) ||
            !string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            instance.SourceKind != SourceKind.Jsonl ||
            !string.Equals(QoderSourceIdentity.NormalizePath(instance.RootPath), _home,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Qoder CLI source instance was invalid.", nameof(instance));
        }
    }

    private bool HasExpectedShape(string path)
    {
        string relative = Path.GetRelativePath(Path.Combine(_home, "projects"),
            QoderSourceIdentity.NormalizePath(path));
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
            string.Equals(segments[1], "transcript", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(segments[2]), ".jsonl", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
    }

    private void EnsureNoReparsePoints(string sourcePath)
    {
        foreach (string path in new[]
        {
            _home,
            Path.Combine(_home, "projects"),
            Path.GetDirectoryName(Path.GetDirectoryName(sourcePath)!)!,
            Path.GetDirectoryName(sourcePath)!,
            sourcePath
        })
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Qoder CLI source path safety validation failed.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or SecurityException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("Qoder CLI source path safety validation failed.", exception);
            }
        }
    }

    private static SourceProbeResult Reparse(SourceInstanceDescriptor instance) => new(
        [instance],
        [],
        [new CollectorDiagnostic(
            "qoder-cli.source_reparse_point",
            "A Qoder CLI source path is a reparse point and was skipped.")]);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string? ReadString(JsonElement root, string name, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? result = value.GetString();
        return result is { Length: > 0 } && result.Length <= maximum &&
               !string.IsNullOrWhiteSpace(result) && !result.Any(char.IsControl)
            ? result
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        string? value = ReadString(root, "timestamp", 128);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
    }
}
