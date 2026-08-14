using System.Runtime.CompilerServices;
using System.Security;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed class ClaudeCodeCollector : IIncrementalFileCollector
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private const string EmptyContentFingerprint =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string UnsafePathMessage =
        "Claude Code source path safety validation failed.";

    private readonly string _sourceRoot;
    private readonly string _monitoredRoot;
    private readonly string _expectedInstanceId;
    private readonly TimeProvider _timeProvider;
    private readonly IClaudeCodeSourceResolver _resolver;
    private readonly IncrementalJsonlReader _reader;
    private readonly ClaudeCodeTranscriptParser _parser;

    public ClaudeCodeCollector(
        string claudeHome,
        TimeProvider? timeProvider = null,
        ClaudeCodeSourceResolver? resolver = null,
        IncrementalJsonlReader? reader = null,
        ClaudeCodeTranscriptParser? parser = null)
        : this(
            claudeHome,
            Path.Combine(claudeHome, "projects"),
            ClaudeCodeSourceIdentity.InstanceId(claudeHome),
            timeProvider,
            resolver ?? new ClaudeCodeSourceResolver(),
            reader,
            parser)
    {
    }

    internal ClaudeCodeCollector(
        string sourceRoot,
        string monitoredRoot,
        string expectedInstanceId,
        TimeProvider? timeProvider,
        IClaudeCodeSourceResolver resolver,
        IncrementalJsonlReader? reader = null,
        ClaudeCodeTranscriptParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(monitoredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedInstanceId);
        _sourceRoot = ClaudeCodeSourceIdentity.NormalizePath(sourceRoot);
        _monitoredRoot = ClaudeCodeSourceIdentity.NormalizePath(monitoredRoot);
        _expectedInstanceId = expectedInstanceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _reader = reader ?? new IncrementalJsonlReader();
        _parser = parser ?? new ClaudeCodeTranscriptParser();
    }

    public string AgentId => "claude-code";

    public string ParserVersion => _parser.ParserVersion;

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_sourceRoot, cancellationToken);
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        ClaudeCodeCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null &&
            !CursorBelongsToEntity(request.Cursor, request.Entity))
        {
            cursor = ClaudeCodeCursor.Start;
            cursorDiagnostic = CursorSourceMismatchDiagnostic();
        }
        else
        {
            if (request.Cursor is not null &&
                !string.Equals(
                    request.Cursor.ParserVersion,
                    ParserVersion,
                    StringComparison.Ordinal))
            {
                throw new AgentParserRebuildRequiredException(
                    AgentId,
                    request.Cursor.ParserVersion,
                    ParserVersion);
            }

            cursor = ClaudeCodeCursor.DeserializeOrStart(
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

            if (string.IsNullOrWhiteSpace(
                    readBatch.NextCursor.SourceFingerprint))
            {
                if (readBatch.Diagnostic?.Code is "jsonl.first_line_too_long")
                {
                    throw new InvalidDataException(readBatch.Diagnostic.Message);
                }

                bool persistReset = request.Cursor is not null &&
                    (readBatch.Diagnostic?.Code is "jsonl.source_reset" ||
                     cursorDiagnostic is not null);
                if (persistReset)
                {
                    var resetDiagnostics = new List<CollectorDiagnostic>();
                    AddDiagnostic(
                        resetDiagnostics,
                        cursorDiagnostic,
                        request.Entity.SourceEntityId);
                    AddDiagnostic(
                        resetDiagnostics,
                        readBatch.Diagnostic,
                        request.Entity.SourceEntityId);
                    string resetFingerprint = ValidFingerprint(
                        request.Cursor!.SourceFingerprint)
                            ? request.Cursor.SourceFingerprint
                            : EmptyContentFingerprint;
                    yield return new CollectedBatch(
                        request.Instance,
                        request.Entity,
                        [],
                        ClaudeCodeCursor.Start.Serialize(),
                        resetFingerprint,
                        ParserVersion,
                        resetDiagnostics);
                }

                yield break;
            }

            ClaudeCodeParseState state =
                readBatch.Diagnostic?.Code is "jsonl.source_reset"
                    ? new ClaudeCodeParseState()
                    : cursor.State;
            var events = new List<UsageEvent>();
            var sessions = new List<UsageSessionMetadata>();
            var turns = new List<UsageTurnMetadata>();
            var eventTools = new List<UsageEventToolMetadata>();
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

            var context = new ClaudeCodeEventContext(
                request.Instance,
                request.Entity,
                readBatch.NextCursor.SourceFingerprint,
                _timeProvider.GetUtcNow());
            foreach (JsonlLine line in readBatch.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClaudeCodeParseResult result = _parser.ParseLine(
                    line,
                    state,
                    context);
                state = result.State;
                if (result.Event is not null)
                {
                    events.Add(result.Event);
                }

                if (result.SessionMetadata is not null)
                {
                    sessions.Add(result.SessionMetadata);
                }

                if (result.TurnMetadata is not null)
                {
                    turns.Add(result.TurnMetadata);
                }

                eventTools.AddRange(result.EventTools);
                if (result.Diagnostic is not null)
                {
                    diagnostics.Add(result.Diagnostic);
                }
            }

            cursor = new ClaudeCodeCursor(readBatch.NextCursor, state);
            batchCount++;
            bool collectionLimitReached =
                batchCount >= MaxCollectionBatches && !readBatch.EndOfFile;
            if (collectionLimitReached)
            {
                diagnostics.Add(CollectionLimitDiagnostic(
                    request.Entity.SourceEntityId));
            }

            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                events,
                cursor.Serialize(),
                readBatch.NextCursor.SourceFingerprint,
                ParserVersion,
                diagnostics)
            {
                Sessions = sessions,
                Turns = turns,
                EventTools = eventTools
            };

            if (readBatch.EndOfFile || collectionLimitReached)
            {
                yield break;
            }
        }
    }

    IReadOnlyList<string> ISourceFileChangeCollector.GetWatchRoots(
        SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [_monitoredRoot];
    }

    string ISourceFileChangeCollector.NormalizeSourcePath(string path) =>
        ClaudeCodeSourceIdentity.NormalizePath(path);

    string ISourceFileChangeCollector.GetSourceEntityId(string normalizedPath) =>
        ClaudeCodeSourceIdentity.EntityId(normalizedPath);

    bool ISourceFileChangeCollector.IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath)
    {
        ValidateInstance(instance);
        return IsWithinMonitoredRoot(normalizedPath);
    }

    bool IIncrementalFileCollector.TryGetCursorByteOffset(
        StoredCursor cursor,
        out long byteOffset)
    {
        ClaudeCodeCursor parsed = ClaudeCodeCursor.DeserializeOrStart(
            cursor.CursorJson,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);
        byteOffset = parsed.Jsonl.ByteOffset;
        return diagnostic is null;
    }

    private void ValidateRequest(CollectionRequest request)
    {
        SourceInstanceDescriptor instance = request.Instance ??
            throw new ArgumentException(
                "Collection instance is required.",
                nameof(request));
        SourceEntityDescriptor entity = request.Entity ??
            throw new ArgumentException(
                "Collection entity is required.",
                nameof(request));
        ValidateInstance(instance);

        string normalizedPath;
        try
        {
            normalizedPath = ClaudeCodeSourceIdentity.NormalizePath(
                entity.SourcePath);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new ArgumentException(
                "Collection request contains an invalid source path.",
                nameof(request),
                exception);
        }

        bool valid = string.Equals(
                entity.SourceInstanceId,
                instance.SourceInstanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                entity.SourceEntityId,
                ClaudeCodeSourceIdentity.EntityId(normalizedPath),
                StringComparison.Ordinal) &&
            string.Equals(
                Path.GetExtension(normalizedPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase) &&
            IsWithinMonitoredRoot(normalizedPath);
        if (!valid)
        {
            throw new ArgumentException(
                "Collection request does not belong to the injected Claude Code source root.",
                nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string normalizedRoot = ClaudeCodeSourceIdentity.NormalizePath(
            instance.RootPath);
        bool valid = string.Equals(
                instance.AgentId,
                AgentId,
                StringComparison.Ordinal) &&
            instance.SourceKind == SourceKind.Jsonl &&
            string.Equals(
                instance.SourceInstanceId,
                _expectedInstanceId,
                StringComparison.Ordinal) &&
            string.Equals(
                normalizedRoot,
                _sourceRoot,
                StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new ArgumentException(
                "Claude Code source instance is invalid.",
                nameof(instance));
        }
    }

    private bool IsWithinMonitoredRoot(string normalizedPath) =>
        IsWithinRoot(normalizedPath, _monitoredRoot);

    private static bool IsWithinRoot(string normalizedPath, string rootPath)
    {
        string normalizedRoot = ClaudeCodeSourceIdentity.NormalizePath(rootPath);
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
            string normalizedPath = ClaudeCodeSourceIdentity.NormalizePath(
                sourcePath);
            if (!IsWithinRoot(normalizedPath, _monitoredRoot))
            {
                throw new SecurityException();
            }

            EnsureNotReparsePoint(_monitoredRoot);
            string current = _monitoredRoot;
            string relative = Path.GetRelativePath(_monitoredRoot, normalizedPath);
            foreach (string component in relative.Split(
                         [
                             Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar
                         ],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                EnsureNotReparsePoint(current);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or SecurityException
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
            throw new SecurityException();
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
            StringComparison.Ordinal) &&
        string.Equals(
            ClaudeCodeSourceIdentity.NormalizePath(cursor.SourcePath),
            ClaudeCodeSourceIdentity.NormalizePath(entity.SourcePath),
            StringComparison.OrdinalIgnoreCase);

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

    private static CollectorDiagnostic CursorSourceMismatchDiagnostic() => new(
        "collector.cursor_source_mismatch",
        "The stored cursor belongs to a different source and was reset.");

    private static CollectorDiagnostic CollectionLimitDiagnostic(
        string entityId) => new(
        "collector.batch_limit_reached",
        "A single collection reached 25 batches (at most 5000 lines) and will resume from its cursor.",
        entityId);
}
