using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CodexRolloutParserTests
{
    [TestMethod]
    public void ParseLine_ConvertsCumulativeSnapshotsToStableDeltas()
    {
        IReadOnlyList<CodexParseResult> results = ParseFixture("basic-rollout.jsonl");
        UsageEvent[] events = results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!)
            .ToArray();

        Assert.HasCount(2, events);
        UsageEvent second = events[1];
        Assert.AreEqual("thread-1", second.SessionId);
        Assert.AreEqual("gpt-test", second.Model.NormalizedModel);
        Assert.AreEqual("openai/gpt-test-2026-07-01", second.Model.RawModel);
        Assert.AreEqual(30L, second.Tokens.InputReported.Value);
        Assert.AreEqual(20L, second.Tokens.UncachedInput.Value);
        Assert.AreEqual(10L, second.Tokens.CacheRead.Value);
        Assert.AreEqual(8L, second.Tokens.Output.Value);
        Assert.AreEqual(2L, second.Tokens.Reasoning.Value);
        Assert.AreEqual(38L, second.Tokens.ReportedTotal.Value);
        Assert.AreEqual(38L, second.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Exact, second.Tokens.InputReported.Origin);
        Assert.AreEqual(MetricOrigin.Derived, second.Tokens.UncachedInput.Origin);
        Assert.AreEqual(MetricOrigin.Derived, second.Tokens.NormalizedTotal.Origin);
        Assert.AreEqual(MetricInclusion.Included, second.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Included, second.Tokens.ReasoningIncludedInOutput);
        Assert.AreEqual(EventIdentity(2), second.EventId);
        AssertCanonicalDedupKey(second);
        Assert.AreEqual(2L, second.SourceRevision);
        Assert.AreEqual("openai", second.Model.ProviderId);
        Assert.AreEqual(ExpectedProjectId("C:\\fixture\\project"), second.ProjectId);
        Assert.AreEqual(
            ExpectedProjectPath("C:\\fixture\\project"),
            second.ProjectPath);
        Assert.DoesNotContain("fixture", second.ProjectId!);
        Assert.IsTrue(results.All(static result => result.Diagnostic is null));
    }

    [TestMethod]
    public void ParseLine_UsesLastUsageWhenNoCumulativeSnapshotExists()
    {
        UsageEvent value = Assert.ContainsSingle(ParseFixture("last-only.jsonl")
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreEqual(9L, value.Tokens.InputReported.Value);
        Assert.AreEqual(5L, value.Tokens.UncachedInput.Value);
        Assert.AreEqual(4L, value.Tokens.CacheRead.Value);
        Assert.AreEqual(3L, value.Tokens.Output.Value);
        Assert.AreEqual(12L, value.Tokens.ReportedTotal.Value);
        Assert.AreEqual(12L, value.Tokens.NormalizedTotal.Value);
        Assert.AreEqual("gpt-last", value.Model.NormalizedModel);
        Assert.AreEqual(EventIdentity(1), value.EventId);
        AssertCanonicalDedupKey(value);
    }

    [TestMethod]
    public void ParseLine_UsesLastUsageAfterCumulativeReset()
    {
        UsageEvent[] events = ParseFixture("reset.jsonl")
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!)
            .ToArray();

        Assert.HasCount(2, events);
        UsageEvent reset = events[1];
        Assert.AreEqual(12L, reset.Tokens.InputReported.Value);
        Assert.AreEqual(5L, reset.Tokens.CacheRead.Value);
        Assert.AreEqual(4L, reset.Tokens.Output.Value);
        Assert.AreEqual(16L, reset.Tokens.ReportedTotal.Value);
        Assert.AreEqual(16L, reset.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(EventIdentity(2), reset.EventId);
        AssertCanonicalDedupKey(reset);
    }

    [TestMethod]
    public void ParseLine_ClampsCachedInputAndReportsFixedDiagnostic()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T05:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cache-thread\",\"model_provider\":\"openai\"}}",
            "{\"timestamp\":\"2026-07-16T05:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":20,\"cached_input_tokens\":90,\"output_tokens\":4,\"total_tokens\":24}}}}"
        ];

        CodexParseResult result = Parse(lines)[1];

        Assert.IsNotNull(result.Event);
        Assert.AreEqual(20L, result.Event.Tokens.CacheRead.Value);
        Assert.AreEqual(0L, result.Event.Tokens.UncachedInput.Value);
        Assert.AreEqual(24L, result.Event.Tokens.NormalizedTotal.Value);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual("codex.cached_input_clamped", result.Diagnostic.Code);
        Assert.AreEqual("Codex cached input exceeded reported input and was clamped.", result.Diagnostic.Message);
        Assert.DoesNotContain("90", result.Diagnostic.Message);
    }

    [TestMethod]
    public void ParseLine_DoesNotInventMissingDirectCacheMetrics()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T05:10:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"missing-cache-thread\"}}",
            "{\"timestamp\":\"2026-07-16T05:10:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":3,\"total_tokens\":13}}}}"
        ];

        UsageEvent value = Parse(lines)[1].Event!;

        Assert.AreEqual(MetricOrigin.Unavailable, value.Tokens.CacheRead.Origin);
        Assert.AreEqual(MetricOrigin.Unavailable, value.Tokens.UncachedInput.Origin);
        Assert.AreEqual(MetricOrigin.Unavailable, value.Tokens.CacheWrite.Origin);
        Assert.AreEqual(13L, value.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Derived, value.Tokens.NormalizedTotal.Origin);
    }

    [TestMethod]
    public void ParseLine_PreservesExplicitZeroCacheWriteAsExact()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T05:20:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"zero-cache-write-thread\"}}",
            "{\"timestamp\":\"2026-07-16T05:20:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":2,\"cache_write_tokens\":0,\"output_tokens\":3,\"total_tokens\":13}}}}"
        ];

        UsageEvent value = Parse(lines)[1].Event!;

        Assert.AreEqual(0L, value.Tokens.CacheWrite.Value);
        Assert.AreEqual(MetricOrigin.Exact, value.Tokens.CacheWrite.Origin);
        Assert.AreEqual(13L, value.Tokens.NormalizedTotal.Value);
    }

    [TestMethod]
    public void ParseLine_SameLogicalCallInDifferentRolloutsSharesDedupKeyButNotEventId()
    {
        string metadata =
            "{\"timestamp\":\"2026-07-16T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"shared-thread\",\"parent_thread_id\":\"direct-parent-thread\",\"forked_from_id\":\"history-origin-thread\",\"source\":{\"subagent\":\"fixture\"}}}";
        string boundary =
            "{\"timestamp\":\"2026-07-16T01:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}";
        string turn =
            "{\"timestamp\":\"2026-07-16T01:00:01.5000000Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"shared-turn\",\"model\":\"gpt-test\",\"effort\":\"medium\"}}";
        string token =
            "{\"timestamp\":\"2026-07-16T01:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12}}}}";

        UsageEvent first = Assert.ContainsSingle(Parse(
                [metadata, boundary, turn, token],
                CreateContext("codex:rollout:stream-a"))
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        UsageEvent second = Assert.ContainsSingle(Parse(
                [metadata, boundary, turn, token],
                CreateContext("codex:rollout:stream-b"))
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreEqual("codex:rollout:stream-a:token:1", first.EventId);
        Assert.AreEqual("codex:rollout:stream-b:token:1", second.EventId);
        Assert.AreNotEqual(first.EventId, second.EventId);
        Assert.AreEqual(first.DedupKey, second.DedupKey);
        AssertCanonicalDedupKey(first);
        Assert.AreEqual("shared-thread", first.SessionId);
        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual("direct-parent-thread", first.ParentSessionId);
    }

    [TestMethod]
    public void ParseLine_DifferentTurnIdsProduceDifferentCanonicalDedupKeys()
    {
        string[] firstLines = CanonicalCallLines("turn-a");
        string[] secondLines = CanonicalCallLines("turn-b");

        UsageEvent first = Assert.ContainsSingle(Parse(firstLines)
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        UsageEvent second = Assert.ContainsSingle(Parse(secondLines)
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreNotEqual(first.DedupKey, second.DedupKey);
        AssertCanonicalDedupKey(first);
        AssertCanonicalDedupKey(second);
    }

    [TestMethod]
    public void ParseLine_RepeatedSessionMetadataForSameThreadPreservesTurnIdentity()
    {
        string session =
            "{\"timestamp\":\"2026-07-16T01:25:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"same-thread\"}}";
        string turn =
            "{\"timestamp\":\"2026-07-16T01:25:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"same-turn\",\"model\":\"gpt-test\",\"effort\":\"medium\"}}";
        string token =
            "{\"timestamp\":\"2026-07-16T01:25:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12},\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12}}}}";

        UsageEvent direct = Assert.ContainsSingle(Parse(
                [session, turn, token],
                CreateContext("codex:rollout:session-repeat-a"))
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        UsageEvent repeated = Assert.ContainsSingle(Parse(
                [session, turn, session, token],
                CreateContext("codex:rollout:session-repeat-b"))
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreEqual(direct.DedupKey, repeated.DedupKey);
        Assert.AreNotEqual(direct.EventId, repeated.EventId);
    }

    [TestMethod]
    public void ParseLine_ExplicitUsageIdentityUsesScopeAndFieldPriority()
    {
        string firstToken =
            "{\"timestamp\":\"2026-07-16T01:30:02Z\",\"type\":\"event_msg\",\"usage_id\":\"root-winner\",\"payload\":{\"type\":\"token_count\",\"event_id\":\"payload-a\",\"info\":{\"call_id\":\"info-a\",\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12}}}}";
        string secondToken =
            "{\"timestamp\":\"2026-07-16T01:30:03Z\",\"type\":\"event_msg\",\"usage_id\":\"root-winner\",\"payload\":{\"type\":\"token_count\",\"event_id\":\"payload-b\",\"info\":{\"call_id\":\"info-b\",\"last_token_usage\":{\"input_tokens\":99,\"output_tokens\":1,\"total_tokens\":100}}}}";
        string differentFieldToken =
            "{\"timestamp\":\"2026-07-16T01:30:04Z\",\"type\":\"event_msg\",\"event_id\":\"root-winner\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12}}}}";
        string metadata =
            "{\"timestamp\":\"2026-07-16T01:30:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"identity-thread\"}}";

        UsageEvent first = Parse([metadata, firstToken])[1].Event!;
        UsageEvent second = Parse([metadata, secondToken])[1].Event!;
        UsageEvent differentField = Parse([metadata, differentFieldToken])[1].Event!;

        Assert.AreEqual(first.DedupKey, second.DedupKey);
        Assert.AreNotEqual(first.DedupKey, differentField.DedupKey);
        AssertCanonicalDedupKey(first);
    }

    [TestMethod]
    public void ParseLine_NoTurnIdUsesExactEventTimestampAsStrictFallback()
    {
        const string metadata =
            "{\"timestamp\":\"2026-07-16T01:40:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"fallback-thread\"}}";
        const string firstToken =
            "{\"timestamp\":\"2026-07-16T01:40:01.0000000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":4,\"output_tokens\":1,\"total_tokens\":5}}}}";
        const string secondToken =
            "{\"timestamp\":\"2026-07-16T01:40:01.0000001Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":4,\"output_tokens\":1,\"total_tokens\":5}}}}";

        UsageEvent first = Parse([metadata, firstToken])[1].Event!;
        UsageEvent sameExactTimestamp = Parse([metadata, firstToken])[1].Event!;
        UsageEvent differentTimestamp = Parse([metadata, secondToken])[1].Event!;

        Assert.AreEqual(first.DedupKey, sameExactTimestamp.DedupKey);
        Assert.AreNotEqual(first.DedupKey, differentTimestamp.DedupKey);
    }

    [TestMethod]
    public void ParseLine_PrefersLastUsageAndSuppressesNonAdvancingTotalSnapshot()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T01:50:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"last-priority-thread\"}}",
            "{\"timestamp\":\"2026-07-16T01:50:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T01:50:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7},\"total_token_usage\":{\"input_tokens\":160,\"output_tokens\":60,\"total_tokens\":220}}}}",
            "{\"timestamp\":\"2026-07-16T01:50:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":999,\"output_tokens\":999,\"total_tokens\":1998},\"total_token_usage\":{\"input_tokens\":160,\"output_tokens\":60,\"total_tokens\":220}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        UsageEvent[] events = results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!)
            .ToArray();

        Assert.HasCount(2, events);
        Assert.AreEqual(5L, events[1].Tokens.InputReported.Value);
        Assert.AreEqual(2L, events[1].Tokens.Output.Value);
        Assert.AreEqual(7L, events[1].Tokens.ReportedTotal.Value);
        Assert.IsNull(results[3].Event);
        Assert.AreEqual(220L, results[3].State.PreviousCumulative!.Total);
    }

    [TestMethod]
    public void ParseLine_TotalEqualitySuppressesLateComponentChanges()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T01:51:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"equal-total-thread\"}}",
            "{\"timestamp\":\"2026-07-16T01:51:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":60,\"output_tokens\":20,\"reasoning_output_tokens\":4,\"total_tokens\":120},\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":60,\"output_tokens\":20,\"reasoning_output_tokens\":4,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T01:51:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":70,\"output_tokens\":20,\"reasoning_output_tokens\":6,\"total_tokens\":120},\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":70,\"output_tokens\":20,\"reasoning_output_tokens\":6,\"total_tokens\":120}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsNotNull(results[1].Event);
        Assert.IsNull(results[2].Event);
        CodexTokenCounters baseline = results[2].State.PreviousCumulative!;
        Assert.AreEqual(120L, baseline.Total);
        Assert.AreEqual(70L, baseline.CachedInput);
        Assert.AreEqual(6L, baseline.Reasoning);
    }

    [TestMethod]
    public void ParseLine_RisingTotalDoesNotTreatCorrectedComponentAsReset()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T01:52:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"corrected-component-thread\"}}",
            "{\"timestamp\":\"2026-07-16T01:52:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T01:52:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":90,\"output_tokens\":40,\"total_tokens\":130}}}}"
        ];

        UsageEvent corrected = Parse(lines)[2].Event!;

        Assert.AreEqual(0L, corrected.Tokens.InputReported.Value);
        Assert.AreEqual(20L, corrected.Tokens.Output.Value);
        Assert.AreEqual(10L, corrected.Tokens.ReportedTotal.Value);
        Assert.AreEqual(20L, corrected.Tokens.NormalizedTotal.Value);
    }

    [TestMethod]
    public void ParseLine_FallingTotalStillStartsNewCumulativeSequence()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T01:53:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"scalar-reset-thread\"}}",
            "{\"timestamp\":\"2026-07-16T01:53:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T01:53:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":12,\"output_tokens\":4,\"total_tokens\":16},\"total_token_usage\":{\"input_tokens\":40,\"output_tokens\":10,\"total_tokens\":50}}}}"
        ];

        CodexParseResult reset = Parse(lines)[2];

        Assert.AreEqual(12L, reset.Event!.Tokens.InputReported.Value);
        Assert.AreEqual(4L, reset.Event.Tokens.Output.Value);
        Assert.AreEqual(16L, reset.Event.Tokens.ReportedTotal.Value);
        Assert.AreEqual(50L, reset.State.PreviousCumulative!.Total);
    }

    [TestMethod]
    public void ParseLine_OrdinarySideSessionCountsFirstReliableLastBeforeBoundary()
    {
        IReadOnlyList<CodexParseResult> results =
            ParseFixture("side-session-first-call.jsonl");

        UsageEvent value = Assert.ContainsSingle(results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreEqual("side-fixture-a", value.SessionId);
        Assert.AreEqual("primary-fixture-a", value.ParentSessionId);
        Assert.AreEqual(13L, value.Tokens.InputReported.Value);
        Assert.AreEqual(16L, value.Tokens.ReportedTotal.Value);
        Assert.AreEqual(136L, results[^1].State.PreviousCumulative!.Total);
        Assert.IsFalse(results[0].State.IsHistoryReplay);
        UsageSessionMetadata metadata = results[0].SessionMetadata!;
        Assert.AreEqual(SessionKind.Side, metadata.SessionKind);
        Assert.AreEqual(
            SessionRelationOrigin.TopLevelParentThreadId,
            metadata.RelationOrigin);
        Assert.AreEqual(SessionRelationState.Confirmed, metadata.RelationState);
        Assert.AreEqual(ReplayState.Active, metadata.ReplayState);
        Assert.AreEqual("primary-fixture-a", metadata.DirectParentSessionId);
        Assert.IsNull(metadata.ForkedFromSessionId);
        Assert.IsNull(metadata.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_OrdinarySideSessionInheritsTotalOnlyBaselineThenCountsFirstLast()
    {
        IReadOnlyList<CodexParseResult> results =
            ParseFixture("side-session-inherited-baseline.jsonl");

        Assert.IsNull(results[1].Event);
        Assert.AreEqual(120L, results[1].State.PreviousCumulative!.Total);
        UsageEvent value = Assert.ContainsSingle(results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        Assert.AreEqual("side-fixture-b", value.SessionId);
        Assert.AreEqual("primary-fixture-b", value.ParentSessionId);
        Assert.AreEqual(7L, value.Tokens.InputReported.Value);
        Assert.AreEqual(9L, value.Tokens.ReportedTotal.Value);
    }

    [TestMethod]
    public void ParseLine_SuppressesSubagentHistoryUntilReplayBoundary()
    {
        IReadOnlyList<CodexParseResult> results = ParseFixture("subagent-replay.jsonl");

        Assert.IsTrue(results[0].State.IsHistoryReplay);
        Assert.IsNull(results[2].Event);
        Assert.AreEqual(1L, results[2].State.TokenEventIndex);
        Assert.IsNull(results[3].Event);
        Assert.AreEqual(2L, results[3].State.TokenEventIndex);
        Assert.IsFalse(results[4].State.IsHistoryReplay);

        UsageEvent value = Assert.ContainsSingle(results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        Assert.AreEqual("child-thread", value.SessionId);
        Assert.AreEqual("direct-parent-thread", value.ParentSessionId);
        Assert.AreEqual(11L, value.Tokens.ReportedTotal.Value);
        Assert.AreEqual(EventIdentity(3), value.EventId);
        AssertCanonicalDedupKey(value);
        Assert.AreEqual(3L, value.SourceRevision);
        UsageSessionMetadata metadata = results[0].SessionMetadata!;
        Assert.AreEqual(SessionKind.Side, metadata.SessionKind);
        Assert.AreEqual("direct-parent-thread", metadata.DirectParentSessionId);
        Assert.AreEqual("history-origin-thread", metadata.ForkedFromSessionId);
        Assert.AreEqual(ReplayState.HistoryReplay, metadata.ReplayState);
    }

    [TestMethod]
    public void ParseLine_PrimaryForkRepeatedMetadataAfterBoundaryCountsActiveTail()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T09:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"fork-target\",\"forked_from_id\":\"history-origin\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"history-origin\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"history-turn\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120},\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T09:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"active-turn\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:04Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"fork-target\",\"forked_from_id\":\"history-origin\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:05Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"active-turn\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:06Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9,\"cached_input_tokens\":6,\"output_tokens\":2,\"total_tokens\":11},\"total_token_usage\":{\"input_tokens\":109,\"cached_input_tokens\":46,\"output_tokens\":22,\"total_tokens\":131}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsTrue(results[0].State.IsHistoryReplay);
        Assert.IsNull(results[3].Event);
        Assert.IsFalse(results[4].State.IsHistoryReplay);
        Assert.IsFalse(results[6].State.IsHistoryReplay);
        UsageSessionMetadata activeMetadata = results[6].SessionMetadata!;
        Assert.AreEqual("fork-target", activeMetadata.SessionId);
        Assert.AreEqual("history-origin", activeMetadata.ForkedFromSessionId);
        Assert.AreEqual(ReplayState.Active, activeMetadata.ReplayState);
        UsageEvent? active = results[8].Event;
        Assert.IsNotNull(active);
        Assert.AreEqual("fork-target", active.SessionId);
        Assert.AreEqual(9L, active.Tokens.InputReported.Value);
        Assert.AreEqual(6L, active.Tokens.CacheRead.Value);
        Assert.AreEqual(3L, active.Tokens.UncachedInput.Value);
        Assert.AreEqual(2L, active.Tokens.Output.Value);
        Assert.AreEqual(11L, active.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(EventIdentity(2), active.EventId);
        AssertCanonicalDedupKey(active);
    }

    [TestMethod]
    public void ParseLine_SamePathUuidForkRestoresTargetAtFirstProvablyNewTurn()
    {
        const string target = "019fbba4-1b1a-7560-b2d0-006521985379";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string historicalTurn = "019fb89a-0000-7000-8000-000000000002";
        const string activeTurn = "019fbba5-18b0-7000-8000-000000000003";
        string replayedDispatch = JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-01T04:45:30Z",
            type = "response_item",
            payload = new
            {
                type = "function_call",
                name = "spawn_agent",
                call_id = "replayed-call",
                arguments = "{\"task_name\":\"replayed-worker\"}"
            }
        });
        string[] lines =
        [
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{target}\",\"forked_from_id\":\"{origin}\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{origin}\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:25Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{historicalTurn}\"}}}}",
            replayedDispatch,
            "{\"timestamp\":\"2026-08-01T04:45:31Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            $"{{\"timestamp\":\"2026-08-01T04:46:27Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{activeTurn}\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:46:28Z\",\"type\":\"turn_context\",\"turn_id\":\"{activeTurn}\",\"payload\":{{\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            "{\"timestamp\":\"2026-08-01T04:46:29Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"branch prompt\"}}",
            "{\"timestamp\":\"2026-08-01T04:46:30Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9,\"cached_input_tokens\":6,\"output_tokens\":2,\"total_tokens\":11},\"total_token_usage\":{\"input_tokens\":109,\"cached_input_tokens\":46,\"output_tokens\":22,\"total_tokens\":131}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsTrue(results[4].State.IsHistoryReplay);
        Assert.IsNull(results[3].Dispatch);
        Assert.IsFalse(results[5].State.IsHistoryReplay);
        Assert.AreEqual(target, results[5].State.ThreadId);
        Assert.IsNull(results[5].State.ReplayTarget);
        Assert.AreEqual(target, results[5].SessionMetadata!.SessionId);
        Assert.AreEqual(target, results[7].TurnMetadata!.SessionId);
        Assert.AreEqual("branch prompt", results[7].TurnMetadata!.PromptPreview);
        Assert.AreEqual(target, results[8].Event!.SessionId);
        Assert.AreEqual(11L, results[8].Event!.Tokens.NormalizedTotal.Value);
    }

    [TestMethod]
    public void ParseLine_SamePathUuidSubagentRestoresCompleteChildIdentity()
    {
        const string target = "019fb8f6-449e-73b1-9e51-0d1fa1da8fa0";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string historicalTurn = "019fb8f5-a5d0-7000-8000-000000000002";
        const string activeTurn = "019fb8f6-4650-7000-8000-000000000003";
        string targetMetadata = JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-01T00:16:15Z",
            type = "session_meta",
            payload = new
            {
                id = target,
                parent_thread_id = origin,
                forked_from_id = origin,
                cwd = @"D:\Projects\codex\faker",
                agent_path = "root/protocol_worker",
                source = new { subagent = new { } }
            }
        });
        string[] lines =
        [
            targetMetadata,
            $"{{\"timestamp\":\"2026-08-01T00:16:15Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{origin}\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T00:16:16Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{historicalTurn}\"}}}}",
            "{\"timestamp\":\"2026-08-01T00:16:17Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            $"{{\"timestamp\":\"2026-08-01T00:16:18Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{activeTurn}\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T00:16:19Z\",\"type\":\"turn_context\",\"turn_id\":\"{activeTurn}\",\"payload\":{{\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            "{\"timestamp\":\"2026-08-01T00:16:20Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":7,\"output_tokens\":2,\"total_tokens\":9}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        UsageSessionMetadata initial = results[0].SessionMetadata!;
        UsageSessionMetadata active = results[4].SessionMetadata!;

        Assert.AreEqual(SessionRole.Subagent, active.SessionRole);
        Assert.AreEqual(SessionKind.Side, active.SessionKind);
        Assert.AreEqual(origin, active.DirectParentSessionId);
        Assert.AreEqual(SessionRelationState.Confirmed, active.RelationState);
        Assert.AreEqual(initial.AgentPathHash, active.AgentPathHash);
        Assert.AreEqual(initial.AgentLeafHash, active.AgentLeafHash);
        Assert.AreEqual(target, results[6].Event!.SessionId);
        Assert.AreEqual(origin, results[6].Event!.ParentSessionId);
    }

    [TestMethod]
    public void ParseLine_SameMillisecondUuidTurnDoesNotGuessReplayOwnership()
    {
        const string target = "019fbba4-1b1a-7560-b2d0-006521985379";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string ambiguousTurn = "019fbba4-1b1a-7fff-8000-000000000002";
        string[] lines =
        [
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{target}\",\"forked_from_id\":\"{origin}\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{origin}\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\faker\"}}}}",
            "{\"timestamp\":\"2026-08-01T04:45:23Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:24Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{ambiguousTurn}\"}}}}",
            "{\"timestamp\":\"2026-08-01T04:45:25Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":4,\"output_tokens\":1,\"total_tokens\":5}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsTrue(results[^1].State.IsHistoryReplay);
        Assert.AreEqual(origin, results[^1].State.ThreadId);
        Assert.IsNull(results[3].TurnMetadata);
        Assert.IsNull(results[4].Event);
    }

    [TestMethod]
    public void ParseLine_DifferentPathUuidForkPersistsPendingTurnUntilContext()
    {
        const string target = "019fbba4-1b1a-7560-b2d0-006521985379";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string activeTurn = "019fbba5-18b0-7000-8000-000000000003";
        string[] prefix =
        [
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{target}\",\"forked_from_id\":\"{origin}\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{origin}\",\"cwd\":\"C:\\\\fixture\\\\main\"}}}}",
            "{\"timestamp\":\"2026-08-01T04:45:23Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            $"{{\"timestamp\":\"2026-08-01T04:46:27Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{activeTurn}\"}}}}"
        ];

        CodexParseResult pending = Parse(prefix)[^1];
        Assert.IsFalse(pending.State.IsHistoryReplay);
        Assert.IsTrue(pending.State.IsReplayTargetContextPending);
        Assert.IsNull(pending.TurnMetadata);
        string cursorJson = new CodexCursor(
            new JsonlCursor(4, string.Empty, 4, new string('a', 64)),
            pending.State).Serialize();
        CodexCursor cursor = CodexCursor.DeserializeOrStart(
            cursorJson,
            out CollectorDiagnostic? diagnostic);
        Assert.IsNull(diagnostic);
        Assert.IsTrue(cursor.State.IsReplayTargetContextPending);

        string originContext =
            $"{{\"timestamp\":\"2026-08-01T04:46:28Z\",\"type\":\"turn_context\",\"turn_id\":\"{activeTurn}\",\"payload\":{{\"cwd\":\"C:\\\\fixture\\\\main\"}}}}";
        CodexParseResult continuedOrigin =
            ParseSingle(originContext, cursor.State, 5);
        Assert.IsFalse(continuedOrigin.State.IsReplayTargetContextPending);
        Assert.AreEqual(origin, continuedOrigin.State.ThreadId);
        Assert.AreEqual(origin, continuedOrigin.TurnMetadata!.SessionId);
        Assert.IsNull(continuedOrigin.SessionMetadata);

        string context =
            $"{{\"timestamp\":\"2026-08-01T04:46:28Z\",\"type\":\"turn_context\",\"turn_id\":\"{activeTurn}\",\"payload\":{{\"cwd\":\"C:\\\\fixture\\\\fork\"}}}}";
        CodexParseResult restored = ParseSingle(context, cursor.State, 5);

        Assert.IsFalse(restored.State.IsReplayTargetContextPending);
        Assert.AreEqual(target, restored.State.ThreadId);
        Assert.AreEqual(target, restored.TurnMetadata!.SessionId);
        Assert.AreEqual(target, restored.SessionMetadata!.SessionId);
    }

    [TestMethod]
    public void ParseLine_StoredReplaySourcePathSurvivesLaterPathlessMetadata()
    {
        const string target = "019fbba4-1b1a-7560-b2d0-006521985379";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string activeTurn = "019fbba5-18b0-7000-8000-000000000003";
        string[] lines =
        [
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{target}\",\"forked_from_id\":\"{origin}\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:45:22Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{origin}\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}}}",
            "{\"timestamp\":\"2026-08-01T04:45:23Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"019fb800-0000-7000-8000-000000000004\"}}",
            "{\"timestamp\":\"2026-08-01T04:45:24Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            $"{{\"timestamp\":\"2026-08-01T04:46:27Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{activeTurn}\"}}}}",
            $"{{\"timestamp\":\"2026-08-01T04:46:28Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{target}\",\"forked_from_id\":\"{origin}\",\"cwd\":\"C:\\\\fixture\\\\fork\"}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        CodexParseResult result = results[^1];

        Assert.IsFalse(result.State.IsHistoryReplay);
        Assert.IsNull(result.State.ReplayTarget);
        Assert.AreEqual(target, result.State.ThreadId);
        Assert.AreEqual(ReplayState.Active, result.SessionMetadata!.ReplayState);
        CodexParseResult active = results[^2];
        Assert.AreEqual(target, active.TurnMetadata!.SessionId);
        Assert.AreEqual(target, result.SessionMetadata!.SessionId);
    }

    [TestMethod]
    public void ParseLine_PrimaryForkRestoresTargetOnlyAfterActiveTargetContext()
    {
        const string repositoryUrl =
            "https://github.com/example/AgenTally.git";
        string[] lines =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T09:00:00Z",
                type = "session_meta",
                payload = new
                {
                    id = "fork-target",
                    forked_from_id = "main-origin",
                    cwd = @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally",
                    git = new { repository_url = repositoryUrl }
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T09:00:00Z",
                type = "session_meta",
                payload = new
                {
                    id = "main-origin",
                    cwd = @"C:\Projects\AgenTally",
                    git = new { repository_url = repositoryUrl }
                }
            }),
            "{\"timestamp\":\"2026-07-16T09:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"history-turn\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\AgenTally\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120},\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T09:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"main-live-turn\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:04Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"main-live-turn\",\"cwd\":\"D:\\\\Projects\\\\codex\\\\AgenTally\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"main continuation\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:06Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":5,\"cached_input_tokens\":2,\"output_tokens\":2,\"total_tokens\":7},\"total_token_usage\":{\"input_tokens\":105,\"cached_input_tokens\":42,\"output_tokens\":22,\"total_tokens\":127}}}}",
            "{\"timestamp\":\"2026-07-16T09:00:07Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:08Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"branch-live-turn\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:08Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"branch-live-turn\",\"cwd\":\"C:\\\\Users\\\\fixture\\\\.codex\\\\worktrees\\\\2f68\\\\AgenTally\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:09Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"branch continuation\"}}",
            "{\"timestamp\":\"2026-07-16T09:00:10Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":7,\"cached_input_tokens\":3,\"output_tokens\":2,\"total_tokens\":9},\"total_token_usage\":{\"input_tokens\":112,\"cached_input_tokens\":45,\"output_tokens\":24,\"total_tokens\":136}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsNull(results[3].Event);
        Assert.AreEqual("main-origin", results[8].Event!.SessionId);
        Assert.AreEqual("main-origin", results[7].TurnMetadata!.SessionId);
        Assert.AreEqual("fork-target", results[11].State.ThreadId);
        Assert.AreEqual(
            @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally",
            results[11].State.ProjectPath);
        Assert.IsNotNull(results[11].SessionMetadata);
        Assert.AreEqual(
            ReplayState.Active,
            results[11].SessionMetadata!.ReplayState);
        Assert.AreEqual("fork-target", results[12].TurnMetadata!.SessionId);
        Assert.AreEqual("fork-target", results[13].Event!.SessionId);
        Assert.AreEqual(9L, results[13].Event!.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(
            results[8].Event!.ProjectId,
            results[13].Event!.ProjectId);
    }

    [TestMethod]
    public void ParseLine_PrimaryAndSideCallsNeverShareCanonicalDedupIdentity()
    {
        const string primary =
            "{\"timestamp\":\"2026-07-16T09:20:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"primary-call-session\"}}";
        const string side =
            "{\"timestamp\":\"2026-07-16T09:20:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"side-call-session\",\"parent_thread_id\":\"primary-call-session\",\"thread_source\":\"subagent\"}}";
        const string turn =
            "{\"timestamp\":\"2026-07-16T09:20:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"synthetic-shared-turn\"}}";
        const string token =
            "{\"timestamp\":\"2026-07-16T09:20:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":4,\"cached_input_tokens\":1,\"output_tokens\":1,\"total_tokens\":5},\"total_token_usage\":{\"input_tokens\":4,\"cached_input_tokens\":1,\"output_tokens\":1,\"total_tokens\":5}}}}";

        UsageEvent primaryEvent = Parse([primary, turn, token])[^1].Event!;
        UsageEvent sideEvent = Parse([side, turn, token])[^1].Event!;

        Assert.AreNotEqual(primaryEvent.DedupKey, sideEvent.DedupKey);
        Assert.AreEqual(primaryEvent.TurnIdHash, sideEvent.TurnIdHash);
        Assert.AreNotEqual(primaryEvent.SessionId, sideEvent.SessionId);
    }

    [TestMethod]
    public void ParseLine_ConflictingDirectParentsRemainIndependentWithoutSuppressingUsage()
    {
        const string metadata =
            "{\"timestamp\":\"2026-07-16T09:30:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"conflict-side\",\"parent_thread_id\":\"top-parent\",\"thread_source\":\"subagent\",\"source\":{\"subagent\":{\"parent_thread_id\":\"nested-parent\"}}}}";
        const string token =
            "{\"timestamp\":\"2026-07-16T09:30:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":3,\"output_tokens\":1,\"total_tokens\":4}}}}";

        IReadOnlyList<CodexParseResult> results = Parse([metadata, token]);

        UsageEvent emitted = results[1].Event!;
        Assert.IsNotNull(emitted);
        Assert.IsNull(emitted.ParentSessionId);
        Assert.AreEqual(SessionKind.Side, results[0].State.SessionKind);
        Assert.AreEqual(
            SessionRelationState.Uncertain,
            results[0].State.ParentRelationState);
        Assert.AreEqual(
            CompatibilityLevel.PartiallyCompatible,
            results[0].State.CompatibilityLevel);
        Assert.IsFalse(results[0].State.IsHistoryReplay);
        UsageSessionMetadata session = results[0].SessionMetadata!;
        Assert.IsNull(session.DirectParentSessionId);
        Assert.AreEqual(SessionRelationState.Uncertain, session.RelationState);
    }

    [TestMethod]
    public void ParseLine_RepeatedSideSessionMetadataDoesNotReplayOrDuplicateFirstCall()
    {
        const string metadata =
            "{\"timestamp\":\"2026-07-16T09:40:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"repeat-side\",\"parent_thread_id\":\"repeat-primary\",\"thread_source\":\"subagent\"}}";
        const string turn =
            "{\"timestamp\":\"2026-07-16T09:40:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"repeat-side-turn\"}}";
        const string token =
            "{\"timestamp\":\"2026-07-16T09:40:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":6,\"output_tokens\":2,\"total_tokens\":8},\"total_token_usage\":{\"input_tokens\":106,\"output_tokens\":22,\"total_tokens\":128}}}}";

        UsageEvent direct = Assert.ContainsSingle(Parse([metadata, turn, token])
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        IReadOnlyList<CodexParseResult> repeatedResults =
            Parse([metadata, turn, metadata, token]);
        UsageEvent repeated = Assert.ContainsSingle(repeatedResults
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));

        Assert.AreEqual(direct.DedupKey, repeated.DedupKey);
        Assert.AreEqual("repeat-primary", repeated.ParentSessionId);
        Assert.AreEqual(SessionKind.Side, repeatedResults[2].State.SessionKind);
        Assert.IsFalse(repeatedResults[2].State.IsHistoryReplay);
    }

    [TestMethod]
    public void ParseLine_SubagentWithoutBoundaryKeepsReplayBaselineAndEmitsNothing()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T05:40:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"no-boundary-child\",\"forked_from_id\":\"parent-thread\",\"source\":{\"subagent\":\"fixture\"}}}",
            "{\"timestamp\":\"2026-07-16T05:40:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        CodexParseState state = results[^1].State;

        Assert.IsEmpty(results.Where(static result => result.Event is not null));
        Assert.AreEqual(1L, state.TokenEventIndex);
        Assert.AreEqual(
            new CodexTokenCounters(10, 4, 2, null, null, 12),
            state.PreviousCumulative);
        Assert.IsTrue(state.IsHistoryReplay);
    }

    [TestMethod]
    public void ParseLine_InterAgentCommunicationAlsoEndsReplay()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"child-2\",\"session_id\":\"parent-2\",\"model_provider\":\"openai\"}}",
            "{\"timestamp\":\"2026-07-16T06:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":7,\"cached_input_tokens\":2,\"output_tokens\":1,\"total_tokens\":8}}}}",
            "{\"timestamp\":\"2026-07-16T06:00:02Z\",\"type\":\"inter_agent_communication\",\"payload\":{}}",
            "{\"timestamp\":\"2026-07-16T06:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":3,\"output_tokens\":3,\"total_tokens\":13}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsFalse(results[2].State.IsHistoryReplay);
        UsageEvent value = Assert.ContainsSingle(results
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        Assert.AreEqual(3L, value.Tokens.InputReported.Value);
        Assert.AreEqual(2L, value.Tokens.Output.Value);
        Assert.AreEqual("parent-2", value.ParentSessionId);
        Assert.AreEqual(EventIdentity(2), value.EventId);
        AssertCanonicalDedupKey(value);
    }

    [TestMethod]
    public void ParseLine_ReadsNestedSubagentParentIdentity()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:30:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"nested-child\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\"nested-parent\"}}}}}",
            "{\"timestamp\":\"2026-07-16T06:30:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            "{\"timestamp\":\"2026-07-16T06:30:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        UsageEvent value = Parse(lines)[2].Event!;

        Assert.AreEqual("nested-parent", value.ParentSessionId);
    }

    [TestMethod]
    public void ParseLine_TokenWithoutUsageStillAdvancesStableIndex()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:40:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"empty-thread\"}}",
            "{\"timestamp\":\"2026-07-16T06:40:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{}}}",
            "{\"timestamp\":\"2026-07-16T06:40:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsNull(results[1].Event);
        Assert.AreEqual(1L, results[1].State.TokenEventIndex);
        Assert.AreEqual(EventIdentity(2), results[2].Event!.EventId);
        AssertCanonicalDedupKey(results[2].Event!);
    }

    [TestMethod]
    public void ParseLine_EmptyUsageObjectsDoNotEmitOrHideValidSnapshot()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:45:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"empty-object-thread\"}}",
            "{\"timestamp\":\"2026-07-16T06:45:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{},\"total_token_usage\":{\"input_tokens\":5,\"cached_input_tokens\":1,\"output_tokens\":2,\"total_tokens\":7}}}}",
            "{\"timestamp\":\"2026-07-16T06:45:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":3,\"cached_input_tokens\":1,\"output_tokens\":1,\"total_tokens\":4},\"total_token_usage\":{}}}}",
            "{\"timestamp\":\"2026-07-16T06:45:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{},\"total_token_usage\":{}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.AreEqual(5L, results[1].Event!.Tokens.InputReported.Value);
        Assert.AreEqual(3L, results[2].Event!.Tokens.InputReported.Value);
        Assert.IsNull(results[3].Event);
        Assert.AreEqual(3L, results[3].State.TokenEventIndex);
        Assert.AreEqual(5L, results[3].State.PreviousCumulative!.Input);
    }

    [TestMethod]
    public void ParseLine_PartialCumulativeSnapshotPreservesMissingFieldBaseline()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:47:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"partial-total-thread\"}}",
            "{\"timestamp\":\"2026-07-16T06:47:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T06:47:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":110,\"cached_input_tokens\":45,\"total_tokens\":130}}}}",
            "{\"timestamp\":\"2026-07-16T06:47:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":120,\"cached_input_tokens\":50,\"output_tokens\":28,\"total_tokens\":148}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.AreEqual(MetricOrigin.Unavailable, results[2].Event!.Tokens.Output.Origin);
        Assert.AreEqual(20L, results[2].State.PreviousCumulative!.Output);
        Assert.AreEqual(8L, results[3].Event!.Tokens.Output.Value);
        Assert.AreEqual(18L, results[3].Event!.Tokens.ReportedTotal.Value);
    }

    [TestMethod]
    public void ParseLine_ResetStartsFreshPartialBaselineWithoutOldFields()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:48:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"fresh-reset-thread\"}}",
            "{\"timestamp\":\"2026-07-16T06:48:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120}}}}",
            "{\"timestamp\":\"2026-07-16T06:48:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":2,\"output_tokens\":2,\"total_tokens\":12},\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":2,\"total_tokens\":10}}}}"
        ];

        CodexParseResult reset = Parse(lines)[2];

        Assert.AreEqual(2L, reset.Event!.Tokens.Output.Value);
        Assert.IsNull(reset.State.PreviousCumulative!.Output);
        Assert.AreEqual(10L, reset.State.PreviousCumulative.Input);
    }

    [TestMethod]
    public void ParseLine_StructurallyIncompleteMetadataDoesNotThrow()
    {
        CodexParseResult result = Parse(
            ["{\"timestamp\":\"2026-07-16T06:50:00Z\",\"type\":\"session_meta\"}"])[0];

        Assert.IsNull(result.Event);
        Assert.AreEqual(new CodexParseState(), result.State);
        Assert.IsNull(result.Diagnostic);
    }

    [TestMethod]
    public void ParseLine_RejectsTimestampWithoutExplicitTimeZone()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T06:55:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"timezone-thread\"}}",
            "{\"timestamp\":\"2026-07-16T06:55:01\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        CodexParseResult result = Parse(lines)[1];

        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual("codex.invalid_token_event", result.Diagnostic.Code);
        Assert.AreEqual("Codex token event contains invalid structural data.", result.Diagnostic.Message);
        Assert.DoesNotContain("2026-07-16", result.Diagnostic.Message);
    }

    [TestMethod]
    public void ParseLine_NormalizesExplicitTimestampOffsetToUtc()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"offset-thread\"}}",
            "{\"timestamp\":\"2026-07-16T08:00:01+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        UsageEvent value = Parse(lines)[1].Event!;

        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 0, 0, 1, TimeSpan.Zero),
            value.OccurredAtUtc);
    }

    [TestMethod]
    public void ParseLine_NormalizesOnlyTheFinalModelSegmentAndKnownDateSuffix()
    {
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"model-thread\",\"model_provider\":\"provider\"}}",
            "{\"timestamp\":\"2026-07-16T07:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"Provider/Team/GPT-Custom-20260701\"}}",
            "{\"timestamp\":\"2026-07-16T07:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":2}}}}"
        ];

        UsageEvent value = Parse(lines)[2].Event!;

        Assert.AreEqual("Provider/Team/GPT-Custom-20260701", value.Model.RawModel);
        Assert.AreEqual("gpt-custom", value.Model.NormalizedModel);
    }

    [TestMethod]
    public void ParseLine_MalformedJsonUsesPrivacySafeDiagnostic()
    {
        const string privateLine = "{private-prompt-content}";

        CodexParseResult result = Parse([privateLine])[0];

        Assert.IsNull(result.Event);
        Assert.IsNotNull(result.Diagnostic);
        Assert.AreEqual("codex.invalid_json", result.Diagnostic.Code);
        Assert.DoesNotContain(privateLine, result.Diagnostic.Message);
        Assert.AreEqual(0L, result.Diagnostic.ByteOffset);
    }

    [TestMethod]
    public void ParseLine_CapturesOnlyFirstPromptPreviewAndCountsSupplements()
    {
        string firstMessage = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:05:02Z",
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message = "  第一行 \r\n\t 第二行  ",
                images = new[] { @"C:\private\image.png" },
                audio = new[] { @"C:\private\voice.wav" }
            }
        });
        string supplement = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:05:03Z",
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message = "不得替换第一条摘要的补充消息"
            }
        });
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:05:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"prompt-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:05:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"prompt-turn\"}}",
            firstMessage,
            supplement,
            "{\"timestamp\":\"2026-07-16T07:05:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"prompt-turn\"}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        UsageTurnMetadata completed = results[4].TurnMetadata!;

        Assert.AreEqual("[图片] [音频] 第一行 第二行", completed.PromptPreview);
        Assert.AreEqual(2, completed.UserMessageCount);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 7, 5, 1, TimeSpan.Zero),
            completed.StartedAtUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 7, 5, 4, TimeSpan.Zero),
            completed.CompletedAtUtc);
        string persistedShape = JsonSerializer.Serialize(completed);
        Assert.DoesNotContain(@"C:\private", persistedShape);
        Assert.DoesNotContain("补充消息", persistedShape);
    }

    [TestMethod]
    public void ParseLine_RemovesCodexAttachmentEnvelopePathsFromPromptPreview()
    {
        const string attachmentPath =
            @"C:\Users\fixture\AppData\Local\Temp\codex-clipboard-secret.png";
        string message = $"""
            # Files mentioned by the user:

            ## codex-clipboard-secret.png: {attachmentPath}

            ## My request for Codex:
            只保留这句实际请求
            <image name=[Image #1] path="{attachmentPath}">
            """;
        string userMessage = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:05:12Z",
            type = "event_msg",
            payload = new
            {
                type = "user_message",
                message,
                local_images = new[] { attachmentPath }
            }
        });
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:05:10Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"attachment-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:05:11Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"attachment-turn\"}}",
            userMessage
        ];

        string preview = Parse(lines)[2].TurnMetadata!.PromptPreview!;

        Assert.AreEqual("[图片] 只保留这句实际请求", preview);
        Assert.DoesNotContain("Files mentioned", preview);
        Assert.DoesNotContain("codex-clipboard", preview);
        Assert.DoesNotContain("AppData", preview);
        Assert.DoesNotContain("<image", preview);
    }

    [TestMethod]
    public void ParseLine_TruncatesPromptAt120UnicodeScalarsWithoutSplittingEmoji()
    {
        string privateTail = "TAIL-MUST-NOT-BE-STORED";
        string message = $"  {new string('甲', 119)}😀{privateTail}";
        string userMessage = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:06:02Z",
            type = "event_msg",
            payload = new { type = "user_message", message }
        });
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:06:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"unicode-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:06:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"unicode-turn\"}}",
            userMessage
        ];

        string preview = Parse(lines)[2].TurnMetadata!.PromptPreview!;

        Assert.AreEqual(120, preview.EnumerateRunes().Count());
        Assert.EndsWith("😀", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(privateTail, preview);
    }

    [TestMethod]
    public void ParseLine_BindsToolNamesAndHashesDispatchTargetsWithoutArguments()
    {
        string shell = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:07:02Z",
            type = "response_item",
            payload = new
            {
                type = "function_call",
                name = "shell_command",
                call_id = "shell-call",
                arguments = "{\"command\":\"contains-private-argument\"}"
            }
        });
        string spawn = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:07:03Z",
            type = "response_item",
            payload = new
            {
                type = "function_call",
                name = "spawn_agent",
                call_id = "spawn-call",
                arguments =
                    "{\"task_name\":\"child_worker\",\"message\":\"contains-private-instructions\"}"
            }
        });
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:07:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"dispatch-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:07:01Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"dispatch-turn\"}}",
            shell,
            spawn,
            "{\"timestamp\":\"2026-07-16T07:07:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":2,\"output_tokens\":3,\"total_tokens\":13}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        UsageTurnDispatch dispatch = results[3].Dispatch!;
        CodexParseResult token = results[4];

        Assert.AreEqual(TurnDispatchKind.Spawn, dispatch.DispatchKind);
        Assert.AreEqual(DispatchTargetKind.AgentLeaf, dispatch.TargetKind);
        Assert.AreEqual(64, dispatch.TargetAgentHash.Length);
        Assert.HasCount(2, token.EventTools);
        CollectionAssert.AreEqual(
            new[] { "shell_command", "spawn_agent" },
            token.EventTools.Select(static tool => tool.ToolName).ToArray());
        string persistedShape = JsonSerializer.Serialize(new
        {
            dispatch,
            token.EventTools
        });
        Assert.DoesNotContain("child_worker", persistedShape);
        Assert.DoesNotContain("contains-private", persistedShape);
    }

    [TestMethod]
    public void ParseLine_RecognizesGuardianRoleButStoresOnlyHashedAgentPath()
    {
        string metadata = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:08:00Z",
            type = "session_meta",
            payload = new
            {
                id = "guardian-thread",
                parent_thread_id = "root-thread",
                agent_path = "/root/private_guardian",
                source = new { subagent = new { other = "guardian" } }
            }
        });

        UsageSessionMetadata session = Parse([metadata])[0].SessionMetadata!;

        Assert.AreEqual(SessionRole.Guardian, session.SessionRole);
        Assert.AreEqual(64, session.AgentPathHash!.Length);
        Assert.AreEqual(64, session.AgentLeafHash!.Length);
        Assert.DoesNotContain(
            "private_guardian",
            JsonSerializer.Serialize(session));
    }

    [TestMethod]
    public void ParseLine_InvalidPresentModelClearsOldModelAndReportsFixedDiagnostic()
    {
        string tooLongModel = new('m', 1025);
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:10:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"metadata-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:10:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"safe-model\"}}",
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T07:10:02Z",
                type = "turn_context",
                payload = new { model = tooLongModel }
            }),
            "{\"timestamp\":\"2026-07-16T07:10:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);
        CodexParseResult invalid = results[2];
        UsageEvent value = results[3].Event!;

        Assert.IsNotNull(invalid.Diagnostic);
        Assert.AreEqual("codex.invalid_state_metadata", invalid.Diagnostic.Code);
        Assert.AreEqual(
            "Codex state metadata contained an invalid value and was cleared.",
            invalid.Diagnostic.Message);
        Assert.DoesNotContain(tooLongModel, invalid.Diagnostic.Message);
        Assert.IsNull(invalid.State.CurrentRawModel);
        Assert.IsNull(value.Model.RawModel);
        Assert.IsNull(value.Model.NormalizedModel);
        Assert.AreEqual(ModelResolutionOrigin.Unknown, value.Model.ResolutionOrigin);
    }

    [TestMethod]
    public void ParseLine_InvalidParentMarkersSuppressHistoryUntilBoundary()
    {
        string invalidSession = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:20:00Z",
            type = "session_meta",
            payload = new
            {
                id = "history-thread",
                forked_from_id = 42,
                session_id = "bad\u0001session",
                source = new { subagent = new { parent_thread_id = 42 } }
            }
        });
        string token =
            "{\"timestamp\":\"2026-07-16T07:20:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}";
        string boundary =
            "{\"timestamp\":\"2026-07-16T07:20:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}";

        IReadOnlyList<CodexParseResult> results = Parse(
            [invalidSession, token, boundary, token]);

        Assert.AreEqual("codex.invalid_state_metadata", results[0].Diagnostic!.Code);
        Assert.IsNull(results[0].State.ParentSessionId);
        Assert.IsTrue(results[0].State.IsHistoryReplay);
        Assert.IsNull(results[1].Event);
        Assert.AreEqual(1L, results[1].State.TokenEventIndex);
        Assert.IsFalse(results[2].State.IsHistoryReplay);
        UsageEvent emitted = results[3].Event!;
        Assert.AreEqual(EventIdentity(2), emitted.EventId);
        AssertCanonicalDedupKey(emitted);
        Assert.IsNull(emitted.ParentSessionId);
    }

    [TestMethod]
    public void ParseLine_BlankAndControlIdsNeverReuseOldThreadOrCumulativeState()
    {
        string controlId = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:30:03Z",
            type = "session_meta",
            payload = new { id = "bad\u0001thread" }
        });
        string[] lines =
        [
            "{\"timestamp\":\"2026-07-16T07:30:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"old-thread\"}}",
            "{\"timestamp\":\"2026-07-16T07:30:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":2,\"output_tokens\":3,\"total_tokens\":13}}}}",
            "{\"timestamp\":\"2026-07-16T07:30:02Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\" \"}}",
            controlId,
            "{\"timestamp\":\"2026-07-16T07:30:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];

        IReadOnlyList<CodexParseResult> results = Parse(lines);

        Assert.IsNotNull(results[1].State.PreviousCumulative);
        Assert.AreEqual("codex.invalid_state_metadata", results[2].Diagnostic!.Code);
        Assert.AreEqual("codex.invalid_state_metadata", results[3].Diagnostic!.Code);
        Assert.IsNull(results[3].State.ThreadId);
        Assert.IsNull(results[3].State.PreviousCumulative);
        Assert.IsTrue(results[3].State.IsHistoryReplay);
        Assert.IsNull(results[4].Event);
        Assert.AreEqual(2L, results[4].State.TokenEventIndex);
    }

    [TestMethod]
    public void CodexCursor_SizeBudgetIncludesWorstCaseJsonEscapingForStateStrings()
    {
        const int expectedStateCharacters =
            (6 * 1024) +
            24 +
            (2 * 32767) +
            64 +
            64;
        const int expectedStateBudget = expectedStateCharacters * 6;
        var field = typeof(CodexCursor).GetField(
            "MaxSerializedCursorCharacters",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(field);
        int actual = (int)field.GetRawConstantValue()!;
        Assert.IsGreaterThanOrEqualTo(
            JsonlCursor.MaxSerializedCursorCharacters + expectedStateBudget + 2048,
            actual);
    }

    [TestMethod]
    public void ParseLine_ChangedValidThreadStartsCleanIdentityBoundary()
    {
        var oldState = new CodexParseState(
            ThreadId: "old-thread",
            ParentSessionId: "old-parent",
            CurrentRawModel: "old-model",
            CurrentProviderId: "old-provider",
            ProjectId: ExpectedProjectId(@"C:\fixture\old"),
            ProjectPath: ExpectedProjectPath(@"C:\fixture\old"),
            PreviousCumulative: new CodexTokenCounters(100, 20, 30, 5, 0, 130),
            TokenEventIndex: 7,
            IsHistoryReplay: true);
        const string newCwd = @"C:\fixture\new";
        string metadata = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T07:40:00Z",
            type = "session_meta",
            payload = new
            {
                id = "new-thread",
                parent_thread_id = "new-parent",
                forked_from_id = "history-origin",
                model_provider = "new-provider",
                cwd = newCwd
            }
        });

        CodexParseResult changed = ParseSingle(metadata, oldState, 1);
        CodexParseResult boundary = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:40:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            changed.State,
            2);
        CodexParseResult token = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:40:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":120,\"cached_input_tokens\":20,\"output_tokens\":30,\"total_tokens\":150},\"total_token_usage\":{\"input_tokens\":120,\"cached_input_tokens\":20,\"output_tokens\":30,\"total_tokens\":150}}}}",
            boundary.State,
            3);

        Assert.IsNull(changed.Diagnostic);
        Assert.AreEqual("new-thread", changed.State.ThreadId);
        Assert.AreEqual("new-parent", changed.State.ParentSessionId);
        Assert.IsNull(changed.State.CurrentRawModel);
        Assert.AreEqual("new-provider", changed.State.CurrentProviderId);
        Assert.AreEqual(ExpectedProjectId(newCwd), changed.State.ProjectId);
        Assert.AreEqual(ExpectedProjectPath(newCwd), changed.State.ProjectPath);
        Assert.IsNull(changed.State.PreviousCumulative);
        Assert.AreEqual(7L, changed.State.TokenEventIndex);
        Assert.IsTrue(changed.State.IsHistoryReplay);
        UsageEvent value = token.Event!;
        Assert.AreEqual(EventIdentity(8), value.EventId);
        AssertCanonicalDedupKey(value);
        Assert.AreEqual(120L, value.Tokens.InputReported.Value);
        Assert.AreEqual("new-parent", value.ParentSessionId);
        Assert.IsNull(value.Model.RawModel);
        Assert.AreEqual("new-provider", value.Model.ProviderId);
        Assert.AreEqual(ExpectedProjectId(newCwd), value.ProjectId);
        Assert.AreEqual(ExpectedProjectPath(newCwd), value.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_ChangedValidThreadWithoutReplayCountsOnlyTheNewCall()
    {
        var oldState = new CodexParseState(
            ThreadId: "old-thread",
            ParentSessionId: "old-parent",
            CurrentRawModel: "old-model",
            CurrentProviderId: "old-provider",
            ProjectId: ExpectedProjectId(@"C:\fixture\old"),
            ProjectPath: ExpectedProjectPath(@"C:\fixture\old"),
            PreviousCumulative: new CodexTokenCounters(100, 20, 30, 5, 0, 130),
            TokenEventIndex: 7,
            IsHistoryReplay: true);
        CodexParseResult changed = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:45:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"new-thread\",\"model_provider\":\"new-provider\",\"cwd\":\"C:\\\\fixture\\\\new\"}}",
            oldState,
            1);
        CodexParseResult token = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:45:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":5,\"cached_input_tokens\":2,\"output_tokens\":2,\"total_tokens\":7},\"total_token_usage\":{\"input_tokens\":1005,\"cached_input_tokens\":202,\"output_tokens\":302,\"total_tokens\":1307}}}}",
            changed.State,
            2);

        Assert.IsNull(changed.Diagnostic);
        Assert.AreEqual("new-thread", changed.State.ThreadId);
        Assert.IsNull(changed.State.ParentSessionId);
        Assert.IsNull(changed.State.PreviousCumulative);
        Assert.IsFalse(changed.State.IsHistoryReplay);
        Assert.IsNull(changed.State.CurrentRawModel);
        Assert.AreEqual("new-provider", changed.State.CurrentProviderId);
        Assert.AreEqual(ExpectedProjectId(@"C:\fixture\new"), changed.State.ProjectId);
        UsageEvent value = token.Event!;
        Assert.AreEqual("new-thread", value.SessionId);
        Assert.AreEqual(5L, value.Tokens.InputReported.Value);
        Assert.AreEqual(2L, value.Tokens.CacheRead.Value);
        Assert.AreEqual(3L, value.Tokens.UncachedInput.Value);
        Assert.AreEqual(2L, value.Tokens.Output.Value);
        Assert.AreEqual(7L, value.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(EventIdentity(8), value.EventId);
        AssertCanonicalDedupKey(value);
    }

    [TestMethod]
    public void ParseLine_RecoveredThreadCannotInheritInvalidIdentityScope()
    {
        var oldState = new CodexParseState(
            ThreadId: "old-thread",
            ParentSessionId: "old-parent",
            CurrentRawModel: "old-model",
            CurrentProviderId: "old-provider",
            ProjectId: ExpectedProjectId(@"C:\fixture\old"),
            ProjectPath: ExpectedProjectPath(@"C:\fixture\old"),
            PreviousCumulative: new CodexTokenCounters(90, 20, 20, 4, 0, 110),
            TokenEventIndex: 4,
            IsHistoryReplay: false);

        CodexParseResult invalid = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:49:59Z\",\"type\":\"session_meta\",\"payload\":{\"id\":null,\"forked_from_id\":\"untrusted-parent\",\"model_provider\":\"untrusted-provider\",\"cwd\":\"C:\\\\fixture\\\\untrusted\"}}",
            oldState,
            1);

        CodexParseResult recovered = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:50:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"recovered-thread\"}}",
            invalid.State,
            2);
        CodexParseResult boundary = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:50:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}",
            recovered.State,
            3);
        CodexParseResult token = ParseSingle(
            "{\"timestamp\":\"2026-07-16T07:50:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":50,\"cached_input_tokens\":10,\"output_tokens\":10,\"total_tokens\":60}}}}",
            boundary.State,
            4);

        Assert.AreEqual("codex.invalid_state_metadata", invalid.Diagnostic!.Code);
        Assert.IsNull(invalid.State.ThreadId);
        Assert.IsNull(invalid.State.ParentSessionId);
        Assert.IsNull(invalid.State.CurrentRawModel);
        Assert.IsNull(invalid.State.CurrentProviderId);
        Assert.IsNull(invalid.State.ProjectId);
        Assert.IsNull(invalid.State.ProjectPath);
        Assert.IsNull(invalid.State.PreviousCumulative);
        Assert.AreEqual(4L, invalid.State.TokenEventIndex);
        Assert.IsTrue(invalid.State.IsHistoryReplay);
        Assert.IsNull(recovered.Diagnostic);
        Assert.AreEqual("recovered-thread", recovered.State.ThreadId);
        Assert.IsNull(recovered.State.ParentSessionId);
        Assert.IsNull(recovered.State.CurrentRawModel);
        Assert.IsNull(recovered.State.CurrentProviderId);
        Assert.IsNull(recovered.State.ProjectId);
        Assert.IsNull(recovered.State.ProjectPath);
        Assert.IsNull(recovered.State.PreviousCumulative);
        Assert.AreEqual(4L, recovered.State.TokenEventIndex);
        Assert.IsTrue(recovered.State.IsHistoryReplay);
        UsageEvent value = token.Event!;
        Assert.AreEqual(EventIdentity(5), value.EventId);
        AssertCanonicalDedupKey(value);
        Assert.AreEqual(50L, value.Tokens.InputReported.Value);
        Assert.IsNull(value.ParentSessionId);
        Assert.IsNull(value.Model.RawModel);
        Assert.IsNull(value.Model.ProviderId);
        Assert.IsNull(value.ProjectId);
        Assert.IsNull(value.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_CwdUsesPresentMissingInvalidSemanticsAcrossMetadataKinds()
    {
        const string safeCwd = @"C:\fixture\safe-cwd";
        const string privateInvalidCwd = "bad\u0001cwd";
        var state = new CodexParseState(
            ThreadId: "cwd-thread",
            ProjectId: ExpectedProjectId(@"C:\fixture\old-cwd"),
            ProjectPath: ExpectedProjectPath(@"C:\fixture\old-cwd"));
        CodexParseResult invalidSession = ParseSingle(
            "{\"timestamp\":\"2026-07-16T08:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cwd-thread\",\"cwd\":null}}",
            state,
            1);
        CodexParseResult validTurn = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:00:01Z",
                type = "turn_context",
                payload = new { cwd = safeCwd }
            }),
            invalidSession.State,
            2);
        CodexParseResult invalidTurn = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:00:02Z",
                type = "turn_context",
                payload = new { cwd = privateInvalidCwd }
            }),
            validTurn.State,
            3);
        CodexParseResult token = ParseSingle(
            "{\"timestamp\":\"2026-07-16T08:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}",
            invalidTurn.State,
            4);

        Assert.AreEqual("codex.invalid_state_metadata", invalidSession.Diagnostic!.Code);
        Assert.IsNull(invalidSession.State.ProjectId);
        Assert.IsNull(invalidSession.State.ProjectPath);
        Assert.IsNull(validTurn.Diagnostic);
        Assert.AreEqual(ExpectedProjectId(safeCwd), validTurn.State.ProjectId);
        Assert.AreEqual(ExpectedProjectPath(safeCwd), validTurn.State.ProjectPath);
        Assert.AreEqual("codex.invalid_state_metadata", invalidTurn.Diagnostic!.Code);
        Assert.AreEqual(
            "Codex state metadata contained an invalid value and was cleared.",
            invalidTurn.Diagnostic.Message);
        Assert.DoesNotContain(privateInvalidCwd, invalidTurn.Diagnostic.Message);
        Assert.IsNull(invalidTurn.State.ProjectId);
        Assert.IsNull(invalidTurn.State.ProjectPath);
        Assert.IsNull(token.Event!.ProjectId);
        Assert.IsNull(token.Event.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_ProjectPathUsesFullDirectoryWithoutCollapsingNestedProjects()
    {
        const string rootCwd = @"D:\Repo";
        const string caseVariantCwd = @"d:\repo";
        const string nestedCwd = @"D:\Repo\frontend\";

        CodexParseResult root = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:00Z",
                type = "session_meta",
                payload = new { id = "root-thread", cwd = rootCwd }
            }),
            new CodexParseState(),
            1);
        CodexParseResult caseVariant = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:01Z",
                type = "session_meta",
                payload = new { id = "case-thread", cwd = caseVariantCwd }
            }),
            new CodexParseState(),
            1);
        CodexParseResult nested = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:02Z",
                type = "session_meta",
                payload = new { id = "nested-thread", cwd = nestedCwd }
            }),
            new CodexParseState(),
            1);

        Assert.AreEqual(ExpectedProjectPath(rootCwd), root.State.ProjectPath);
        Assert.AreEqual(
            ExpectedProjectPath(caseVariantCwd),
            caseVariant.State.ProjectPath);
        Assert.AreEqual(ExpectedProjectPath(nestedCwd), nested.State.ProjectPath);
        Assert.AreEqual(root.State.ProjectId, caseVariant.State.ProjectId);
        Assert.AreNotEqual(root.State.ProjectId, nested.State.ProjectId);
    }

    [TestMethod]
    public void ParseLine_ReliableRepositoryIdentityMergesMainAndWorktreePaths()
    {
        CodexParseResult main = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:10Z",
                type = "session_meta",
                payload = new
                {
                    id = "main-thread",
                    cwd = @"C:\Projects\AgenTally",
                    git = new
                    {
                        repository_url =
                            "https://github.com/example/AgenTally.git"
                    }
                }
            }),
            new CodexParseState(),
            1);
        CodexParseResult worktree = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:11Z",
                type = "session_meta",
                payload = new
                {
                    id = "worktree-thread",
                    cwd =
                        @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally",
                    git = new
                    {
                        repository_url =
                            "git@github.com:example/AgenTally.git"
                    }
                }
            }),
            new CodexParseState(),
            1);
        CodexParseResult worktreeTurn = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:12Z",
                type = "turn_context",
                payload = new
                {
                    turn_id = "worktree-turn",
                    cwd =
                        @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally"
                }
            }),
            main.State,
            2);
        CodexParseResult worktreeToken = ParseSingle(
            "{\"timestamp\":\"2026-07-16T08:01:13Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}",
            worktreeTurn.State,
            3);

        Assert.IsNull(main.Diagnostic);
        Assert.IsNull(worktree.Diagnostic);
        Assert.IsNotNull(main.State.ProjectRepositoryIdentityHash);
        Assert.AreEqual(main.State.ProjectId, worktree.State.ProjectId);
        Assert.AreEqual(
            main.State.ProjectRepositoryIdentityHash,
            worktree.State.ProjectRepositoryIdentityHash);
        Assert.AreEqual(main.State.ProjectId, worktreeTurn.State.ProjectId);
        Assert.AreEqual(
            @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally",
            worktreeTurn.State.ProjectPath);
        Assert.AreEqual(
            main.State.ProjectRepositoryIdentityHash,
            worktreeTurn.State.ProjectRepositoryIdentityHash);
        Assert.AreEqual(
            main.State.ProjectRepositoryIdentityHash,
            main.SessionMetadata!.ProjectRepositoryIdentityHash);
        Assert.AreEqual(
            main.State.ProjectRepositoryIdentityHash,
            worktreeToken.Event!.ProjectRepositoryIdentityHash);
    }

    [TestMethod]
    public void ParseLine_AcceptsFullyQualifiedDriveAndUncProjectPaths()
    {
        const string driveCwd = @"C:\fixture\drive-project";
        const string uncCwd = @"\\fixture-server\fixture-share\unc-project";
        Assert.IsTrue(Path.IsPathFullyQualified(driveCwd));
        Assert.IsTrue(Path.IsPathFullyQualified(uncCwd));

        foreach (string cwd in new[] { driveCwd, uncCwd })
        {
            CodexParseResult result = ParseSingle(
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-07-16T08:01:30Z",
                    type = "session_meta",
                    payload = new { id = "absolute-thread", cwd }
                }),
                new CodexParseState(),
                1);

            Assert.IsNull(result.Diagnostic);
            Assert.AreEqual(ExpectedProjectPath(cwd), result.State.ProjectPath);
            Assert.AreEqual(ExpectedProjectId(cwd), result.State.ProjectId);
        }
    }

    [TestMethod]
    public void ParseLine_RejectsRelativeAndDriveRelativeProjectPaths()
    {
        string[] invalidCwds =
        [
            @"private-relative\project",
            @"D:private-drive-relative\project"
        ];

        foreach (string privateCwd in invalidCwds)
        {
            Assert.IsFalse(Path.IsPathFullyQualified(privateCwd));
            CodexParseResult result = ParseSingle(
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-07-16T08:01:31Z",
                    type = "session_meta",
                    payload = new { id = "relative-thread", cwd = privateCwd }
                }),
                new CodexParseState(),
                1);

            Assert.IsNotNull(result.Diagnostic);
            Assert.AreEqual("codex.invalid_state_metadata", result.Diagnostic.Code);
            Assert.DoesNotContain(privateCwd, result.Diagnostic.Message);
            Assert.IsNull(result.State.ProjectId);
            Assert.IsNull(result.State.ProjectPath);
        }
    }

    [TestMethod]
    public void ParseLine_PreservesProjectRootsAndTrimsNonRootSeparators()
    {
        const string driveRoot = @"C:\";
        const string uncRoot = @"\\fixture-server\fixture-share\";
        const string directoryWithSeparator = @"C:\fixture\directory\";

        foreach (string root in new[] { driveRoot, uncRoot })
        {
            CodexParseResult result = ParseSingle(
                JsonSerializer.Serialize(new
                {
                    timestamp = "2026-07-16T08:01:32Z",
                    type = "session_meta",
                    payload = new { id = "root-thread", cwd = root }
                }),
                new CodexParseState(),
                1);

            Assert.IsNull(result.Diagnostic);
            Assert.AreEqual(Path.GetFullPath(root), result.State.ProjectPath);
        }

        CodexParseResult directory = ParseSingle(
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T08:01:33Z",
                type = "turn_context",
                payload = new { cwd = directoryWithSeparator }
            }),
            new CodexParseState(),
            1);

        Assert.IsNull(directory.Diagnostic);
        Assert.AreEqual(@"C:\fixture\directory", directory.State.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_ProjectPathDoesNotChangeCanonicalDedupKey()
    {
        string[] lines = CanonicalCallLines("turn-a");
        lines[0] =
            "{\"timestamp\":\"2026-07-16T01:20:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"canonical-thread\",\"cwd\":\"C:\\\\fixture\\\\dedup-project\"}}";

        UsageEvent value = Assert.ContainsSingle(Parse(lines)
            .Where(static result => result.Event is not null)
            .Select(static result => result.Event!));
        UsageEvent withoutPath = Assert.ContainsSingle(
            Parse(CanonicalCallLines("turn-a"))
                .Where(static result => result.Event is not null)
                .Select(static result => result.Event!));

        Assert.AreEqual(withoutPath.DedupKey, value.DedupKey);
        Assert.AreEqual(ExpectedProjectPath(@"C:\fixture\dedup-project"), value.ProjectPath);
    }

    [TestMethod]
    public void ParseLine_MissingCwdDoesNotInventProjectIdentity()
    {
        CodexParseResult metadata = ParseSingle(
            "{\"timestamp\":\"2026-07-16T08:02:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"missing-cwd-thread\"}}",
            new CodexParseState(),
            1);
        CodexParseResult token = ParseSingle(
            "{\"timestamp\":\"2026-07-16T08:02:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}",
            metadata.State,
            2);

        Assert.IsNull(metadata.State.ProjectId);
        Assert.IsNull(metadata.State.ProjectPath);
        Assert.IsNull(token.Event!.ProjectId);
        Assert.IsNull(token.Event.ProjectPath);
    }

    [TestMethod]
    public void CodexCursor_IsJsonSerializableWithoutSourceContent()
    {
        var cursor = new CodexCursor(
            new JsonlCursor(12, string.Empty, 1, "fingerprint"),
            new CodexParseState(
                ThreadId: "thread-serial",
                ParentSessionId: null,
                CurrentRawModel: "provider/model-20260701",
                CurrentProviderId: "provider",
                ProjectId: "0123456789abcdef01234567",
                PreviousCumulative: new CodexTokenCounters(5, 2, 3, 1, 0, 8),
                TokenEventIndex: 4,
                IsHistoryReplay: false));

        string json = JsonSerializer.Serialize(cursor);
        CodexCursor? restored = JsonSerializer.Deserialize<CodexCursor>(json);

        Assert.IsNotNull(restored);
        Assert.AreEqual(cursor, restored);
        Assert.DoesNotContain("fixture", json);
    }

    [TestMethod]
    public void ParseLine_HashesTurnIdentityAndCursorRoundTripsPendingLineState()
    {
        const string rawTurnId = "private-turn-identity";
        string turn = JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T08:10:01.1234567+08:00",
            type = "turn_context",
            payload = new
            {
                turn_id = rawTurnId,
                model = "gpt-private",
                effort = "high"
            }
        });
        CodexParseResult parsed = ParseSingle(turn, new CodexParseState(), 1);
        byte[] pending = Encoding.UTF8.GetBytes("{\"partial\":");
        var cursor = new CodexCursor(
            new JsonlCursor(
                pending.LongLength + 1,
                Convert.ToBase64String(pending),
                1,
                new string('a', 64)),
            parsed.State);

        string json = cursor.Serialize();
        CodexCursor restored = CodexCursor.DeserializeOrStart(json, out var diagnostic);

        Assert.IsNull(diagnostic);
        Assert.AreEqual(cursor, restored);
        Assert.AreEqual(Sha256(rawTurnId), restored.State.CurrentTurnIdHash);
        Assert.AreEqual("high", restored.State.CurrentEffort);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 0, 10, 1, 123, TimeSpan.Zero).AddTicks(4567),
            restored.State.CurrentTurnTimestampUtc);
        Assert.DoesNotContain(rawTurnId, json);
    }

    [TestMethod]
    public void CodexCursor_RoundTripsMatchingProjectIdentity()
    {
        const string cwd = @"C:\fixture\cursor-project";
        var cursor = new CodexCursor(
            new JsonlCursor(1, string.Empty, 1, new string('a', 64)),
            new CodexParseState(
                ThreadId: "cursor-thread",
                ProjectId: ExpectedProjectId(cwd),
                ProjectPath: ExpectedProjectPath(cwd),
                TokenEventIndex: 1));

        string json = cursor.Serialize();
        CodexCursor restored = CodexCursor.DeserializeOrStart(
            json,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);

        Assert.IsNull(diagnostic);
        Assert.AreEqual(cursor, restored);
    }

    [TestMethod]
    public void CodexCursor_ResetsMismatchedOrNoncanonicalProjectIdentity()
    {
        const string privateMismatchedPath = @"C:\private\mismatched-project";
        const string privateNoncanonicalPath = @"C:\private\normalized-project\";
        var jsonl = new JsonlCursor(1, string.Empty, 1, new string('a', 64));
        string mismatchedJson = SerializeCursorUnchecked(new CodexCursor(
            jsonl,
            new CodexParseState(
                ThreadId: "mismatched-thread",
                ProjectId: ExpectedProjectId(@"C:\different\project"),
                ProjectPath: privateMismatchedPath,
                TokenEventIndex: 1)));
        string noncanonicalJson = SerializeCursorUnchecked(new CodexCursor(
            jsonl,
            new CodexParseState(
                ThreadId: "noncanonical-thread",
                ProjectId: ExpectedProjectId(privateNoncanonicalPath),
                ProjectPath: privateNoncanonicalPath,
                TokenEventIndex: 1)));

        foreach ((string json, string privatePath) in new[]
                 {
                     (mismatchedJson, privateMismatchedPath),
                     (noncanonicalJson, privateNoncanonicalPath)
                 })
        {
            CodexCursor restored = CodexCursor.DeserializeOrStart(
                json,
                hasStoredCursor: true,
                out CollectorDiagnostic? diagnostic);

            Assert.AreEqual(CodexCursor.Start, restored);
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual("codex.invalid_cursor", diagnostic.Code);
            Assert.DoesNotContain(privatePath, diagnostic.Message);
        }
    }

    [TestMethod]
    public void CodexCursor_RejectsInvalidOrOversizedProjectPathsPrivately()
    {
        const string privateRelativePath = @"private-cursor\relative-project";
        const string privateControlPath = "C:\\private\\control\u0001project";
        string privateOversizedPath = @"C:\" + new string('p', 32768);
        var jsonl = new JsonlCursor(1, string.Empty, 1, new string('a', 64));
        string[] invalidPaths =
        [
            privateRelativePath,
            privateControlPath,
            privateOversizedPath
        ];

        foreach (string privatePath in invalidPaths)
        {
            var cursor = new CodexCursor(
                jsonl,
                new CodexParseState(
                    ThreadId: "invalid-path-thread",
                    ProjectId: "0123456789abcdef01234567",
                    ProjectPath: privatePath,
                    TokenEventIndex: 1));

            Assert.Throws<InvalidOperationException>(() => cursor.Serialize());
            string json = SerializeCursorUnchecked(cursor);
            CodexCursor restored = CodexCursor.DeserializeOrStart(
                json,
                hasStoredCursor: true,
                out CollectorDiagnostic? diagnostic);

            Assert.AreEqual(CodexCursor.Start, restored);
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual("codex.invalid_cursor", diagnostic.Code);
            Assert.DoesNotContain(privatePath, diagnostic.Message);
        }

        var field = typeof(CodexCursor).GetField(
            "MaxSerializedCursorCharacters",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(field);
        int maxSerializedCharacters = (int)field.GetRawConstantValue()!;
        string oversizedCursor = new('x', maxSerializedCharacters + 1);

        CodexCursor oversized = CodexCursor.DeserializeOrStart(
            oversizedCursor,
            hasStoredCursor: true,
            out CollectorDiagnostic? oversizedDiagnostic);

        Assert.AreEqual(CodexCursor.Start, oversized);
        Assert.IsNotNull(oversizedDiagnostic);
        Assert.AreEqual("codex.invalid_cursor", oversizedDiagnostic.Code);
    }

    [TestMethod]
    public void CodexCursor_ReadsLegacyProjectIdWithoutInventingProjectPath()
    {
        const string legacyProjectId = "0123456789abcdef01234567";
        var cursor = new CodexCursor(
            new JsonlCursor(1, string.Empty, 1, new string('a', 64)),
            new CodexParseState(
                ThreadId: "legacy-project-thread",
                ProjectId: legacyProjectId,
                TokenEventIndex: 1));

        string json = cursor.Serialize();
        CodexCursor restored = CodexCursor.DeserializeOrStart(
            json,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);

        Assert.IsNull(diagnostic);
        Assert.AreEqual(legacyProjectId, restored.State.ProjectId);
        Assert.IsNull(restored.State.ProjectPath);
    }

    [TestMethod]
    public void CodexCursor_RejectsInvalidCanonicalIdentityState()
    {
        object jsonl = new
        {
            byteOffset = 1,
            pendingBase64 = string.Empty,
            lineNumber = 1,
            sourceFingerprint = new string('b', 64)
        };
        string invalidHash = JsonSerializer.Serialize(new
        {
            jsonl,
            state = new { currentTurnIdHash = "raw-turn-id" }
        });
        string invalidOffset = JsonSerializer.Serialize(new
        {
            jsonl,
            state = new
            {
                currentTurnIdHash = new string('c', 64),
                currentTurnTimestampUtc = new DateTimeOffset(
                    2026,
                    7,
                    16,
                    8,
                    0,
                    0,
                    TimeSpan.FromHours(8))
            }
        });
        string invalidEffort = JsonSerializer.Serialize(new
        {
            jsonl,
            state = new { currentEffort = new string('e', 1025) }
        });

        foreach (string json in new[] { invalidHash, invalidOffset, invalidEffort })
        {
            CodexCursor restored = CodexCursor.DeserializeOrStart(json, out var diagnostic);

            Assert.AreEqual(CodexCursor.Start, restored);
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual("codex.invalid_cursor", diagnostic.Code);
        }
    }

    private static IReadOnlyList<CodexParseResult> ParseFixture(string fileName) =>
        Parse(File.ReadAllLines(Path.Combine(FixtureDirectory, fileName)));

    private static IReadOnlyList<CodexParseResult> Parse(
        IReadOnlyList<string> lines,
        CodexEventContext? context = null)
    {
        var parser = new CodexRolloutParser();
        CodexParseState state = new();
        var results = new List<CodexParseResult>(lines.Count);
        long byteOffset = 0;
        CodexEventContext eventContext = context ?? Context;

        for (int index = 0; index < lines.Count; index++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(lines[index]);
            var line = new JsonlLine(index + 1, byteOffset, utf8);
            CodexParseResult result = parser.ParseLine(line, state, eventContext);
            results.Add(result);
            state = result.State;
            byteOffset += utf8.LongLength + 1;
        }

        return results;
    }

    private static CodexParseResult ParseSingle(
        string json,
        CodexParseState state,
        long lineNumber)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        return new CodexRolloutParser().ParseLine(
            new JsonlLine(lineNumber, lineNumber - 1, utf8),
            state,
            Context);
    }

    private static string[] CanonicalCallLines(string turnId) =>
    [
        "{\"timestamp\":\"2026-07-16T01:20:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"canonical-thread\"}}",
        JsonSerializer.Serialize(new
        {
            timestamp = "2026-07-16T01:20:01Z",
            type = "turn_context",
            payload = new { turn_id = turnId, model = "gpt-test", effort = "medium" }
        }),
        "{\"timestamp\":\"2026-07-16T01:20:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12},\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":4,\"output_tokens\":2,\"total_tokens\":12}}}}"
    ];

    private static void AssertCanonicalDedupKey(UsageEvent value)
    {
        Assert.AreEqual(64, value.DedupKey.Length);
        Assert.IsTrue(value.DedupKey.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string SerializeCursorUnchecked(CodexCursor cursor) =>
        JsonSerializer.Serialize(
            cursor,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

    private static string ExpectedProjectId(string cwd)
    {
        string normalized = ExpectedProjectPath(cwd).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            [..24]
            .ToLowerInvariant();
    }

    private static string ExpectedProjectPath(string cwd)
    {
        string normalized = Path.GetFullPath(cwd);
        string? root = Path.GetPathRoot(normalized);
        return root is not null && normalized.Length > root.Length
            ? normalized.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            : normalized;
    }

    private static string EventIdentity(
        long tokenIndex,
        CodexEventContext? context = null) =>
        $"{(context ?? Context).Entity.SourceEntityId}:token:{tokenIndex}";

    private static CodexEventContext CreateContext(string sourceEntityId) => new(
        Context.Instance,
        new SourceEntityDescriptor(
            Context.Instance.SourceInstanceId,
            sourceEntityId,
            "C:\\fixture\\codex\\rollout.jsonl"),
        Context.SourceFingerprint,
        Context.ImportedAtUtc);

    private static string FixtureDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Fixtures",
        "Codex"));

    private static CodexEventContext Context { get; } = new(
        new SourceInstanceDescriptor(
            "codex:windows:test",
            "codex",
            SourceKind.Jsonl,
            "Codex test",
            "C:\\fixture\\codex"),
        new SourceEntityDescriptor(
            "codex:windows:test",
            "codex:rollout:test",
            "C:\\fixture\\codex\\rollout.jsonl"),
        "fixture-fingerprint",
        new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
}
