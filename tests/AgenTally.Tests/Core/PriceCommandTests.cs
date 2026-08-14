using System.IO;
using System.IO.Pipes;
using AgenTally.Core.Hosting;
using AgenTally.Storage.Pricing;
using AgenTally.UI.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class PriceCommandTests
{
    [TestMethod]
    public async Task Handler_ExecutesEverySupportedCommandAndReturnsStableCodes()
    {
        var ledger = new FakePriceLedger
        {
            SetResult = 3,
            RestoreResult = 2,
            RestoreAllResult = 1
        };
        using var gate = new CoreDatabaseWriteGate();
        var handler = new PriceCommandHandler(ledger, gate);
        ModelPriceRate rate = Rate("private-model");

        PriceCommandResponse set = await handler.HandleAsync(
            PriceCommandRequest.SetOverride(rate),
            CancellationToken.None);
        PriceCommandResponse restore = await handler.HandleAsync(
            PriceCommandRequest.RestoreDefault("private-model"),
            CancellationToken.None);
        PriceCommandResponse restoreAll = await handler.HandleAsync(
            PriceCommandRequest.RestoreAllDefaults(),
            CancellationToken.None);

        Assert.AreEqual(PriceCommandResultCode.Success, set.Result);
        Assert.AreEqual(PriceCommandMessageCodes.PriceUpdated, set.MessageCode);
        Assert.AreEqual(3, set.NewlyPricedRecords);
        Assert.AreEqual("private-model", ledger.LastSetRate?.NormalizedModel);
        Assert.AreEqual(
            PriceCommandMessageCodes.PriceDefaultRestored,
            restore.MessageCode);
        Assert.AreEqual(2, restore.NewlyPricedRecords);
        Assert.AreEqual("private-model", ledger.LastRestoredModel);
        Assert.AreEqual(
            PriceCommandMessageCodes.AllPriceDefaultsRestored,
            restoreAll.MessageCode);
        Assert.AreEqual(1, restoreAll.NewlyPricedRecords);
        Assert.AreEqual(1, ledger.RestoreAllCalls);
    }

    [TestMethod]
    public async Task Handler_ReturnsBusyWithoutCallingLedgerWhenCollectionOwnsGate()
    {
        var ledger = new FakePriceLedger();
        using var gate = new CoreDatabaseWriteGate();
        using IDisposable collectionLease =
            await gate.EnterAsync(CancellationToken.None);
        var handler = new PriceCommandHandler(ledger, gate);

        PriceCommandResponse response = await handler.HandleAsync(
            PriceCommandRequest.SetOverride(Rate("private-model")),
            CancellationToken.None);

        Assert.AreEqual(PriceCommandResultCode.Busy, response.Result);
        Assert.AreEqual(PriceCommandMessageCodes.Busy, response.MessageCode);
        Assert.IsNull(ledger.LastSetRate);
    }

    [TestMethod]
    public async Task Handler_ReturnsBusyWhileAutomaticMaintenanceBlocksPricing()
    {
        var ledger = new FakePriceLedger();
        using var gate = new CoreDatabaseWriteGate();
        using IDisposable maintenance = gate.BlockPricing();
        var handler = new PriceCommandHandler(ledger, gate);

        PriceCommandResponse response = await handler.HandleAsync(
            PriceCommandRequest.SetOverride(Rate("private-model")),
            CancellationToken.None);

        Assert.AreEqual(PriceCommandResultCode.Busy, response.Result);
        Assert.AreEqual(PriceCommandMessageCodes.Busy, response.MessageCode);
        Assert.IsNull(ledger.LastSetRate);
    }

    [TestMethod]
    public async Task Handler_RejectsUnsupportedProtocolAndInvalidPayload()
    {
        var ledger = new FakePriceLedger();
        using var gate = new CoreDatabaseWriteGate();
        var handler = new PriceCommandHandler(ledger, gate);
        PriceCommandRequest valid =
            PriceCommandRequest.SetOverride(Rate("private-model"));

        PriceCommandResponse unsupported = await handler.HandleAsync(
            valid with
            {
                ProtocolVersion = PriceCommandProtocol.CurrentVersion + 1
            },
            CancellationToken.None);
        PriceCommandResponse invalid = await handler.HandleAsync(
            valid with
            {
                Rate = null
            },
            CancellationToken.None);

        Assert.AreEqual(
            PriceCommandResultCode.UnsupportedProtocol,
            unsupported.Result);
        Assert.AreEqual(
            PriceCommandMessageCodes.UnsupportedProtocol,
            unsupported.MessageCode);
        Assert.AreEqual(PriceCommandResultCode.InvalidRequest, invalid.Result);
        Assert.AreEqual(
            PriceCommandMessageCodes.InvalidRequest,
            invalid.MessageCode);
        Assert.IsNull(ledger.LastSetRate);
    }

    [TestMethod]
    public async Task NamedPipe_RoundTripsOneProfileScopedCommand()
    {
        string pipeName = $"AgenTally.Tests.Price.{Guid.NewGuid():N}";
        var ledger = new FakePriceLedger
        {
            SetResult = 4
        };
        using var gate = new CoreDatabaseWriteGate();
        var server = new PriceCommandServer(
            pipeName,
            new PriceCommandHandler(ledger, gate));
        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        var client = new NamedPipePriceCommandClient(pipeName);
        PriceCommandRequest request =
            PriceCommandRequest.SetOverride(Rate("private-model"));

        try
        {
            PriceCommandResponse response = await client.SendAsync(
                request,
                CancellationToken.None);

            Assert.AreEqual(request.RequestId, response.RequestId);
            Assert.AreEqual(PriceCommandResultCode.Success, response.Result);
            Assert.AreEqual(4, response.NewlyPricedRecords);
        }
        finally
        {
            cancellation.Cancel();
            await AssertCanceledAsync(serverTask);
        }
    }

    [TestMethod]
    public async Task NamedPipe_ClientDisconnectDoesNotCancelValidatedDatabaseCommand()
    {
        string pipeName = $"AgenTally.Tests.Price.{Guid.NewGuid():N}";
        var ledger = new FakePriceLedger
        {
            BlockSet = true
        };
        using var gate = new CoreDatabaseWriteGate();
        var server = new PriceCommandServer(
            pipeName,
            new PriceCommandHandler(ledger, gate));
        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        var request = PriceCommandRequest.SetOverride(Rate("private-model"));

        try
        {
            await using (var pipe = new NamedPipeClientStream(
                             ".",
                             pipeName,
                             PipeDirection.InOut,
                             PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(CancellationToken.None);
                await PriceCommandProtocol.WriteAsync(
                    pipe,
                    request,
                    CancellationToken.None);
                await ledger.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            ledger.ReleaseSet.TrySetResult();
            await ledger.SetCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("private-model", ledger.LastSetRate?.NormalizedModel);
        }
        finally
        {
            ledger.ReleaseSet.TrySetResult();
            cancellation.Cancel();
            await AssertCanceledAsync(serverTask);
        }
    }

    [TestMethod]
    public async Task Protocol_RejectsFramesLargerThanSixteenKiB()
    {
        await using var stream = new MemoryStream();
        var response = new PriceCommandResponse(
            PriceCommandProtocol.CurrentVersion,
            Guid.NewGuid().ToString("D"),
            PriceCommandResultCode.Failed,
            new string('x', PriceCommandProtocol.MaxFrameBytes),
            0);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PriceCommandProtocol.WriteAsync(
                stream,
                response,
                CancellationToken.None));
    }

    private static ModelPriceRate Rate(string model) => new(
        model,
        2m,
        0.2m,
        null,
        8m,
        100_000,
        2m,
        1.5m);

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("Named-pipe server should stop through cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakePriceLedger : IPriceLedger
    {
        public int SetResult { get; init; }

        public int RestoreResult { get; init; }

        public int RestoreAllResult { get; init; }

        public bool BlockSet { get; init; }

        public ModelPriceRate? LastSetRate { get; private set; }

        public string? LastRestoredModel { get; private set; }

        public int RestoreAllCalls { get; private set; }

        public TaskCompletionSource SetStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSet { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SetCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ResolvedPriceRule> GetBuiltInCatalog() => [];

        public Task<IReadOnlyList<CustomPriceSetting>> GetCustomPricesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomPriceSetting>>([]);

        public async Task<int> SetCustomPriceAsync(
            ModelPriceRate rate,
            CancellationToken cancellationToken)
        {
            LastSetRate = rate;
            SetStarted.TrySetResult();
            if (BlockSet)
            {
                await ReleaseSet.Task.WaitAsync(cancellationToken);
            }

            SetCompleted.TrySetResult();
            return SetResult;
        }

        public Task<int> RestoreDefaultAsync(
            string normalizedModel,
            CancellationToken cancellationToken)
        {
            LastRestoredModel = normalizedModel;
            return Task.FromResult(RestoreResult);
        }

        public Task<int> RestoreAllDefaultsAsync(
            CancellationToken cancellationToken)
        {
            RestoreAllCalls++;
            return Task.FromResult(RestoreAllResult);
        }
    }
}
