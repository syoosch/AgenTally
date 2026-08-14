using System.IO;
using System.Text.Json;
using AgenTally.Core.Hosting;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class CoreInstanceLeaseTests
{
    [TestMethod]
    public void TryAcquire_RejectsSecondOwnerAndRecoversAfterDispose()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);

        using CoreInstanceLease first = CoreInstanceLease.TryAcquire(profile) ??
            throw new AssertFailedException("First lease should succeed.");
        Assert.IsNull(CoreInstanceLease.TryAcquire(profile));

        first.Dispose();
        using CoreInstanceLease recovered = CoreInstanceLease.TryAcquire(profile) ??
            throw new AssertFailedException("Lease should recover after dispose.");
    }

    [TestMethod]
    public void TryAcquire_SharesSourceLeaseAcrossChannels()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile development = CreateDevelopmentProfile(directory);
        string installed = directory.File("installed");
        string local = directory.File("local");
        string user = directory.File("user");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(Path.Combine(user, ".codex"));
        AgenTallyRuntimeProfile stable = AgenTallyRuntimeProfile.CreateStable(
            installed,
            local,
            user);
        Assert.AreEqual(development.CodexHome, stable.CodexHome);
        Assert.AreEqual(development.SourceLeaseName, stable.SourceLeaseName);
        Assert.AreNotEqual(development.DatabaseLeaseName, stable.DatabaseLeaseName);

        using CoreInstanceLease owner = CoreInstanceLease.TryAcquire(development) ??
            throw new AssertFailedException("Development lease should succeed.");
        Assert.IsNull(CoreInstanceLease.TryAcquire(stable));
    }

    [TestMethod]
    public void TryAcquire_AllowsDistinctSourceAndDatabaseProfiles()
    {
        using var firstDirectory = new TestTempDirectory();
        using var secondDirectory = new TestTempDirectory();
        AgenTallyRuntimeProfile firstProfile = CreateDevelopmentProfile(firstDirectory);
        AgenTallyRuntimeProfile secondProfile = CreateDevelopmentProfile(secondDirectory);

        using CoreInstanceLease first = CoreInstanceLease.TryAcquire(firstProfile) ??
            throw new AssertFailedException("First lease should succeed.");
        using CoreInstanceLease second = CoreInstanceLease.TryAcquire(secondProfile) ??
            throw new AssertFailedException("Distinct profile should succeed.");
    }

    [TestMethod]
    public async Task ShutdownSignal_WakesMatchingListenerOnly()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile development = CreateDevelopmentProfile(directory);
        string installed = directory.File("installed");
        string local = directory.File("local");
        string user = directory.File("user");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(Path.Combine(user, ".codex"));
        AgenTallyRuntimeProfile stable = AgenTallyRuntimeProfile.CreateStable(
            installed,
            local,
            user);

        using var developmentSignal = new ApplicationShutdownSignal(
            development.ShutdownEventName);
        using var stableSignal = new ApplicationShutdownSignal(
            stable.ShutdownEventName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task developmentWait = developmentSignal.WaitAsync(timeout.Token);
        Task stableWait = stableSignal.WaitAsync(timeout.Token);

        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
            development.ShutdownEventName));
        await developmentWait;
        Assert.IsFalse(stableWait.IsCompleted);

        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
            stable.ShutdownEventName));
        await stableWait;
    }

    [TestMethod]
    public async Task MaintenanceShutdownSignal_WakesMatchingProfileOnly()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile firstProfile = CreateDevelopmentProfile(
            directory,
            Path.Combine("user", ".codex"));
        AgenTallyRuntimeProfile secondProfile = CreateDevelopmentProfile(
            directory,
            Path.Combine("artifacts", "development", "sources", "codex"));
        using var first = new CoreMaintenanceShutdownSignal(firstProfile);
        using var second = new CoreMaintenanceShutdownSignal(secondProfile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstWait = first.WaitAsync(timeout.Token);
        Task secondWait = second.WaitAsync(timeout.Token);

        Assert.IsTrue(CoreMaintenanceShutdownSignal.TryRequest(firstProfile));
        await firstWait;
        Assert.IsFalse(secondWait.IsCompleted);

        Assert.IsTrue(CoreMaintenanceShutdownSignal.TryRequest(secondProfile));
        await secondWait;
    }

    [TestMethod]
    public void ShutdownSignal_ReturnsFalseWhenNoApplicationOwnsTheEvent()
    {
        string name = $@"Local\AgenTally.Tests.Missing.{Guid.NewGuid():N}";
        Assert.IsFalse(ApplicationShutdownSignal.TryRequest(name));
    }

    [TestMethod]
    public async Task ShutdownSignal_BroadcastsToEveryMatchingListener()
    {
        string name = $@"Local\AgenTally.Tests.Broadcast.{Guid.NewGuid():N}";
        using var first = new ApplicationShutdownSignal(name);
        using var second = new ApplicationShutdownSignal(name);
        using var third = new ApplicationShutdownSignal(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task[] listeners =
        [
            first.WaitAsync(timeout.Token),
            second.WaitAsync(timeout.Token),
            Task.Run(() => third.Wait(timeout.Token), timeout.Token)
        ];

        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(name));
        await Task.WhenAll(listeners);
    }

    [TestMethod]
    public async Task ShutdownSignal_DevelopmentRequestMarkerWakesUiFallback()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        using var signal = new ApplicationShutdownSignal(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task listener = Task.Run(() => signal.Wait(timeout.Token), timeout.Token);
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllTextAsync(
            profile.ShutdownRequestPath,
            JsonSerializer.Serialize(new
            {
                profileId = profile.ProfileId,
                requestedAtUtcTicks = DateTime.UtcNow.Ticks
            }));

        await listener;
    }

    [TestMethod]
    public async Task ShutdownSignal_DevelopmentRequestMarkerWakesAsyncFallback()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        using var signal = new ApplicationShutdownSignal(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task listener = signal.WaitAsync(timeout.Token);

        await WriteRequestMarkerAsync(profile);

        await listener;
    }

    [TestMethod]
    public async Task ShutdownSignal_ProfileMarkersAreIsolatedWithinSharedRuntimeRoot()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile firstProfile = CreateDevelopmentProfile(
            directory,
            Path.Combine("user", ".codex"));
        AgenTallyRuntimeProfile secondProfile = CreateDevelopmentProfile(
            directory,
            Path.Combine("artifacts", "development", "sources", "codex"));

        Assert.AreEqual(firstProfile.RuntimeRoot, secondProfile.RuntimeRoot);
        Assert.AreNotEqual(
            firstProfile.ShutdownRequestPath,
            secondProfile.ShutdownRequestPath);

        using var first = new ApplicationShutdownSignal(firstProfile);
        using var second = new ApplicationShutdownSignal(secondProfile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task firstWait = first.WaitAsync(timeout.Token);
        Task secondWait = second.WaitAsync(timeout.Token);

        await WriteRequestMarkerAsync(firstProfile);
        await firstWait;
        await Task.Delay(150, timeout.Token);
        Assert.IsFalse(secondWait.IsCompleted);

        await WriteRequestMarkerAsync(secondProfile);
        await secondWait;
        Assert.IsTrue(File.Exists(firstProfile.ShutdownRequestPath));
        Assert.IsTrue(File.Exists(secondProfile.ShutdownRequestPath));
    }

    [TestMethod]
    public async Task ShutdownSignal_ProfileMarkerBroadcastsToSyncAndAsyncListeners()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        using var asynchronous = new ApplicationShutdownSignal(profile);
        using var synchronous = new ApplicationShutdownSignal(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task[] listeners =
        [
            asynchronous.WaitAsync(timeout.Token),
            Task.Run(() => synchronous.Wait(timeout.Token), timeout.Token)
        ];

        await WriteRequestMarkerAsync(profile);

        await Task.WhenAll(listeners);
    }

    [TestMethod]
    public async Task ShutdownSignal_RequestReportsEachTransportForTheProfile()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        using var signal = new ApplicationShutdownSignal(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task listener = signal.WaitAsync(timeout.Token);

        ApplicationShutdownRequestResult result =
            ApplicationShutdownSignal.Request(profile);

        Assert.AreEqual(profile.ProfileId, result.ProfileId);
        Assert.IsTrue(result.MarkerWritten);
        Assert.IsTrue(result.SemaphoreOpened);
        Assert.IsTrue(result.SemaphoreBroadcast);
        Assert.IsTrue(result.AnyTransportSucceeded);
        Assert.IsTrue(result.RequestAccepted);
        await listener;
    }

    [TestMethod]
    public async Task ShutdownSignal_ProfileListenerIgnoresStaleMarkerAndResidualSemaphore()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllTextAsync(
            profile.ShutdownRequestPath,
            JsonSerializer.Serialize(new
            {
                profileId = profile.ProfileId,
                requestedAtUtcTicks = 1
            }));
        using var legacyOwner = new ApplicationShutdownSignal(
            profile.ShutdownEventName);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
            profile.ShutdownEventName));
        using var signal = new ApplicationShutdownSignal(profile);
        using (var staleTimeout = new CancellationTokenSource(
                   TimeSpan.FromMilliseconds(250)))
        {
            Task staleWait = signal.WaitAsync(staleTimeout.Token);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await staleWait);
        }

        using var currentTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        Task currentWait = signal.WaitAsync(currentTimeout.Token);
        ApplicationShutdownRequestResult result =
            ApplicationShutdownSignal.Request(profile);
        Assert.IsTrue(result.RequestAccepted);
        await currentWait;
    }

    [TestMethod]
    public async Task ShutdownSignal_ProfileListenerIgnoresSemaphoreWithoutCurrentMarker()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        using var signal = new ApplicationShutdownSignal(profile);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task listener = signal.WaitAsync(timeout.Token);

        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
            profile.ShutdownEventName));
        await Task.Delay(250, timeout.Token);
        Assert.IsFalse(listener.IsCompleted);

        ApplicationShutdownRequestResult result =
            ApplicationShutdownSignal.Request(profile);
        Assert.IsTrue(result.RequestAccepted);
        await listener;
    }

    [TestMethod]
    public void ShutdownSignal_ProfileRequestFailsClosedWhenOnlySemaphoreSucceeds()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(profile.RuntimeRoot)!);
        File.WriteAllText(profile.RuntimeRoot, "blocked");
        using var legacyOwner = new ApplicationShutdownSignal(
            profile.ShutdownEventName);

        ApplicationShutdownRequestResult result =
            ApplicationShutdownSignal.Request(profile);

        Assert.IsFalse(result.MarkerWritten);
        Assert.IsTrue(result.SemaphoreOpened);
        Assert.IsTrue(result.SemaphoreBroadcast);
        Assert.IsTrue(result.AnyTransportSucceeded);
        Assert.IsFalse(result.RequestAccepted);
        Assert.IsFalse(ApplicationShutdownSignal.TryRequest(profile));
    }

    [TestMethod]
    public async Task ShutdownSignal_StaleProfileMarkerDoesNotWakeNewProcessListener()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllTextAsync(
            profile.ShutdownRequestPath,
            JsonSerializer.Serialize(new
            {
                profileId = profile.ProfileId,
                requestedAtUtcTicks = 1
            }));
        using var signal = new ApplicationShutdownSignal(profile);
        using (var staleTimeout = new CancellationTokenSource(
                   TimeSpan.FromMilliseconds(250)))
        {
            Task staleWait = signal.WaitAsync(staleTimeout.Token);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await staleWait);
        }

        using var currentTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        Task currentWait = signal.WaitAsync(currentTimeout.Token);
        await WriteRequestMarkerAsync(profile);
        await currentWait;
    }

    [TestMethod]
    public async Task ShutdownSignal_ProfileCancellationAndDisposeRaceWithWakeCallbacks()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            var signal = new ApplicationShutdownSignal(profile);
            Task canceledWait = signal.WaitAsync(cancellation.Token);
            Task disposedWait = signal.WaitAsync(CancellationToken.None);

            await File.WriteAllTextAsync(
                profile.ShutdownRequestPath,
                JsonSerializer.Serialize(new
                {
                    profileId = profile.ProfileId,
                    requestedAtUtcTicks = 1
                }));
            Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
                profile.ShutdownEventName));
            cancellation.Cancel();
            signal.Dispose();

            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await canceledWait.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await disposedWait.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [TestMethod]
    public async Task ShutdownSignal_CancellationAndDisposeCompleteOutstandingWaits()
    {
        string name = $@"Local\AgenTally.Tests.Dispose.{Guid.NewGuid():N}";
        var canceledSignal = new ApplicationShutdownSignal(name);
        using (canceledSignal)
        using (var cancellation = new CancellationTokenSource())
        {
            Task canceledWait = canceledSignal.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await canceledWait);
        }

        var disposedSignal = new ApplicationShutdownSignal(name);
        Task disposedWait = disposedSignal.WaitAsync(CancellationToken.None);
        disposedSignal.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await disposedWait.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            disposedSignal.Wait(CancellationToken.None));
    }

    [TestMethod]
    public async Task UiInstanceRegistration_SecondInstanceActivatesOwnerAndLeaseRecovers()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile =
            CreateDevelopmentProfile(directory);
        UiInstanceRegistration owner =
            await UiInstanceRegistration.TryRegisterAsync(profile) ??
            throw new AssertFailedException(
                "The first UI instance should own the registration.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task activation = Task.Run(
            () => owner.ActivationSignal.Wait(timeout.Token),
            timeout.Token);

        UiInstanceRegistration? duplicate =
            await UiInstanceRegistration.TryRegisterAsync(
                profile,
                TimeSpan.FromMilliseconds(250),
                timeout.Token);

        Assert.IsNull(duplicate);
        await activation;
        owner.Dispose();
        using UiInstanceRegistration recovered =
            await UiInstanceRegistration.TryRegisterAsync(
                profile,
                TimeSpan.FromMilliseconds(250),
                timeout.Token) ??
            throw new AssertFailedException(
                "The UI registration should be recoverable after exit.");
    }

    [TestMethod]
    public async Task UiActivationSignal_IsIsolatedByProfile()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile first = CreateDevelopmentProfile(
            directory,
            Path.Combine("first", ".codex"));
        AgenTallyRuntimeProfile second = CreateDevelopmentProfile(
            directory,
            Path.Combine("second", ".codex"));
        using UiInstanceRegistration firstRegistration =
            await UiInstanceRegistration.TryRegisterAsync(first) ??
            throw new AssertFailedException(
                "The first profile should own its UI registration.");
        using UiInstanceRegistration secondRegistration =
            await UiInstanceRegistration.TryRegisterAsync(second) ??
            throw new AssertFailedException(
                "The second profile should own its UI registration.");
        using var firstTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var secondTimeout = new CancellationTokenSource();
        Task firstActivation = Task.Run(
            () => firstRegistration.ActivationSignal.Wait(firstTimeout.Token),
            firstTimeout.Token);
        Task secondActivation = Task.Run(
            () => secondRegistration.ActivationSignal.Wait(secondTimeout.Token),
            secondTimeout.Token);

        Assert.IsTrue(UiActivationSignal.TryRequest(first));
        await firstActivation;
        Assert.IsFalse(secondActivation.IsCompleted);
        secondTimeout.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await secondActivation);
    }

    private static Task WriteRequestMarkerAsync(
        AgenTallyRuntimeProfile profile)
    {
        Directory.CreateDirectory(profile.RuntimeRoot);
        return File.WriteAllTextAsync(
            profile.ShutdownRequestPath,
            JsonSerializer.Serialize(new
            {
                profileId = profile.ProfileId,
                requestedAtUtcTicks = DateTime.UtcNow.Ticks
            }));
    }

    private static AgenTallyRuntimeProfile CreateDevelopmentProfile(
        TestTempDirectory directory) => CreateDevelopmentProfile(
            directory,
            Path.Combine("user", ".codex"));

    private static AgenTallyRuntimeProfile CreateDevelopmentProfile(
        TestTempDirectory directory,
        string relativeCodexHome)
    {
        File.WriteAllText(directory.File("AgenTally.sln"), string.Empty);
        File.WriteAllText(directory.File(".agentally-root"), string.Empty);
        string codexHome = directory.File(relativeCodexHome);
        Directory.CreateDirectory(codexHome);
        return AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            codexHome);
    }
}
