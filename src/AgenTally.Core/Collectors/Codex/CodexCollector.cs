using System.Runtime.CompilerServices;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.Codex;

public sealed class CodexCollector :
    IIncrementalFileCollector,
    IUsageSessionNameSource,
    IDisposable
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private const string EmptyContentFingerprint =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string UnsafePathMessage =
        "Codex source path safety validation failed.";

    private readonly string _codexHome;
    private readonly TimeProvider _timeProvider;
    private readonly CodexSourceResolver _resolver;
    private readonly IncrementalJsonlReader _reader;
    private readonly CodexRolloutParser _parser;
    private readonly IUsageSessionNameSource _sessionNames;
    private readonly IDisposable? _ownedSessionNames;

    public CodexCollector(
        string codexHome,
        TimeProvider? timeProvider = null,
        CodexSourceResolver? resolver = null,
        IncrementalJsonlReader? reader = null,
        CodexRolloutParser? parser = null,
        IUsageSessionNameSource? sessionNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        _codexHome = CodexSourceIdentity.NormalizePath(codexHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new CodexSourceResolver();
        _reader = reader ?? new IncrementalJsonlReader();
        _parser = parser ?? new CodexRolloutParser();
        if (sessionNames is null)
        {
            var ownedSessionNames = new CodexSessionNameSource(_codexHome);
            _sessionNames = ownedSessionNames;
            _ownedSessionNames = ownedSessionNames;
        }
        else
        {
            _sessionNames = sessionNames;
        }
    }

    public string AgentId => "codex";

    public string ParserVersion => CodexRolloutParser.CurrentParserVersion;

    IReadOnlyList<string> ISourceFileChangeCollector.GetWatchRoots(
        SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return
        [
            Path.Combine(_codexHome, "sessions"),
            Path.Combine(_codexHome, "archived_sessions")
        ];
    }

    string ISourceFileChangeCollector.NormalizeSourcePath(string path) =>
        CodexSourceIdentity.NormalizePath(path);

    string ISourceFileChangeCollector.GetSourceEntityId(string normalizedPath) =>
        CodexSourceIdentity.EntityId(normalizedPath);

    bool ISourceFileChangeCollector.IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath)
    {
        ValidateInstance(instance);
        return IsWithinKnownRoot(normalizedPath);
    }

    bool IIncrementalFileCollector.TryGetCursorByteOffset(
        StoredCursor cursor,
        out long byteOffset)
    {
        CodexCursor parsed = CodexCursor.DeserializeOrStart(
            cursor.CursorJson,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);
        byteOffset = parsed.Jsonl.ByteOffset;
        return diagnostic is null;
    }

    public Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
        CancellationToken cancellationToken) =>
        _sessionNames.ReadSessionNamesAsync(cancellationToken);

    public void Dispose() => _ownedSessionNames?.Dispose();

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_codexHome, cancellationToken);
    }

    internal async Task<bool> HasContinuousSourceAsync(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        StoredCursor? storedCursor,
        CancellationToken cancellationToken)
    {
        if (storedCursor is null)
        {
            return false;
        }

        try
        {
            var request = new CollectionRequest(
                instance,
                entity,
                storedCursor,
                CollectionReason.RepairScan);
            ValidateRequest(request);
            if (!CursorBelongsToEntity(storedCursor, entity) ||
                !CursorPathMatchesEntity(storedCursor, entity))
            {
                return false;
            }

            CodexCursor cursor = CodexCursor.DeserializeOrStart(
                storedCursor.CursorJson,
                hasStoredCursor: true,
                out CollectorDiagnostic? cursorDiagnostic);
            if (cursorDiagnostic is not null ||
                cursor == CodexCursor.Start ||
                cursor.Jsonl.ByteOffset <= 0 ||
                cursor.Jsonl.LineNumber <= 0 ||
                !ValidFingerprint(storedCursor.SourceFingerprint) ||
                !string.Equals(
                    storedCursor.SourceFingerprint,
                    cursor.Jsonl.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }

            EnsureNoReparsePoints(entity.SourcePath);
            JsonlReadBatch readBatch = await _reader.ReadBatchAsync(
                entity.SourcePath,
                cursor.Jsonl,
                maxLines: 1,
                cancellationToken);
            EnsureNoReparsePoints(entity.SourcePath);

            return readBatch.Diagnostic?.Code is not "jsonl.source_reset" &&
                string.Equals(
                    readBatch.NextCursor.SourceFingerprint,
                    cursor.Jsonl.SourceFingerprint,
                    StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        IReadOnlyDictionary<string, UsageSessionNameMetadata> sessionNames =
            (await ReadSessionNamesAsync(cancellationToken))
            .ToDictionary(value => value.SessionId, StringComparer.Ordinal);

        CodexCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null && !CursorBelongsToEntity(request.Cursor, request.Entity))
        {
            cursor = CodexCursor.Start;
            cursorDiagnostic = CursorSourceMismatchDiagnostic();
        }
        else
        {
            if (request.Cursor is not null &&
                !string.Equals(
                    request.Cursor.ParserVersion,
                    CodexRolloutParser.CurrentParserVersion,
                    StringComparison.Ordinal))
            {
                throw new CodexParserRebuildRequiredException(
                    request.Cursor.ParserVersion,
                    CodexRolloutParser.CurrentParserVersion);
            }

            cursor = CodexCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                hasStoredCursor: request.Cursor is not null,
                out cursorDiagnostic);
        }

        int batchCount = 0;
        while (true)
        {
            EnsureNoReparsePoints(request.Entity.SourcePath);
            JsonlReadBatch readBatch = await _reader.ReadBatchAsync(
                request.Entity.SourcePath,
                cursor.Jsonl,
                MaxBatchLines,
                cancellationToken);
            EnsureNoReparsePoints(request.Entity.SourcePath);

            if (string.IsNullOrWhiteSpace(readBatch.NextCursor.SourceFingerprint))
            {
                if (readBatch.Diagnostic?.Code is "jsonl.first_line_too_long")
                {
                    throw new InvalidDataException(readBatch.Diagnostic.Message);
                }

                bool hasResetToPersist = request.Cursor is not null &&
                    (readBatch.Diagnostic?.Code is "jsonl.source_reset" ||
                     cursorDiagnostic is not null);
                if (hasResetToPersist)
                {
                    var resetDiagnostics = new List<CollectorDiagnostic>();
                    AddDiagnostic(resetDiagnostics, cursorDiagnostic, request.Entity.SourceEntityId);
                    AddDiagnostic(resetDiagnostics, readBatch.Diagnostic, request.Entity.SourceEntityId);

                    // An empty/truncated source has no current first-line fingerprint.
                    // Keep a structurally valid last-known hash, or the SHA-256 of empty content.
                    string resetFingerprint = ValidFingerprint(request.Cursor!.SourceFingerprint)
                        ? request.Cursor.SourceFingerprint
                        : EmptyContentFingerprint;
                    yield return new CollectedBatch(
                        request.Instance,
                        request.Entity,
                        [],
                        CodexCursor.Start.Serialize(),
                        resetFingerprint,
                        CodexRolloutParser.CurrentParserVersion,
                        resetDiagnostics)
                    {
                        EventRevisionHighWatermark = 0
                    };
                    yield break;
                }

                yield break;
            }

            CodexParseState state = readBatch.Diagnostic?.Code is "jsonl.source_reset"
                ? new CodexParseState()
                : cursor.State;
            var events = new List<UsageEvent>();
            var sessions = new List<UsageSessionMetadata>();
            var turns = new List<UsageTurnMetadata>();
            var eventTools = new List<UsageEventToolMetadata>();
            var dispatches = new List<UsageTurnDispatch>();
            var diagnostics = new List<CollectorDiagnostic>();

            if (cursorDiagnostic is not null)
            {
                diagnostics.Add(cursorDiagnostic with
                {
                    SourceEntityId = request.Entity.SourceEntityId
                });
                cursorDiagnostic = null;
            }

            if (readBatch.Diagnostic is not null)
            {
                diagnostics.Add(readBatch.Diagnostic with
                {
                    SourceEntityId = request.Entity.SourceEntityId
                });
            }

            var context = new CodexEventContext(
                request.Instance,
                request.Entity,
                readBatch.NextCursor.SourceFingerprint,
                _timeProvider.GetUtcNow());
            foreach (JsonlLine line in readBatch.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CodexParseResult result = _parser.ParseLine(line, state, context);
                state = result.State;
                if (result.Event is not null)
                {
                    events.Add(result.Event);
                }

                if (result.SessionMetadata is not null)
                {
                    UsageSessionMetadata session = result.SessionMetadata;
                    if (sessionNames.TryGetValue(
                            session.SessionId,
                            out UsageSessionNameMetadata? name))
                    {
                        session = session with
                        {
                            SessionName = name.SessionName,
                            SessionNameUpdatedAtUtc = name.UpdatedAtUtc
                        };
                    }

                    sessions.Add(session);
                }

                if (result.TurnMetadata is not null)
                {
                    turns.Add(result.TurnMetadata);
                }

                eventTools.AddRange(result.EventTools);
                if (result.Dispatch is not null)
                {
                    dispatches.Add(result.Dispatch);
                }

                if (result.Diagnostic is not null)
                {
                    diagnostics.Add(result.Diagnostic);
                }
            }

            cursor = new CodexCursor(readBatch.NextCursor, state);
            batchCount++;
            bool collectionLimitReached =
                batchCount >= MaxCollectionBatches && !readBatch.EndOfFile;
            if (collectionLimitReached)
            {
                diagnostics.Add(CollectionLimitDiagnostic(request.Entity.SourceEntityId));
            }

            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                events,
                cursor.Serialize(),
                readBatch.NextCursor.SourceFingerprint,
                CodexRolloutParser.CurrentParserVersion,
                diagnostics)
            {
                EventRevisionHighWatermark = state.TokenEventIndex,
                Sessions = sessions,
                Turns = turns,
                EventTools = eventTools,
                Dispatches = dispatches
            };

            if (readBatch.EndOfFile || collectionLimitReached)
            {
                yield break;
            }
        }
    }

    private void ValidateRequest(CollectionRequest request)
    {
        SourceInstanceDescriptor instance = request.Instance ??
            throw new ArgumentException("Collection instance is required.", nameof(request));
        SourceEntityDescriptor entity = request.Entity ??
            throw new ArgumentException("Collection entity is required.", nameof(request));

        string normalizedRoot;
        string normalizedPath;
        try
        {
            normalizedRoot = CodexSourceIdentity.NormalizePath(instance.RootPath);
            normalizedPath = CodexSourceIdentity.NormalizePath(entity.SourcePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "Collection request contains an invalid source path.",
                nameof(request),
                exception);
        }

        bool valid = string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) &&
            instance.SourceKind == SourceKind.Jsonl &&
            string.Equals(
                instance.SourceInstanceId,
                CodexSourceIdentity.InstanceId(_codexHome),
                StringComparison.Ordinal) &&
            string.Equals(normalizedRoot, _codexHome, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                entity.SourceInstanceId,
                instance.SourceInstanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                entity.SourceEntityId,
                CodexSourceIdentity.EntityId(normalizedPath),
                StringComparison.Ordinal) &&
            string.Equals(
                Path.GetExtension(normalizedPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase) &&
            IsWithinKnownRoot(normalizedPath);
        if (!valid)
        {
            throw new ArgumentException(
                "Collection request does not belong to the injected Codex home.",
                nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        string normalizedRoot;
        try
        {
            normalizedRoot = CodexSourceIdentity.NormalizePath(instance.RootPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "Source instance contains an invalid root path.",
                nameof(instance),
                exception);
        }

        bool valid = string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) &&
            instance.SourceKind == SourceKind.Jsonl &&
            string.Equals(
                instance.SourceInstanceId,
                CodexSourceIdentity.InstanceId(_codexHome),
                StringComparison.Ordinal) &&
            string.Equals(normalizedRoot, _codexHome, StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new ArgumentException(
                "Source instance does not belong to the injected Codex home.",
                nameof(instance));
        }
    }

    private bool IsWithinKnownRoot(string normalizedPath) =>
        IsWithinRoot(normalizedPath, Path.Combine(_codexHome, "sessions")) ||
        IsWithinRoot(normalizedPath, Path.Combine(_codexHome, "archived_sessions"));

    private static bool IsWithinRoot(string normalizedPath, string rootPath)
    {
        string normalizedRoot = CodexSourceIdentity.NormalizePath(rootPath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return !string.Equals(relative, ".", StringComparison.Ordinal) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private void EnsureNoReparsePoints(string sourcePath)
    {
        try
        {
            string normalizedPath = CodexSourceIdentity.NormalizePath(sourcePath);
            string sessionsRoot = CodexSourceIdentity.NormalizePath(
                Path.Combine(_codexHome, "sessions"));
            string archiveRoot = CodexSourceIdentity.NormalizePath(
                Path.Combine(_codexHome, "archived_sessions"));
            string root = IsWithinRoot(normalizedPath, sessionsRoot)
                ? sessionsRoot
                : IsWithinRoot(normalizedPath, archiveRoot)
                    ? archiveRoot
                    : throw new System.Security.SecurityException();

            EnsureNotReparsePoint(root);
            string current = root;
            string relative = Path.GetRelativePath(root, normalizedPath);
            foreach (string component in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                EnsureNotReparsePoint(current);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            throw new IOException(UnsafePathMessage);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new System.Security.SecurityException();
        }
    }

    private static bool ValidFingerprint(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AddDiagnostic(
        ICollection<CollectorDiagnostic> diagnostics,
        CollectorDiagnostic? diagnostic,
        string entityId)
    {
        if (diagnostic is not null)
        {
            diagnostics.Add(diagnostic with { SourceEntityId = entityId });
        }
    }

    private static bool CursorBelongsToEntity(
        StoredCursor cursor,
        SourceEntityDescriptor entity) =>
        string.Equals(
            cursor.SourceInstanceId,
            entity.SourceInstanceId,
            StringComparison.Ordinal) &&
        string.Equals(
            cursor.SourceEntityId,
            entity.SourceEntityId,
            StringComparison.Ordinal);

    private static bool CursorPathMatchesEntity(
        StoredCursor cursor,
        SourceEntityDescriptor entity) =>
        string.Equals(
            CodexSourceIdentity.NormalizePath(cursor.SourcePath),
            CodexSourceIdentity.NormalizePath(entity.SourcePath),
            StringComparison.OrdinalIgnoreCase);

    private static CollectorDiagnostic CursorSourceMismatchDiagnostic() => new(
        "collector.cursor_source_mismatch",
        "The stored cursor belongs to a different source and was reset.");

    private static CollectorDiagnostic CollectionLimitDiagnostic(string entityId) => new(
        "collector.batch_limit_reached",
        "A single collection reached 25 batches (at most 5000 lines) and will resume from its cursor.",
        entityId);
}
