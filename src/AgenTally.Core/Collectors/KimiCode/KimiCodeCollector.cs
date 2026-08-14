using System.Runtime.CompilerServices;
using System.Security;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed class KimiCodeCollector :
    IIncrementalFileCollector,
    IUsageSessionNameSource
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private const string EmptyContentFingerprint =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string UnsafePathMessage =
        "Kimi Code source path safety validation failed.";

    private readonly string _kimiHome;
    private readonly KimiCodeSourceLayout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly KimiCodeSourceResolver _resolver;
    private readonly IncrementalJsonlReader _reader;
    private readonly KimiCodeWireParser _parser;
    private readonly KimiCodeEntityMetadataReader _metadataReader;

    public KimiCodeCollector(
        string kimiHome,
        TimeProvider? timeProvider = null,
        KimiCodeSourceResolver? resolver = null,
        IncrementalJsonlReader? reader = null,
        KimiCodeWireParser? parser = null,
        KimiCodeEntityMetadataReader? metadataReader = null)
        : this(
            kimiHome,
            KimiCodeSourceLayout.Cli,
            timeProvider,
            resolver,
            reader,
            parser,
            metadataReader)
    {
    }

    internal KimiCodeCollector(
        string kimiHome,
        KimiCodeSourceLayout layout,
        TimeProvider? timeProvider = null)
        : this(
            kimiHome,
            layout,
            timeProvider,
            resolver: null,
            reader: null,
            parser: null,
            metadataReader: null)
    {
    }

    private KimiCodeCollector(
        string kimiHome,
        KimiCodeSourceLayout layout,
        TimeProvider? timeProvider,
        KimiCodeSourceResolver? resolver,
        IncrementalJsonlReader? reader,
        KimiCodeWireParser? parser,
        KimiCodeEntityMetadataReader? metadataReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kimiHome);
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _kimiHome = KimiCodeSourceIdentity.NormalizePath(kimiHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new KimiCodeSourceResolver(_layout);
        _reader = reader ?? new IncrementalJsonlReader();
        _parser = parser ?? new KimiCodeWireParser();
        _metadataReader = metadataReader ?? new KimiCodeEntityMetadataReader(_layout);
    }

    public string AgentId => _layout.AgentId;

    public string ParserVersion => KimiCodeWireParser.CurrentParserVersion;

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_kimiHome, cancellationToken);
    }

    public async Task<IReadOnlyList<UsageSessionNameMetadata>>
        ReadSessionNamesAsync(CancellationToken cancellationToken)
    {
        SourceProbeResult probe = await _resolver.ProbeAsync(
            _kimiHome,
            cancellationToken);
        if (probe.Diagnostics.Count > 0)
        {
            return [];
        }

        var names = new Dictionary<string, UsageSessionNameMetadata>(
            StringComparer.Ordinal);
        foreach (SourceEntityDescriptor entity in probe.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KimiCodeEntityMetadataResult result = await _metadataReader.ReadAsync(
                _kimiHome,
                entity.SourcePath,
                entity.SourceEntityId,
                cancellationToken);
            KimiCodeEntityMetadata? metadata = result.Metadata;
            if (metadata?.SessionRole is not SessionRole.Main ||
                !metadata.SessionNameUpdatedAtUtc.HasValue)
            {
                continue;
            }

            var candidate = new UsageSessionNameMetadata(
                metadata.SessionId,
                metadata.SessionName,
                metadata.SessionNameUpdatedAtUtc.Value);
            if (!names.TryGetValue(candidate.SessionId, out var current) ||
                candidate.UpdatedAtUtc > current.UpdatedAtUtc)
            {
                names[candidate.SessionId] = candidate;
            }
        }

        return names.Values
            .OrderBy(value => value.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        KimiCodeCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null &&
            !CursorBelongsToEntity(request.Cursor, request.Entity))
        {
            cursor = KimiCodeCursor.Start;
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

            cursor = KimiCodeCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                hasStoredCursor: request.Cursor is not null,
                out cursorDiagnostic);
        }

        EnsureNoReparsePoints(request.Entity.SourcePath);
        KimiCodeEntityMetadataResult metadataResult =
            await _metadataReader.ReadAsync(
                _kimiHome,
                request.Entity.SourcePath,
                request.Entity.SourceEntityId,
                cancellationToken);
        KimiCodeEntityMetadata metadata = metadataResult.Metadata ??
            throw new InvalidDataException(
                metadataResult.Diagnostic?.Message ??
                "Kimi Code session metadata could not be confirmed.");

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
                    AddDiagnostic(
                        resetDiagnostics,
                        metadataResult.Diagnostic,
                        request.Entity.SourceEntityId);
                    string resetFingerprint = ValidFingerprint(
                        request.Cursor!.SourceFingerprint)
                            ? request.Cursor.SourceFingerprint
                            : EmptyContentFingerprint;
                    yield return new CollectedBatch(
                        request.Instance,
                        request.Entity,
                        [],
                        KimiCodeCursor.Start.Serialize(),
                        resetFingerprint,
                        ParserVersion,
                        resetDiagnostics);
                }

                yield break;
            }

            KimiCodeParseState state =
                readBatch.Diagnostic?.Code is "jsonl.source_reset"
                    ? new KimiCodeParseState()
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

            var context = new KimiCodeEventContext(
                request.Instance,
                request.Entity,
                readBatch.NextCursor.SourceFingerprint,
                _timeProvider.GetUtcNow(),
                metadata);
            foreach (JsonlLine line in readBatch.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KimiCodeParseResult result = _parser.ParseLine(
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

            cursor = new KimiCodeCursor(readBatch.NextCursor, state);
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
        return [Path.Combine(_kimiHome, "sessions")];
    }

    string ISourceFileChangeCollector.NormalizeSourcePath(string path) =>
        KimiCodeSourceIdentity.NormalizePath(path);

    string ISourceFileChangeCollector.GetSourceEntityId(string normalizedPath) =>
        KimiCodeSourceIdentity.EntityId(normalizedPath);

    bool ISourceFileChangeCollector.IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath)
    {
        ValidateInstance(instance);
        return IsWithinSessionsRoot(normalizedPath) &&
            string.Equals(
                Path.GetFileName(normalizedPath),
                "wire.jsonl",
                StringComparison.OrdinalIgnoreCase);
    }

    bool IIncrementalFileCollector.TryGetCursorByteOffset(
        StoredCursor cursor,
        out long byteOffset)
    {
        KimiCodeCursor parsed = KimiCodeCursor.DeserializeOrStart(
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
            normalizedPath = KimiCodeSourceIdentity.NormalizePath(
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
                KimiCodeSourceIdentity.EntityId(normalizedPath),
                StringComparison.Ordinal) &&
            string.Equals(
                Path.GetFileName(normalizedPath),
                "wire.jsonl",
                StringComparison.OrdinalIgnoreCase) &&
            IsWithinSessionsRoot(normalizedPath);
        if (!valid)
        {
            throw new ArgumentException(
                "Collection request does not belong to the injected Kimi Code home.",
                nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        string normalizedRoot = KimiCodeSourceIdentity.NormalizePath(
            instance.RootPath);
        bool valid = string.Equals(
                instance.AgentId,
                AgentId,
                StringComparison.Ordinal) &&
            instance.SourceKind == SourceKind.Jsonl &&
            string.Equals(
                instance.SourceInstanceId,
                _layout.InstanceId(_kimiHome),
                StringComparison.Ordinal) &&
            string.Equals(
                normalizedRoot,
                _kimiHome,
                StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new ArgumentException(
                "Kimi Code source instance is invalid.",
                nameof(instance));
        }
    }

    private bool IsWithinSessionsRoot(string normalizedPath) =>
        IsWithinRoot(normalizedPath, Path.Combine(_kimiHome, "sessions"));

    private static bool IsWithinRoot(string normalizedPath, string rootPath)
    {
        string normalizedRoot = KimiCodeSourceIdentity.NormalizePath(rootPath);
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
            string normalizedPath = KimiCodeSourceIdentity.NormalizePath(
                sourcePath);
            string sessionsRoot = KimiCodeSourceIdentity.NormalizePath(
                Path.Combine(_kimiHome, "sessions"));
            if (!IsWithinRoot(normalizedPath, sessionsRoot))
            {
                throw new SecurityException();
            }

            EnsureNotReparsePoint(sessionsRoot);
            string current = sessionsRoot;
            string relative = Path.GetRelativePath(sessionsRoot, normalizedPath);
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
            KimiCodeSourceIdentity.NormalizePath(cursor.SourcePath),
            KimiCodeSourceIdentity.NormalizePath(entity.SourcePath),
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
