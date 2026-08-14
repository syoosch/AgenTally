using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticVersionCheckCoordinatorTests
{
    private static readonly ReleaseVersion CurrentVersion = new(1, 2, 3);
    private static readonly ReleaseVersion LatestVersion = new(1, 3, 0);
    private static readonly VersionCheckConfiguration TestConfiguration = new(
        new Uri("https://updates.invalid/agentally/stable.json"),
        new Uri("https://releases.invalid/agentally"));

    [TestMethod]
    public async Task UnavailableConfigurationNeverCreatesGateOrService()
    {
        (VersionCheckAvailability Availability,
            AutomaticVersionCheckRunResult Expected)[] scenarios =
        [
            (
                VersionCheckAvailability.DevelopmentDisabled,
                AutomaticVersionCheckRunResult.DevelopmentDisabled),
            (
                VersionCheckAvailability.StableChannelNotConfigured,
                AutomaticVersionCheckRunResult.StableChannelNotConfigured),
            (
                VersionCheckAvailability.InvalidCurrentVersion,
                AutomaticVersionCheckRunResult.InvalidCurrentVersion)
        ];

        foreach ((VersionCheckAvailability availability,
                  AutomaticVersionCheckRunResult expected) in scenarios)
        {
            int gateFactoryCalls = 0;
            int serviceFactoryCalls = 0;
            var presenter = new RecordingPresenter();
            using var coordinator = new AutomaticVersionCheckCoordinator(
                CreateUnavailableConfiguration(availability),
                () =>
                {
                    gateFactoryCalls++;
                    return new RecordingLifecycleGate(
                        AutomaticVersionCheckClaimResult.Claimed);
                },
                () =>
                {
                    serviceFactoryCalls++;
                    return CreateService(VersionCheckOutcome.UpToDate);
                },
                presenter);

            AutomaticVersionCheckRunResult result =
                await coordinator.RunAsync(CancellationToken.None);

            Assert.AreEqual(expected, result);
            Assert.AreEqual(0, gateFactoryCalls);
            Assert.AreEqual(0, serviceFactoryCalls);
            Assert.AreEqual(0, presenter.CallCount);
        }
    }

    [TestMethod]
    public async Task CancelledBeforeClaimNeverCreatesGateOrService()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int gateFactoryCalls = 0;
        int serviceFactoryCalls = 0;
        using var coordinator = new AutomaticVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () =>
            {
                gateFactoryCalls++;
                return new RecordingLifecycleGate(
                    AutomaticVersionCheckClaimResult.Claimed);
            },
            () =>
            {
                serviceFactoryCalls++;
                return CreateService(VersionCheckOutcome.UpToDate);
            },
            new RecordingPresenter());

        AutomaticVersionCheckRunResult result = await coordinator.RunAsync(
            cancellation.Token);

        Assert.AreEqual(AutomaticVersionCheckRunResult.Cancelled, result);
        Assert.AreEqual(0, gateFactoryCalls);
        Assert.AreEqual(0, serviceFactoryCalls);
    }

    [TestMethod]
    public async Task MissingLifecycleOwnerSkipsBeforeServiceCreation()
    {
        int serviceFactoryCalls = 0;
        using var coordinator = new AutomaticVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => null,
            () =>
            {
                serviceFactoryCalls++;
                return CreateService(VersionCheckOutcome.UpToDate);
            },
            new RecordingPresenter());

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.LifecycleUnavailable,
            result);
        Assert.AreEqual(0, serviceFactoryCalls);
    }

    [TestMethod]
    public async Task AlreadyCheckedLifecycleSkipsBeforeServiceCreation()
    {
        int serviceFactoryCalls = 0;
        var lifecycleGate = new RecordingLifecycleGate(
            AutomaticVersionCheckClaimResult.AlreadyClaimed);
        var coordinator = new AutomaticVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => lifecycleGate,
            () =>
            {
                serviceFactoryCalls++;
                return CreateService(VersionCheckOutcome.UpToDate);
            },
            new RecordingPresenter());

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.AlreadyChecked,
            result);
        Assert.AreEqual(1, lifecycleGate.ClaimCount);
        Assert.AreEqual(0, serviceFactoryCalls);
        Assert.IsFalse(lifecycleGate.IsDisposed);

        coordinator.Dispose();
        Assert.IsTrue(lifecycleGate.IsDisposed);
    }

    [TestMethod]
    public async Task UpToDateChecksOnceSilentlyAndDisposesService()
    {
        var lifecycleGate = new RecordingLifecycleGate(
            AutomaticVersionCheckClaimResult.Claimed);
        var service = CreateService(VersionCheckOutcome.UpToDate);
        var presenter = new RecordingPresenter();
        using var coordinator = new AutomaticVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => lifecycleGate,
            () => service,
            presenter);

        AutomaticVersionCheckRunResult first =
            await coordinator.RunAsync(CancellationToken.None);
        AutomaticVersionCheckRunResult second =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(AutomaticVersionCheckRunResult.UpToDate, first);
        Assert.AreEqual(AutomaticVersionCheckRunResult.AlreadyRun, second);
        Assert.AreEqual(1, lifecycleGate.ClaimCount);
        Assert.AreEqual(1, service.CallCount);
        Assert.IsTrue(service.IsDisposed);
        Assert.AreEqual(0, presenter.CallCount);
    }

    [TestMethod]
    public async Task UpdateAvailablePresentsOnlyConfiguredReleasePage()
    {
        var presenter = new RecordingPresenter();
        using var coordinator = CreateCoordinator(
            CreateService(VersionCheckOutcome.UpdateAvailable),
            presenter);

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.UpdateAvailable,
            result);
        Assert.AreEqual(1, presenter.CallCount);
        Assert.AreEqual(CurrentVersion, presenter.CurrentVersion);
        Assert.AreEqual(LatestVersion, presenter.LatestVersion);
        Assert.AreEqual(
            TestConfiguration.ReleasePageUri,
            presenter.ReleasePageUri);
    }

    [TestMethod]
    public async Task AutomaticFailuresRemainSilent()
    {
        (VersionCheckOutcome Outcome,
            AutomaticVersionCheckRunResult Expected)[] scenarios =
        [
            (
                VersionCheckOutcome.NetworkFailure,
                AutomaticVersionCheckRunResult.NetworkFailure),
            (
                VersionCheckOutcome.InvalidResponse,
                AutomaticVersionCheckRunResult.InvalidResponse)
        ];

        foreach ((VersionCheckOutcome outcome,
                  AutomaticVersionCheckRunResult expected) in scenarios)
        {
            var presenter = new RecordingPresenter();
            using var coordinator = CreateCoordinator(
                CreateService(outcome),
                presenter);

            AutomaticVersionCheckRunResult result =
                await coordinator.RunAsync(CancellationToken.None);

            Assert.AreEqual(expected, result);
            Assert.AreEqual(0, presenter.CallCount);
        }
    }

    [TestMethod]
    public async Task MalformedUpdateResultRemainsSilent()
    {
        var service = new RecordingVersionCheckService(
            (_, _) => Task.FromResult(new VersionCheckResult(
                VersionCheckOutcome.UpdateAvailable,
                CurrentVersion,
                LatestVersion: null,
                ReleasePageUri: null)));
        var presenter = new RecordingPresenter();
        using var coordinator = CreateCoordinator(service, presenter);

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.InvalidResponse,
            result);
        Assert.AreEqual(0, presenter.CallCount);
    }

    [TestMethod]
    public async Task UntrustedReleasePageResultRemainsSilent()
    {
        var service = new RecordingVersionCheckService(
            (_, _) => Task.FromResult(new VersionCheckResult(
                VersionCheckOutcome.UpdateAvailable,
                new ReleaseVersion(9, 9, 9),
                LatestVersion,
                new Uri("https://untrusted.invalid/release"))));
        var presenter = new RecordingPresenter();
        using var coordinator = CreateCoordinator(service, presenter);

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.InvalidResponse,
            result);
        Assert.AreEqual(0, presenter.CallCount);
    }

    [TestMethod]
    public async Task CancellationAfterClaimRemainsSilentAndDisposesService()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new RecordingVersionCheckService((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<VersionCheckResult>(token);
        });
        var presenter = new RecordingPresenter();
        using var coordinator = CreateCoordinator(service, presenter);

        AutomaticVersionCheckRunResult result = await coordinator.RunAsync(
            cancellation.Token);

        Assert.AreEqual(AutomaticVersionCheckRunResult.Cancelled, result);
        Assert.IsTrue(service.IsDisposed);
        Assert.AreEqual(0, presenter.CallCount);
    }

    [TestMethod]
    public async Task ServiceConstructionFailureDoesNotEscapeStartup()
    {
        var presenter = new RecordingPresenter();
        using var coordinator = new AutomaticVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => new RecordingLifecycleGate(
                AutomaticVersionCheckClaimResult.Claimed),
            () => throw new InvalidOperationException("synthetic"),
            presenter);

        AutomaticVersionCheckRunResult result =
            await coordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(AutomaticVersionCheckRunResult.Failed, result);
        Assert.AreEqual(0, presenter.CallCount);
    }

    [TestMethod]
    public async Task ProductionDevelopmentAndUnconfiguredStableStayOffline()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile development =
            CreateDevelopmentProfile(directory);
        AgenTallyRuntimeProfile stable = AgenTallyRuntimeProfile.CreateStable(
            directory.File("app"),
            directory.File("local"),
            directory.File("user"));
        var presenter = new RecordingPresenter();
        using AutomaticVersionCheckCoordinator developmentCoordinator =
            AutomaticVersionCheckProductionComposition.Create(
                development,
                typeof(AutomaticVersionCheckCoordinator).Assembly,
                presenter);
        using AutomaticVersionCheckCoordinator stableCoordinator =
            AutomaticVersionCheckProductionComposition.Create(
                stable,
                typeof(AutomaticVersionCheckCoordinator).Assembly,
                presenter);

        AutomaticVersionCheckRunResult developmentResult =
            await developmentCoordinator.RunAsync(CancellationToken.None);
        AutomaticVersionCheckRunResult stableResult =
            await stableCoordinator.RunAsync(CancellationToken.None);

        Assert.AreEqual(
            AutomaticVersionCheckRunResult.DevelopmentDisabled,
            developmentResult);
        Assert.AreEqual(
            AutomaticVersionCheckRunResult.StableChannelNotConfigured,
            stableResult);
        Assert.IsFalse(IsLifecycleStatePresent(development));
        Assert.IsFalse(IsLifecycleStatePresent(stable));
        Assert.AreEqual(0, presenter.CallCount);
    }

    private static AutomaticVersionCheckCoordinator CreateCoordinator(
        RecordingVersionCheckService service,
        RecordingPresenter presenter) =>
        new(
            CreateAvailableConfiguration(),
            () => new RecordingLifecycleGate(
                AutomaticVersionCheckClaimResult.Claimed),
            () => service,
            presenter);

    private static VersionCheckRuntimeConfiguration
        CreateAvailableConfiguration() =>
        new(
            VersionCheckAvailability.Available,
            CurrentVersion,
            TestConfiguration);

    private static VersionCheckRuntimeConfiguration
        CreateUnavailableConfiguration(
            VersionCheckAvailability availability) =>
        new(
            availability,
            availability ==
                VersionCheckAvailability.StableChannelNotConfigured
                    ? CurrentVersion
                    : null,
            ServiceConfiguration: null);

    private static RecordingVersionCheckService CreateService(
        VersionCheckOutcome outcome) =>
        new((currentVersion, _) => Task.FromResult(new VersionCheckResult(
            outcome,
            currentVersion,
            outcome == VersionCheckOutcome.UpdateAvailable
                ? LatestVersion
                : null,
            outcome == VersionCheckOutcome.UpdateAvailable
                ? TestConfiguration.ReleasePageUri
                : null)));

    private static AgenTallyRuntimeProfile CreateDevelopmentProfile(
        TestTempDirectory directory)
    {
        File.WriteAllText(directory.File("AgenTally.sln"), string.Empty);
        File.WriteAllText(directory.File(".agentally-root"), string.Empty);
        string codexHome = directory.File("codex");
        Directory.CreateDirectory(codexHome);
        return AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            codexHome);
    }

    private static bool IsLifecycleStatePresent(
        AgenTallyRuntimeProfile profile)
    {
        EventWaitHandle? state = null;
        try
        {
            return EventWaitHandle.TryOpenExisting(
                profile.VersionCheckLifecycleEventName,
                out state);
        }
        finally
        {
            state?.Dispose();
        }
    }

    private sealed class RecordingLifecycleGate(
        AutomaticVersionCheckClaimResult result) :
        IAutomaticVersionCheckLifecycleGate
    {
        public int ClaimCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public AutomaticVersionCheckClaimResult TryClaim()
        {
            ClaimCount++;
            return result;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingVersionCheckService(
        Func<ReleaseVersion, CancellationToken, Task<VersionCheckResult>> check) :
        IVersionCheckService,
        IDisposable
    {
        public int CallCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<VersionCheckResult> CheckAsync(
            ReleaseVersion currentVersion,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return check(currentVersion, cancellationToken);
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingPresenter :
        IAutomaticVersionCheckPresenter
    {
        public int CallCount { get; private set; }

        public ReleaseVersion? CurrentVersion { get; private set; }

        public ReleaseVersion? LatestVersion { get; private set; }

        public Uri? ReleasePageUri { get; private set; }

        public void ShowUpdateAvailable(
            ReleaseVersion currentVersion,
            ReleaseVersion latestVersion,
            Uri releasePageUri)
        {
            CallCount++;
            CurrentVersion = currentVersion;
            LatestVersion = latestVersion;
            ReleasePageUri = releasePageUri;
        }
    }
}
