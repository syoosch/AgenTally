using System.Runtime.CompilerServices;
using System.Security;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.QwenCode;

public sealed class QwenCodeCollector : IIncrementalFileCollector
{
    private const int MaxBatchLines = 200;
    private const int MaxCollectionBatches = 25;
    private readonly string _home;
    private readonly TimeProvider _timeProvider;
    private readonly QwenCodeSourceResolver _resolver;
    private readonly IncrementalJsonlReader _reader;
    private readonly QwenCodeJsonlParser _parser;

    public QwenCodeCollector(
        string qwenHome,
        TimeProvider? timeProvider = null,
        QwenCodeSourceResolver? resolver = null,
        IncrementalJsonlReader? reader = null,
        QwenCodeJsonlParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qwenHome);
        _home = QwenCodeSourceIdentity.NormalizePath(qwenHome);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new QwenCodeSourceResolver();
        _reader = reader ?? new IncrementalJsonlReader();
        _parser = parser ?? new QwenCodeJsonlParser();
    }

    public string AgentId => "qwen-code";

    public string ParserVersion => QwenCodeJsonlParser.CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_home, cancellationToken);
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

        CollectorDiagnostic? cursorDiagnostic = null;
        QwenCodeCursor cursor;
        if (request.Cursor is not null &&
            (!string.Equals(request.Cursor.SourceInstanceId, request.Instance.SourceInstanceId,
                 StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceEntityId, request.Entity.SourceEntityId,
                 StringComparison.Ordinal)))
        {
            cursor = QwenCodeCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "collector.cursor_source_mismatch",
                "The stored cursor did not belong to the Qwen Code chat and was reset.",
                request.Entity.SourceEntityId);
        }
        else
        {
            cursor = QwenCodeCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                request.Cursor is not null,
                out cursorDiagnostic);
        }

        string expectedSessionId = Path.GetFileNameWithoutExtension(request.Entity.SourcePath);
        if (!ValidIdentity(expectedSessionId))
        {
            throw new InvalidDataException("A Qwen Code chat file had an invalid identity.");
        }

        int batches = 0;
        while (true)
        {
            EnsureNoReparsePoints(request.Entity.SourcePath);
            JsonlReadBatch read = await _reader.ReadBatchAsync(
                request.Entity.SourcePath,
                cursor.Jsonl,
                MaxBatchLines,
                cancellationToken);
            EnsureNoReparsePoints(request.Entity.SourcePath);
            if (string.IsNullOrWhiteSpace(read.NextCursor.SourceFingerprint))
            {
                yield break;
            }

            QwenCodeParseState state = read.Diagnostic?.Code == "jsonl.source_reset"
                ? new QwenCodeParseState()
                : cursor.State;
            var events = new List<UsageEvent>();
            var sessions = new List<UsageSessionMetadata>();
            var turns = new List<UsageTurnMetadata>();
            var tools = new List<UsageEventToolMetadata>();
            var diagnostics = new List<CollectorDiagnostic>();
            if (cursorDiagnostic is not null)
            {
                diagnostics.Add(cursorDiagnostic with
                {
                    SourceEntityId = request.Entity.SourceEntityId
                });
                cursorDiagnostic = null;
            }
            if (read.Diagnostic is not null)
            {
                diagnostics.Add(read.Diagnostic with
                {
                    SourceEntityId = request.Entity.SourceEntityId
                });
            }

            var context = new QwenCodeEventContext(
                request.Instance,
                request.Entity,
                read.NextCursor.SourceFingerprint,
                _timeProvider.GetUtcNow(),
                expectedSessionId);
            foreach (JsonlLine line in read.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QwenCodeParseResult result = _parser.ParseLine(line, state, context);
                state = result.State;
                if (result.Event is not null)
                {
                    events.Add(result.Event);
                }
                if (result.Session is not null)
                {
                    sessions.Add(result.Session);
                }
                if (result.Turn is not null)
                {
                    turns.Add(result.Turn);
                }
                tools.AddRange(result.EventTools);
                if (result.Diagnostic is not null)
                {
                    diagnostics.Add(result.Diagnostic);
                }
            }

            cursor = new QwenCodeCursor(read.NextCursor, state);
            batches++;
            bool limited = batches >= MaxCollectionBatches && !read.EndOfFile;
            if (limited)
            {
                diagnostics.Add(new CollectorDiagnostic(
                    "collector.batch_limit_reached",
                    "The Qwen Code collection reached its bounded batch limit.",
                    request.Entity.SourceEntityId));
            }
            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                events,
                cursor.Serialize(),
                read.NextCursor.SourceFingerprint,
                ParserVersion,
                diagnostics)
            {
                Sessions = sessions,
                Turns = turns,
                EventTools = tools
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

    public string NormalizeSourcePath(string path) =>
        QwenCodeSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        QwenCodeSourceIdentity.EntityId(normalizedChangePath);

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath)
    {
        ValidateInstance(instance);
        return HasExpectedShape(normalizedChangePath);
    }

    public bool TryGetCursorByteOffset(StoredCursor cursor, out long byteOffset)
    {
        QwenCodeCursor parsed = QwenCodeCursor.DeserializeOrStart(
            cursor.CursorJson,
            true,
            out CollectorDiagnostic? diagnostic);
        byteOffset = parsed.Jsonl.ByteOffset;
        return diagnostic is null;
    }

    private void ValidateRequest(CollectionRequest request)
    {
        ValidateInstance(request.Instance);
        string path = QwenCodeSourceIdentity.NormalizePath(request.Entity.SourcePath);
        if (!string.Equals(path, request.Entity.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Entity.SourceInstanceId, request.Instance.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(request.Entity.SourceEntityId,
                QwenCodeSourceIdentity.EntityId(path), StringComparison.Ordinal) ||
            !HasExpectedShape(path))
        {
            throw new ArgumentException("The Qwen Code collection request was invalid.", nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (instance is null ||
            !string.Equals(instance.SourceInstanceId,
                QwenCodeSourceIdentity.InstanceId(_home), StringComparison.Ordinal) ||
            !string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            instance.SourceKind != SourceKind.Jsonl ||
            !string.Equals(QwenCodeSourceIdentity.NormalizePath(instance.RootPath), _home,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Qwen Code source instance was invalid.", nameof(instance));
        }
    }

    private bool HasExpectedShape(string path)
    {
        string projects = Path.Combine(_home, "projects");
        string relative = Path.GetRelativePath(projects, QwenCodeSourceIdentity.NormalizePath(path));
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
            string.Equals(segments[1], "chats", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(segments[2]), ".jsonl", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(relative) &&
            !relative.StartsWith("..", StringComparison.Ordinal);
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
                    throw new InvalidDataException("Qwen Code source path safety validation failed.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or SecurityException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException(
                    "Qwen Code source path safety validation failed.", exception);
            }
        }
    }

    private static bool ValidIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
}
