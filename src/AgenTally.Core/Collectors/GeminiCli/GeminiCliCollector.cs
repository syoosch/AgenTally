using System.Runtime.CompilerServices;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.GeminiCli;

// The file layouts are cross-checked against pinned ccusage 20.0.19 and
// tokscale 4.8.1. No upstream project is invoked or embedded at runtime.
public sealed class GeminiCliCollector : ISourceFileChangeCollector
{
    public const string CurrentParserVersion = "gemini-cli-upstream-v1";
    private const int MaxRecordsPerBatch = 200;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly GeminiCliSourceResolver _resolver;

    public GeminiCliCollector(string root, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = GeminiCliSourceIdentity.NormalizePath(root);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = new GeminiCliSourceResolver();
    }

    public string AgentId => "gemini-cli";

    public string ParserVersion => CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public string? MaintenanceCompatibilityCode =>
        "gemini_cli_upstream_derived_contract";

    public string WatchFilter => "*";

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_root, cancellationToken);
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

        string sourceFingerprint = GeminiCliSourceIdentity.SourceFingerprint(
            request.Entity.SourcePath);
        CollectorDiagnostic? cursorDiagnostic = null;
        SnapshotSourceCursor cursor;
        if (request.Cursor is not null &&
            (!string.Equals(request.Cursor.SourceInstanceId,
                 request.Instance.SourceInstanceId, StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceEntityId,
                 request.Entity.SourceEntityId, StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceFingerprint,
                 sourceFingerprint, StringComparison.Ordinal)))
        {
            cursor = SnapshotSourceCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "collector.cursor_source_mismatch",
                "The stored cursor did not belong to this Gemini CLI transcript and was reset.",
                request.Entity.SourceEntityId);
        }
        else
        {
            cursor = SnapshotSourceCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                request.Cursor is not null,
                "gemini-cli.invalid_cursor",
                out cursorDiagnostic);
        }

        string stampBefore = SnapshotSourceCursor.ComputeSourceChangeStamp(
            request.Entity.SourcePath,
            includeSqliteSidecars: false);
        if (cursor.ScanSourceStamp.Length > 0 &&
            !string.Equals(cursor.ScanSourceStamp, stampBefore, StringComparison.Ordinal))
        {
            cursor = cursor.RestartAfterChange();
            cursorDiagnostic = new CollectorDiagnostic(
                "gemini-cli.source_changed_during_scan",
                "The Gemini CLI transcript changed between bounded scan batches and was restarted.",
                request.Entity.SourceEntityId);
        }
        if (cursor.ScanSourceStamp.Length == 0)
        {
            if (request.Cursor is not null &&
                string.Equals(cursor.CompletedSourceStamp, stampBefore, StringComparison.Ordinal))
            {
                yield return EmptyBatch(request, cursor, sourceFingerprint, cursorDiagnostic);
                yield break;
            }
            cursor = cursor.BeginScan(stampBefore);
        }

        GeminiCliParseResult parsed = await GeminiCliParser.ParseAsync(
            request.Entity.SourcePath,
            cancellationToken);
        GeminiCliRecord[] page = parsed.Records
            .Where(record => string.CompareOrdinal(record.SourceKey, cursor.AfterKey) > 0)
            .Take(MaxRecordsPerBatch + 1)
            .ToArray();
        bool hasMore = page.Length > MaxRecordsPerBatch;
        if (hasMore)
        {
            page = page[..MaxRecordsPerBatch];
        }

        DateTimeOffset importedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var events = new List<UsageEvent>(page.Length);
        var sessions = new Dictionary<string, UsageSessionMetadata>(StringComparer.Ordinal);
        foreach (GeminiCliRecord record in page)
        {
            string dedupKey = GeminiCliSourceIdentity.HashIdentity(
                "gemini-cli-call",
                $"{record.SessionId}\0{record.StableId}");
            string? projectId = record.ProjectHash is null
                ? null
                : GeminiCliSourceIdentity.HashIdentity(
                    "gemini-cli-project",
                    record.ProjectHash)[..24];
            var usageEvent = new UsageEvent(
                AgentId,
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                $"gemini-cli-call:{dedupKey[..32]}",
                dedupKey,
                SourceKind.Mixed,
                record.OccurredAtUtc,
                importedAtUtc,
                new ModelIdentity
                {
                    RawModel = record.Model,
                    NormalizedModel = ModelIdentityCanonicalizer.Canonicalize(record.Model, AgentId),
                    ProviderId = "google",
                    ResolutionOrigin = ModelResolutionOrigin.ProviderModelPair
                },
                record.Tokens,
                CompletionState.Finalized,
                DataQuality.Exact,
                ParserVersion,
                sourceFingerprint,
                cursor.ScanRevision)
            {
                SessionId = record.SessionId,
                ProjectId = projectId
            };
            events.Add(usageEvent);
            sessions[record.SessionId] = new UsageSessionMetadata(
                AgentId,
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                record.SessionId,
                SessionKind.Primary,
                null,
                null,
                SessionRelationOrigin.None,
                SessionRelationState.None,
                ReplayState.Active,
                CompatibilityLevel.PartiallyCompatible,
                record.OccurredAtUtc,
                ParserVersion)
            {
                ProjectId = projectId,
                SessionRole = SessionRole.Main
            };
        }

        var diagnostics = new List<CollectorDiagnostic>();
        if (cursorDiagnostic is not null)
        {
            diagnostics.Add(cursorDiagnostic with { SourceEntityId = request.Entity.SourceEntityId });
        }
        diagnostics.AddRange(parsed.Diagnostics.Select(value =>
            value with { SourceEntityId = request.Entity.SourceEntityId }));

        string stampAfter = SnapshotSourceCursor.ComputeSourceChangeStamp(
            request.Entity.SourcePath,
            includeSqliteSidecars: false);
        SnapshotSourceCursor next;
        if (!string.Equals(stampBefore, stampAfter, StringComparison.Ordinal))
        {
            next = cursor.RestartAfterChange();
            diagnostics.Add(new CollectorDiagnostic(
                "gemini-cli.source_changed_during_scan",
                "The Gemini CLI transcript changed while it was read and will be scanned again.",
                request.Entity.SourceEntityId));
        }
        else if (hasMore)
        {
            next = cursor.ContinueAfter(page[^1].SourceKey);
            diagnostics.Add(new CollectorDiagnostic(
                "collector.batch_limit_reached",
                "The Gemini CLI collection reached its bounded record limit.",
                request.Entity.SourceEntityId));
        }
        else
        {
            next = cursor.Complete(stampAfter);
        }

        yield return new CollectedBatch(
            request.Instance,
            request.Entity,
            events,
            next.Serialize(),
            sourceFingerprint,
            ParserVersion,
            diagnostics)
        {
            Sessions = sessions.Values.ToArray()
        };
    }

    public IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [Path.Combine(_root, "tmp")];
    }

    public string NormalizeSourcePath(string path) =>
        GeminiCliSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        GeminiCliSourceIdentity.EntityId(normalizedChangePath);

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath)
    {
        ValidateInstance(instance);
        if (!GeminiCliSourceIdentity.IsSupportedFile(normalizedChangePath))
        {
            return false;
        }
        string relative = Path.GetRelativePath(
            Path.Combine(_root, "tmp"),
            GeminiCliSourceIdentity.NormalizePath(normalizedChangePath));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public bool IsRelevantChangePath(string normalizedChangePath) =>
        GeminiCliSourceIdentity.IsSupportedFile(normalizedChangePath);

    public bool HasSourceChanged(SourceEntityDescriptor entity, StoredCursor storedCursor)
    {
        SnapshotSourceCursor cursor = SnapshotSourceCursor.DeserializeOrStart(
            storedCursor.CursorJson,
            hasStoredCursor: true,
            "gemini-cli.invalid_cursor",
            out CollectorDiagnostic? diagnostic);
        return diagnostic is not null || cursor.ScanSourceStamp.Length > 0 ||
            !string.Equals(
                cursor.CompletedSourceStamp,
                SnapshotSourceCursor.ComputeSourceChangeStamp(
                    entity.SourcePath,
                    includeSqliteSidecars: false),
                StringComparison.Ordinal);
    }

    private CollectedBatch EmptyBatch(
        CollectionRequest request,
        SnapshotSourceCursor cursor,
        string sourceFingerprint,
        CollectorDiagnostic? diagnostic) => new(
        request.Instance,
        request.Entity,
        [],
        cursor.Serialize(),
        sourceFingerprint,
        ParserVersion,
        diagnostic is null ? [] : [diagnostic]);

    private void ValidateRequest(CollectionRequest request)
    {
        ValidateInstance(request.Instance);
        string normalized = GeminiCliSourceIdentity.NormalizePath(request.Entity.SourcePath);
        if (!string.Equals(request.Entity.SourceInstanceId,
                request.Instance.SourceInstanceId, StringComparison.Ordinal) ||
            !string.Equals(request.Entity.SourceEntityId,
                GeminiCliSourceIdentity.EntityId(normalized), StringComparison.Ordinal) ||
            !IsWithinMonitoredRoots(request.Instance, normalized) ||
            !File.Exists(normalized))
        {
            throw new InvalidOperationException(
                "The Gemini CLI collection request does not match the configured source.");
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (!string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            !string.Equals(instance.SourceInstanceId,
                GeminiCliSourceIdentity.InstanceId(_root), StringComparison.Ordinal) ||
            instance.SourceKind != SourceKind.Mixed ||
            !string.Equals(instance.RootPath, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Gemini CLI source instance does not match the collector.");
        }
    }
}
