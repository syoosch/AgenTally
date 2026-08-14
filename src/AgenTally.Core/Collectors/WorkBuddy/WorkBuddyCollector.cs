using System.Runtime.CompilerServices;
using System.Security;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.WorkBuddy;

public sealed class WorkBuddyCollector : IIncrementalFileCollector
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private const string EmptyContentFingerprint =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string UnsafePathMessage =
        "WorkBuddy source path safety validation failed.";

    private readonly string _workBuddyHome;
    private readonly TimeProvider _timeProvider;
    private readonly WorkBuddySourceResolver _resolver;
    private readonly IncrementalJsonlReader _reader;
    private readonly WorkBuddyJsonlParser _parser;

    public WorkBuddyCollector(
        string workBuddyHome,
        TimeProvider? timeProvider = null,
        WorkBuddySourceResolver? resolver = null,
        IncrementalJsonlReader? reader = null,
        WorkBuddyJsonlParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workBuddyHome);
        _workBuddyHome = WorkBuddySourceIdentity.NormalizePath(workBuddyHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new WorkBuddySourceResolver();
        _reader = reader ?? new IncrementalJsonlReader();
        _parser = parser ?? new WorkBuddyJsonlParser();
    }

    public string AgentId => "workbuddy";

    public string ParserVersion => WorkBuddyJsonlParser.CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_workBuddyHome, cancellationToken);
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        WorkBuddyCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null &&
            !CursorBelongsToEntity(request.Cursor, request.Entity))
        {
            cursor = WorkBuddyCursor.Start;
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

            cursor = WorkBuddyCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                hasStoredCursor: request.Cursor is not null,
                out cursorDiagnostic);
        }

        EnsureNoReparsePoints(request.Entity.SourcePath);
        string expectedSessionId = Path.GetFileNameWithoutExtension(
            request.Entity.SourcePath);
        if (string.IsNullOrWhiteSpace(expectedSessionId) ||
            expectedSessionId.Length > 1024 ||
            expectedSessionId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "A WorkBuddy session file had an invalid identity.");
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
                        WorkBuddyCursor.Start.Serialize(),
                        resetFingerprint,
                        ParserVersion,
                        resetDiagnostics);
                }

                yield break;
            }

            WorkBuddyParseState state =
                readBatch.Diagnostic?.Code is "jsonl.source_reset"
                    ? new WorkBuddyParseState()
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

            var context = new WorkBuddyEventContext(
                request.Instance,
                request.Entity,
                readBatch.NextCursor.SourceFingerprint,
                _timeProvider.GetUtcNow(),
                expectedSessionId);
            foreach (JsonlLine line in readBatch.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkBuddyParseResult result = _parser.ParseLine(
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

            cursor = new WorkBuddyCursor(readBatch.NextCursor, state);
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
        return [Path.Combine(_workBuddyHome, "projects")];
    }

    string ISourceFileChangeCollector.NormalizeSourcePath(string path) =>
        WorkBuddySourceIdentity.NormalizePath(path);

    string ISourceFileChangeCollector.GetSourceEntityId(string normalizedPath) =>
        WorkBuddySourceIdentity.EntityId(normalizedPath);

    bool ISourceFileChangeCollector.IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath)
    {
        ValidateInstance(instance);
        return IsWithinProjectsRoot(normalizedPath) &&
            string.Equals(
                Path.GetExtension(normalizedPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase);
    }

    bool IIncrementalFileCollector.TryGetCursorByteOffset(
        StoredCursor cursor,
        out long byteOffset)
    {
        WorkBuddyCursor parsed = WorkBuddyCursor.DeserializeOrStart(
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
            normalizedPath = WorkBuddySourceIdentity.NormalizePath(
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
                WorkBuddySourceIdentity.EntityId(normalizedPath),
                StringComparison.Ordinal) &&
            string.Equals(
                Path.GetExtension(normalizedPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase) &&
            IsWithinProjectsRoot(normalizedPath);
        if (!valid)
        {
            throw new ArgumentException(
                "Collection request does not belong to the injected WorkBuddy home.",
                nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string normalizedRoot = WorkBuddySourceIdentity.NormalizePath(
            instance.RootPath);
        bool valid = string.Equals(
                instance.AgentId,
                AgentId,
                StringComparison.Ordinal) &&
            instance.SourceKind == SourceKind.Jsonl &&
            string.Equals(
                instance.SourceInstanceId,
                WorkBuddySourceIdentity.InstanceId(_workBuddyHome),
                StringComparison.Ordinal) &&
            string.Equals(
                normalizedRoot,
                _workBuddyHome,
                StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new ArgumentException(
                "WorkBuddy source instance is invalid.",
                nameof(instance));
        }
    }

    private bool IsWithinProjectsRoot(string normalizedPath) =>
        IsWithinRoot(normalizedPath, Path.Combine(_workBuddyHome, "projects"));

    private static bool IsWithinRoot(string normalizedPath, string rootPath)
    {
        string normalizedRoot = WorkBuddySourceIdentity.NormalizePath(rootPath);
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
            string normalizedPath = WorkBuddySourceIdentity.NormalizePath(
                sourcePath);
            string projectsRoot = WorkBuddySourceIdentity.NormalizePath(
                Path.Combine(_workBuddyHome, "projects"));
            if (!IsWithinRoot(normalizedPath, projectsRoot))
            {
                throw new SecurityException();
            }

            EnsureNotReparsePoint(projectsRoot);
            string current = projectsRoot;
            string relative = Path.GetRelativePath(projectsRoot, normalizedPath);
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
            WorkBuddySourceIdentity.NormalizePath(cursor.SourcePath),
            WorkBuddySourceIdentity.NormalizePath(entity.SourcePath),
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
