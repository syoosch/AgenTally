using AgenTally.UI;
using AgenTally.UI.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class BackgroundStartupTests
{
    [TestMethod]
    public void ResolveStartupMode_AcceptsOnlyInteractiveOrExactBackground()
    {
        Assert.AreEqual(
            UiStartupMode.Interactive,
            App.ResolveStartupMode(Array.Empty<string>()));
        Assert.AreEqual(
            UiStartupMode.Background,
            App.ResolveStartupMode(["--background"]));
        Assert.Throws<InvalidOperationException>(() =>
            App.ResolveStartupMode(["--BACKGROUND"]));
        Assert.Throws<InvalidOperationException>(() =>
            App.ResolveStartupMode(["--background", "extra"]));
    }

    [TestMethod]
    public async Task BackgroundStartup_EnsuresCoreOnceAndReturnsSuccess()
    {
        var controller = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.Ready,
                string.Empty,
                IsError: false,
                CanRetry: false));

        int exitCode = await App.RunBackgroundStartupAsync(
            controller,
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, controller.EnsureCount);
    }

    [TestMethod]
    public async Task BackgroundStartup_CoreFailureReturnsNonzero()
    {
        var controller = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.MissingCore,
                "missing",
                IsError: true,
                CanRetry: false));

        int exitCode = await App.RunBackgroundStartupAsync(
            controller,
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(1, controller.EnsureCount);
    }

    [TestMethod]
    public async Task BackgroundStartup_TimeoutReturnsNonzero()
    {
        var controller = new FakeCoreRuntimeController(status: null);

        int exitCode = await App.RunBackgroundStartupAsync(
            controller,
            TimeSpan.FromMilliseconds(25));

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(1, controller.EnsureCount);
    }

    private sealed class FakeCoreRuntimeController : ICoreRuntimeController
    {
        private readonly CoreRuntimeUiStatus? _status;

        public FakeCoreRuntimeController(CoreRuntimeUiStatus? status)
        {
            _status = status;
        }

        public int EnsureCount { get; private set; }

        public async Task<CoreRuntimeUiStatus> EnsureAsync(
            CancellationToken cancellationToken)
        {
            EnsureCount++;
            if (_status is not null)
            {
                return _status;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<CoreRuntimeUiStatus> ReadStatusAsync(
            CancellationToken cancellationToken) =>
            EnsureAsync(cancellationToken);

        public Task<CoreRuntimeUiStatus> RebuildCodexAsync(
            CancellationToken cancellationToken) =>
            EnsureAsync(cancellationToken);

        public Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
            CancellationToken cancellationToken) =>
            EnsureAsync(cancellationToken);
    }
}
