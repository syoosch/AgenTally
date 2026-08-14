using AgenTally.Core.Collectors;
using AgenTally.Core.Monitoring;
using AgenTally.Domain.Sources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CollectionRequestQueueTests
{
    [TestMethod]
    public async Task EnqueueAsync_CoalescesPendingEntityAndAllowsOneFollowUpAfterDequeue()
    {
        var queue = new CollectionRequestQueue();
        CollectionRequest request = Request("entity-a", CollectionReason.StartupImport);

        await queue.EnqueueAsync(request, CancellationToken.None);
        await queue.EnqueueAsync(
            request with { Reason = CollectionReason.FileChanged },
            CancellationToken.None);

        CollectionRequest dequeued = await queue.DequeueAsync(CancellationToken.None);
        Assert.AreEqual(request.Entity.SourceEntityId, dequeued.Entity.SourceEntityId);
        Assert.AreEqual(CollectionReason.StartupImport, dequeued.Reason);
        Assert.IsFalse(queue.TryDequeue(out _));

        await queue.EnqueueAsync(
            request with { Reason = CollectionReason.FileChanged },
            CancellationToken.None);
        Assert.IsTrue(queue.TryDequeue(out CollectionRequest? followUp));
        Assert.AreEqual(CollectionReason.FileChanged, followUp.Reason);
    }

    [TestMethod]
    public async Task EnqueueAsync_WaitsWhenAll256SlotsAreOccupied()
    {
        var queue = new CollectionRequestQueue();
        for (int index = 0; index < CollectionRequestQueue.Capacity; index++)
        {
            await queue.EnqueueAsync(
                Request($"entity-{index}", CollectionReason.PeriodicAudit),
                CancellationToken.None);
        }

        ValueTask blockedWrite = queue.EnqueueAsync(
            Request("entity-overflow", CollectionReason.PeriodicAudit),
            CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(blockedWrite.IsCompleted);

        Assert.IsTrue(queue.TryDequeue(out _));
        await blockedWrite.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task EnqueueAsync_CancelledFullWriteRemovesPendingKeyForRetry()
    {
        var queue = new CollectionRequestQueue();
        for (int index = 0; index < CollectionRequestQueue.Capacity; index++)
        {
            await queue.EnqueueAsync(
                Request($"occupied-{index}", CollectionReason.PeriodicAudit),
                CancellationToken.None);
        }

        CollectionRequest retry = Request("retry-after-cancel", CollectionReason.FileChanged);
        using var cancellation = new CancellationTokenSource();
        ValueTask cancelledWrite = queue.EnqueueAsync(retry, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await cancelledWrite.AsTask());

        Assert.IsTrue(queue.TryDequeue(out _));
        await queue.EnqueueAsync(retry, CancellationToken.None);

        bool found = false;
        while (queue.TryDequeue(out CollectionRequest? request))
        {
            found |= request.Entity.SourceEntityId == retry.Entity.SourceEntityId;
        }

        Assert.IsTrue(found);
    }

    private static CollectionRequest Request(
        string entityId,
        CollectionReason reason)
    {
        var instance = new SourceInstanceDescriptor(
            "codex:windows:test",
            "codex",
            SourceKind.Jsonl,
            "Codex test",
            @"C:\fixture\.codex");
        var entity = new SourceEntityDescriptor(
            instance.SourceInstanceId,
            entityId,
            $@"C:\fixture\.codex\sessions\{entityId}.jsonl");
        return new CollectionRequest(instance, entity, null, reason);
    }
}
