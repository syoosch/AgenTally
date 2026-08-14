using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using AgenTally.UI.Updates;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class SettingsVersionCheckTests
{
    private static readonly Uri ReleasePageUri =
        new("https://releases.invalid/agentally");

    [TestMethod]
    public async Task DevelopmentManualCheckShowsPolicyWithoutReleasePage()
    {
        await using var host = new StaDispatcherTestHost();
        var checker = new FakeManualVersionCheckCoordinator(
            new ManualVersionCheckResult(
                ManualVersionCheckStatus.DevelopmentDisabled));
        var launcher = new FakeReleasePageLauncher();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Development,
            checker,
            launcher);

        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());

        Assert.AreEqual(1, checker.Calls);
        StringAssert.Contains(
            viewModel.VersionCheckStatusText,
            "Development 不连接");
        StringAssert.Contains(
            viewModel.NetworkAccessDescription,
            "不访问真实版本渠道");
        Assert.IsFalse(viewModel.VersionCheckStatusIsError);
        Assert.IsFalse(viewModel.CanOpenReleasePage);
        Assert.AreEqual(0, launcher.Calls);
    }

    [TestMethod]
    public async Task StablePrivacyBoundaryUsesProductionWording()
    {
        await using var host = new StaDispatcherTestHost();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Stable,
            new FakeManualVersionCheckCoordinator(
                new ManualVersionCheckResult(
                    ManualVersionCheckStatus.StableChannelNotConfigured)),
            new FakeReleasePageLauncher());

        Assert.AreEqual(
            "正常采集、统计和价格零外联；仅版本检查可访问配置的正式发布渠道。",
            viewModel.NetworkAccessDescription);
        Assert.IsFalse(viewModel.NetworkAccessDescription.Contains(
            "Development",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UpToDateAndFailureStatesAreExplicit()
    {
        await using var host = new StaDispatcherTestHost();
        var checker = new FakeManualVersionCheckCoordinator(
            new ManualVersionCheckResult(
                ManualVersionCheckStatus.UpToDate,
                new ReleaseVersion(1, 2, 3),
                new ReleaseVersion(1, 2, 3)));
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Stable,
            checker,
            new FakeReleasePageLauncher());

        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());
        Assert.AreEqual("已是最新版（1.2.3）。", viewModel.VersionCheckStatusText);
        Assert.IsFalse(viewModel.VersionCheckStatusIsError);

        checker.Result = new ManualVersionCheckResult(
            ManualVersionCheckStatus.NetworkFailure);
        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());
        StringAssert.Contains(viewModel.VersionCheckStatusText, "请确认网络");
        Assert.IsTrue(viewModel.VersionCheckStatusIsError);

        checker.Result = new ManualVersionCheckResult(
            ManualVersionCheckStatus.StableChannelNotConfigured);
        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());
        StringAssert.Contains(viewModel.VersionCheckStatusText, "尚未配置");
        StringAssert.Contains(viewModel.VersionCheckStatusText, "未联网");
        Assert.IsFalse(viewModel.VersionCheckStatusIsError);
    }

    [TestMethod]
    public async Task AvailableUpdateExposesOnlyValidatedReleasePage()
    {
        await using var host = new StaDispatcherTestHost();
        var checker = new FakeManualVersionCheckCoordinator(
            new ManualVersionCheckResult(
                ManualVersionCheckStatus.UpdateAvailable,
                new ReleaseVersion(1, 2, 3),
                new ReleaseVersion(1, 3, 0),
                ReleasePageUri));
        var launcher = new FakeReleasePageLauncher { Succeeds = true };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Stable,
            checker,
            launcher);

        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());
        await host.InvokeAsync(() =>
            viewModel.OpenReleasePageCommand.Execute(null));

        Assert.AreEqual("发现新版本 1.3.0。", viewModel.VersionCheckStatusText);
        Assert.IsTrue(viewModel.CanOpenReleasePage);
        Assert.AreEqual(1, launcher.Calls);
        Assert.AreEqual(ReleasePageUri, launcher.LastUri);
        Assert.IsFalse(viewModel.VersionCheckStatusIsError);
    }

    [TestMethod]
    public async Task ReleasePageOpenFailureIsVisibleAndRetryable()
    {
        await using var host = new StaDispatcherTestHost();
        var checker = new FakeManualVersionCheckCoordinator(
            new ManualVersionCheckResult(
                ManualVersionCheckStatus.UpdateAvailable,
                new ReleaseVersion(1, 2, 3),
                new ReleaseVersion(1, 3, 0),
                ReleasePageUri));
        var launcher = new FakeReleasePageLauncher();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Stable,
            checker,
            launcher);

        await host.InvokeAsync(() =>
            viewModel.CheckForUpdatesCommand.ExecuteAsync());
        await host.InvokeAsync(() =>
            viewModel.OpenReleasePageCommand.Execute(null));

        StringAssert.Contains(viewModel.VersionCheckStatusText, "无法打开");
        Assert.IsTrue(viewModel.VersionCheckStatusIsError);
        Assert.IsTrue(viewModel.CanOpenReleasePage);
    }

    [TestMethod]
    public async Task DisposeCancelsPendingManualCheck()
    {
        await using var host = new StaDispatcherTestHost();
        var checker = new BlockingManualVersionCheckCoordinator();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            AgenTallyChannel.Stable,
            checker,
            new FakeReleasePageLauncher());

        Task? execution = null;
        await host.InvokeAsync((Action)(() =>
            execution = viewModel.CheckForUpdatesCommand.ExecuteAsync()));
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.InvokeAsync(viewModel.Dispose);
        Assert.IsNotNull(execution);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(checker.WasCancelled);
        Assert.IsFalse(viewModel.CheckForUpdatesCommand.CanExecute(null));
    }

    private static Task<SettingsViewModel> CreateAsync(
        StaDispatcherTestHost host,
        AgenTallyChannel channel,
        IManualVersionCheckCoordinator checker,
        IReleasePageLauncher launcher) =>
        host.InvokeAsync(() => new SettingsViewModel(
            queries: null,
            new UnavailablePriceCommandClient(),
            new RejectingPriceRestoreConfirmation(),
            host.Dispatcher,
            Path.Combine("data", "agentally.db"),
            channel,
            new UnavailableUiPreferencesStore(),
            checker,
            launcher));

    private sealed class FakeManualVersionCheckCoordinator(
        ManualVersionCheckResult result) : IManualVersionCheckCoordinator
    {
        public int Calls { get; private set; }

        public ManualVersionCheckResult Result { get; set; } = result;

        public Task<ManualVersionCheckResult> CheckAsync(
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class BlockingManualVersionCheckCoordinator :
        IManualVersionCheckCoordinator
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public async Task<ManualVersionCheckResult> CheckAsync(
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
            }

            return new ManualVersionCheckResult(
                ManualVersionCheckStatus.Cancelled);
        }
    }

    private sealed class FakeReleasePageLauncher : IReleasePageLauncher
    {
        public bool Succeeds { get; init; }

        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        public bool TryOpen(Uri releasePageUri)
        {
            Calls++;
            LastUri = releasePageUri;
            return Succeeds;
        }
    }
}
