using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
[DoNotParallelize]
public sealed class ManualVersionCheckCoordinatorTests
{
    private static readonly ReleaseVersion CurrentVersion = new(1, 2, 3);
    private static readonly ReleaseVersion LatestVersion = new(1, 3, 0);
    private static readonly VersionCheckConfiguration TestConfiguration = new(
        new Uri("https://updates.invalid/agentally/stable.json"),
        new Uri("https://releases.invalid/agentally"));

    [TestMethod]
    public async Task UnavailableConfigurationNeverCreatesService()
    {
        (VersionCheckAvailability Availability,
            ManualVersionCheckStatus Expected)[] scenarios =
        [
            (
                VersionCheckAvailability.DevelopmentDisabled,
                ManualVersionCheckStatus.DevelopmentDisabled),
            (
                VersionCheckAvailability.StableChannelNotConfigured,
                ManualVersionCheckStatus.StableChannelNotConfigured),
            (
                VersionCheckAvailability.InvalidCurrentVersion,
                ManualVersionCheckStatus.InvalidCurrentVersion)
        ];

        foreach ((VersionCheckAvailability availability,
                  ManualVersionCheckStatus expected) in scenarios)
        {
            int serviceFactoryCalls = 0;
            var coordinator = new ManualVersionCheckCoordinator(
                CreateUnavailableConfiguration(availability),
                () =>
                {
                    serviceFactoryCalls++;
                    return CreateService(VersionCheckOutcome.UpToDate);
                });

            ManualVersionCheckResult result = await coordinator.CheckAsync(
                CancellationToken.None);

            Assert.AreEqual(expected, result.Status);
            Assert.AreEqual(0, serviceFactoryCalls);
        }
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
        IManualVersionCheckCoordinator developmentCoordinator =
            ManualVersionCheckProductionComposition.Create(
                development,
                typeof(ManualVersionCheckCoordinator).Assembly);
        IManualVersionCheckCoordinator stableCoordinator =
            ManualVersionCheckProductionComposition.Create(
                stable,
                typeof(ManualVersionCheckCoordinator).Assembly);

        ManualVersionCheckResult developmentResult =
            await developmentCoordinator.CheckAsync(CancellationToken.None);
        ManualVersionCheckResult stableResult =
            await stableCoordinator.CheckAsync(CancellationToken.None);

        Assert.AreEqual(
            ManualVersionCheckStatus.DevelopmentDisabled,
            developmentResult.Status);
        Assert.AreEqual(
            ManualVersionCheckStatus.StableChannelNotConfigured,
            stableResult.Status);
    }

    [TestMethod]
    public async Task ManualChecksCanRunAgainAndDisposeEveryService()
    {
        var services = new List<RecordingVersionCheckService>();
        var coordinator = new ManualVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () =>
            {
                RecordingVersionCheckService service =
                    CreateService(VersionCheckOutcome.UpToDate);
                services.Add(service);
                return service;
            });

        ManualVersionCheckResult first = await coordinator.CheckAsync(
            CancellationToken.None);
        ManualVersionCheckResult second = await coordinator.CheckAsync(
            CancellationToken.None);

