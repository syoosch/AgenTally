using System.Globalization;
using System.Runtime.CompilerServices;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.OpenCode;

// The SQLite v1/v2 and legacy JSON layouts are cross-checked against pinned
// ccusage 20.0.19 and tokscale 4.8.1. Source-reported cost is intentionally not
// imported; AgenTally's independent official-API price ledger remains authoritative.
public sealed class OpenCodeCollector : ISourceFileChangeCollector
{
    public const string CurrentParserVersion = "opencode-upstream-v1";
    private const int MaxRowsPerBatch = 200;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly OpenCodeSourceResolver _resolver;

    public OpenCodeCollector(string root, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = OpenCodeSourceIdentity.NormalizePath(root);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = new OpenCodeSourceResolver();
    }

    public string AgentId => "opencode";

    public string ParserVersion => CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public string? MaintenanceCompatibilityCode =>
        "opencode_upstream_derived_contract";

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

        string entityPath = OpenCodeSourceIdentity.CanonicalEntityPath(
            request.Entity.SourcePath);
        bool database = OpenCodeSourceIdentity.IsDatabase(entityPath);
        string sourceFingerprint = OpenCodeSourceIdentity.SourceFingerprint(entityPath);
        SnapshotSourceCursor cursor;
        CollectorDiagnostic? cursorDiagnostic = null;
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
                "The stored cursor did not belong to this OpenCode source and was reset.",
                request.Entity.SourceEntityId);
        }
        else
        {
            cursor = SnapshotSourceCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                request.Cursor is not null,
                "opencode.invalid_cursor",
                out cursorDiagnostic);
        }
        if (cursor.AfterKey.Length > 0 &&
            (!int.TryParse(cursor.AfterKey, NumberStyles.None,
                 CultureInfo.InvariantCulture, out int parsedOffset) ||
             parsedOffset < 0))
        {
            cursor = SnapshotSourceCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "opencode.invalid_cursor",
                "The OpenCode collection cursor was invalid and has been reset.",
                request.Entity.SourceEntityId);
        }

        string stampBefore = SnapshotSourceCursor.ComputeSourceChangeStamp(
            entityPath,
            includeSqliteSidecars: database);
        if (cursor.ScanSourceStamp.Length > 0 &&
            !string.Equals(cursor.ScanSourceStamp, stampBefore, StringComparison.Ordinal))
        {
            cursor = cursor.RestartAfterChange();
            cursorDiagnostic = new CollectorDiagnostic(
                "opencode.source_changed_during_scan",
                "The OpenCode source changed between bounded scan batches and was restarted.",
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

        int offset = cursor.AfterKey.Length == 0
            ? 0
            : int.Parse(cursor.AfterKey, CultureInfo.InvariantCulture);
        OpenCodeParsePage parsed = await OpenCodeParser.ParseAsync(
            entityPath,
            offset,
            MaxRowsPerBatch,
            cancellationToken);
        DateTimeOffset importedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var events = new List<UsageEvent>(parsed.Records.Count);
        var sessions = new Dictionary<string, UsageSessionMetadata>(StringComparer.Ordinal);
        foreach (OpenCodeParsedRecord record in parsed.Records)
        {
            string dedupKey = OpenCodeSourceIdentity.HashIdentity(
                "opencode-message",
                $"{record.SessionId}\0{record.StableMessageId}");
            CodexProjectIdentity? project = record.WorkspaceRoot is not null &&
                CodexProjectIdentity.TryCreate(
                    record.WorkspaceRoot,
                    out CodexProjectIdentity mappedProject)
                    ? mappedProject
                    : null;
            ModelResolutionOrigin modelOrigin = record.Provider is null
                ? ModelResolutionOrigin.LogConfirmed
                : ModelResolutionOrigin.ProviderModelPair;
            var usageEvent = new UsageEvent(
                AgentId,
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                $"opencode-call:{dedupKey[..32]}",
                dedupKey,
                SourceKind.Mixed,
                record.OccurredAtUtc,
                importedAtUtc,
                new ModelIdentity
                {
                    RawModel = record.Model,
                    NormalizedModel = ModelIdentityCanonicalizer.Canonicalize(record.Model, AgentId),
                    ProviderId = record.Provider,
                    ResolutionOrigin = modelOrigin
                },
                record.Tokens,
                CompletionState.Finalized,
                record.DataQuality,
                ParserVersion,
                sourceFingerprint,
                cursor.ScanRevision)
            {
                SessionId = record.SessionId,
                ProjectId = project?.ProjectId,
                ProjectPath = project?.ProjectPath,
                ProjectRepositoryIdentityHash = project?.RepositoryIdentityHash
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
                ProjectId = project?.ProjectId,
                ProjectPath = project?.ProjectPath,
                ProjectRepositoryIdentityHash = project?.RepositoryIdentityHash,
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
            entityPath,
            includeSqliteSidecars: database);
        SnapshotSourceCursor next;
        if (!string.Equals(stampBefore, stampAfter, StringComparison.Ordinal))
        {
            next = cursor.RestartAfterChange();
            diagnostics.Add(new CollectorDiagnostic(
                "opencode.source_changed_during_scan",
                "The OpenCode source changed while it was read and will be scanned again.",
                request.Entity.SourceEntityId));
        }
        else if (parsed.HasMore)
        {
            int nextOffset = checked(offset + parsed.RawRowsConsumed);
            next = cursor.ContinueAfter(nextOffset.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add(new CollectorDiagnostic(
                "collector.batch_limit_reached",
                "The OpenCode collection reached its bounded row limit.",
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
        return [_root];
    }

    public string NormalizeSourcePath(string path) =>
        OpenCodeSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        OpenCodeSourceIdentity.EntityId(
            OpenCodeSourceIdentity.CanonicalEntityPath(normalizedChangePath));

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath)
    {
        ValidateInstance(instance);
        string canonical = OpenCodeSourceIdentity.CanonicalEntityPath(normalizedChangePath);
        string relative = Path.GetRelativePath(_root, canonical);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }
        if (OpenCodeSourceIdentity.IsDatabase(canonical))
        {
            return string.Equals(Path.GetDirectoryName(canonical), _root,
                StringComparison.OrdinalIgnoreCase);
        }
        string legacyRoot = Path.Combine(_root, "storage", "message");
        string legacyRelative = Path.GetRelativePath(legacyRoot, canonical);
        return string.Equals(Path.GetExtension(canonical), ".json",
                   StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(legacyRelative) && legacyRelative != ".." &&
            !legacyRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public bool IsRelevantChangePath(string normalizedChangePath) =>
        OpenCodeSourceIdentity.IsDatabaseChangePath(normalizedChangePath) ||
        string.Equals(Path.GetExtension(normalizedChangePath), ".json",
            StringComparison.OrdinalIgnoreCase);

    public bool HasSourceChanged(SourceEntityDescriptor entity, StoredCursor storedCursor)
    {
        string path = OpenCodeSourceIdentity.CanonicalEntityPath(entity.SourcePath);
        SnapshotSourceCursor cursor = SnapshotSourceCursor.DeserializeOrStart(
            storedCursor.CursorJson,
            hasStoredCursor: true,
            "opencode.invalid_cursor",
            out CollectorDiagnostic? diagnostic);
        return diagnostic is not null || cursor.ScanSourceStamp.Length > 0 ||
            !string.Equals(
                cursor.CompletedSourceStamp,
                SnapshotSourceCursor.ComputeSourceChangeStamp(
                    path,
                    includeSqliteSidecars: OpenCodeSourceIdentity.IsDatabase(path)),
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
        string canonical = OpenCodeSourceIdentity.CanonicalEntityPath(request.Entity.SourcePath);
        if (!string.Equals(request.Entity.SourceInstanceId,
                request.Instance.SourceInstanceId, StringComparison.Ordinal) ||
            !string.Equals(request.Entity.SourceEntityId,
                OpenCodeSourceIdentity.EntityId(canonical), StringComparison.Ordinal) ||
            !IsWithinMonitoredRoots(request.Instance, canonical) ||
            !File.Exists(canonical))
        {
            throw new InvalidOperationException(
                "The OpenCode collection request does not match the configured source.");
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (!string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            !string.Equals(instance.SourceInstanceId,
                OpenCodeSourceIdentity.InstanceId(_root), StringComparison.Ordinal) ||
            instance.SourceKind != SourceKind.Mixed ||
            !string.Equals(instance.RootPath, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The OpenCode source instance does not match the collector.");
        }
    }
}