        Assert.AreEqual(ManualVersionCheckStatus.UpToDate, first.Status);
        Assert.AreEqual(ManualVersionCheckStatus.UpToDate, second.Status);
        Assert.AreEqual(CurrentVersion, first.CurrentVersion);
        Assert.HasCount(2, services);
        Assert.IsTrue(services.All(static service => service.IsDisposed));
        Assert.IsTrue(services.All(static service => service.CallCount == 1));
    }

    [TestMethod]
    public async Task UpdateAvailableUsesOnlyLocalTrustedReleasePage()
    {
        var coordinator = new ManualVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => CreateService(VersionCheckOutcome.UpdateAvailable));

        ManualVersionCheckResult result = await coordinator.CheckAsync(
            CancellationToken.None);

        Assert.AreEqual(
            ManualVersionCheckStatus.UpdateAvailable,
            result.Status);
        Assert.AreEqual(CurrentVersion, result.CurrentVersion);
        Assert.AreEqual(LatestVersion, result.LatestVersion);
        Assert.AreEqual(
            TestConfiguration.ReleasePageUri,
            result.ReleasePageUri);
    }

    [TestMethod]
    public async Task UntrustedOrIncompleteUpdateResultIsInvalid()
    {
        VersionCheckResult[] responses =
        [
            new(
                VersionCheckOutcome.UpdateAvailable,
                new ReleaseVersion(9, 9, 9),
                LatestVersion,
                new Uri("https://untrusted.invalid/release")),
            new(
                VersionCheckOutcome.UpdateAvailable,
                CurrentVersion,
                LatestVersion: null,
                TestConfiguration.ReleasePageUri)
        ];

        foreach (VersionCheckResult response in responses)
        {
            var coordinator = new ManualVersionCheckCoordinator(
                CreateAvailableConfiguration(),
                () => new RecordingVersionCheckService(
                    (_, _) => Task.FromResult(response)));

            ManualVersionCheckResult result = await coordinator.CheckAsync(
                CancellationToken.None);

            Assert.AreEqual(
                ManualVersionCheckStatus.InvalidResponse,
                result.Status);
            Assert.IsNull(result.ReleasePageUri);
        }
    }

    [TestMethod]
    public async Task NetworkAndManifestFailuresRemainExplicit()
    {
        (VersionCheckOutcome Outcome,
            ManualVersionCheckStatus Expected)[] scenarios =
        [
            (
                VersionCheckOutcome.NetworkFailure,
                ManualVersionCheckStatus.NetworkFailure),
            (
                VersionCheckOutcome.InvalidResponse,
                ManualVersionCheckStatus.InvalidResponse)
        ];

        foreach ((VersionCheckOutcome outcome,
                  ManualVersionCheckStatus expected) in scenarios)
        {
            var coordinator = new ManualVersionCheckCoordinator(
                CreateAvailableConfiguration(),
                () => CreateService(outcome));

            ManualVersionCheckResult result = await coordinator.CheckAsync(
                CancellationToken.None);

            Assert.AreEqual(expected, result.Status);
        }
    }

    [TestMethod]
    public async Task CancellationAndConstructionFailureDoNotEscape()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelledService = new RecordingVersionCheckService((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<VersionCheckResult>(token);
        });
        var cancelledCoordinator = new ManualVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => cancelledService);
        var failedCoordinator = new ManualVersionCheckCoordinator(
            CreateAvailableConfiguration(),
            () => throw new InvalidOperationException("synthetic"));

        ManualVersionCheckResult cancelled =
            await cancelledCoordinator.CheckAsync(cancellation.Token);
        ManualVersionCheckResult failed =
            await failedCoordinator.CheckAsync(CancellationToken.None);

        Assert.AreEqual(
            ManualVersionCheckStatus.Cancelled,
            cancelled.Status);
        Assert.IsTrue(cancelledService.IsDisposed);
        Assert.AreEqual(ManualVersionCheckStatus.Failed, failed.Status);
    }

    [TestMethod]
    public async Task StandaloneUnavailableCoordinatorNeverChecks()
    {
        var coordinator = new UnavailableManualVersionCheckCoordinator();

        ManualVersionCheckResult result = await coordinator.CheckAsync(
            CancellationToken.None);

        Assert.AreEqual(ManualVersionCheckStatus.Unavailable, result.Status);
    }

    [TestMethod]
    public void ShellLauncherRejectsUntrustedUrisBeforeProcessStart()
    {
        var launcher = new ShellReleasePageLauncher();

        Assert.IsFalse(launcher.TryOpen(
            new Uri("http://releases.invalid/agentally")));
        Assert.IsFalse(launcher.TryOpen(
            new Uri("https://user:secret@releases.invalid/agentally")));
        Assert.IsFalse(launcher.TryOpen(
            new Uri("file:///C:/agentally/release.html")));
    }

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
            outcome is VersionCheckOutcome.UpdateAvailable or
                VersionCheckOutcome.UpToDate
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
}
