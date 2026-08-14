using System.IO;
using AgenTally.Core.Hosting;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class CoreTrayControllerTests
{
    [TestMethod]
    public void OpenOrActivate_ExistingUiNeverStartsAnotherProcess()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        int startCalls = 0;
        using var controller = new CoreTrayController(
            profile,
            tryActivate: () => true,
            startUi: _ =>
            {
                startCalls++;
                return new FakeTrackedUiProcess();
            },
            requestShutdown: () => AcceptedShutdown(profile));

        CoreTrayOpenResult result = controller.OpenOrActivate();

        Assert.AreEqual(CoreTrayOpenResult.Activated, result);
        Assert.AreEqual(0, startCalls);
    }

    [TestMethod]
    public void OpenOrActivate_TracksStartupAndAllowsRelaunchAfterUiExit()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateUiExecutable(profile);
        var launched = new List<FakeTrackedUiProcess>();
        using var controller = new CoreTrayController(
            profile,
            tryActivate: () => false,
            startUi: path =>
            {
                Assert.AreEqual(
                    Path.GetFullPath(profile.UiExecutablePath),
                    path);
                var process = new FakeTrackedUiProcess();
                launched.Add(process);
                return process;
            },
            requestShutdown: () => AcceptedShutdown(profile));

        Assert.AreEqual(
            CoreTrayOpenResult.Launched,
            controller.OpenOrActivate());
        Assert.AreEqual(
            CoreTrayOpenResult.LaunchInProgress,
            controller.OpenOrActivate());
        launched.Single().Exit();
        Assert.AreEqual(
            CoreTrayOpenResult.Launched,
            controller.OpenOrActivate());

        Assert.AreEqual(2, launched.Count);
        Assert.IsTrue(launched[0].IsDisposed);
    }

    [TestMethod]
    public void OpenOrActivate_MissingUiFailsWithoutStarting()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        int startCalls = 0;
        using var controller = new CoreTrayController(
            profile,
            tryActivate: () => false,
            startUi: _ =>
            {
                startCalls++;
                return new FakeTrackedUiProcess();
            },
            requestShutdown: () => AcceptedShutdown(profile));

        CoreTrayOpenResult result = controller.OpenOrActivate();

        Assert.AreEqual(CoreTrayOpenResult.Failed, result);
        Assert.AreEqual(0, startCalls);
    }

    [TestMethod]
    public void RequestExit_AcceptedRequestDisablesFurtherCommands()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        int shutdownCalls = 0;
        using var controller = new CoreTrayController(
            profile,
            tryActivate: () => true,
            startUi: _ => new FakeTrackedUiProcess(),
            requestShutdown: () =>
            {
                shutdownCalls++;
                return AcceptedShutdown(profile);
            });

        Assert.AreEqual(
            CoreTrayExitResult.Requested,
            controller.RequestExit());
        Assert.AreEqual(
            CoreTrayExitResult.AlreadyRequested,
            controller.RequestExit());
        Assert.AreEqual(
            CoreTrayOpenResult.Exiting,
            controller.OpenOrActivate());
        Assert.AreEqual(1, shutdownCalls);
    }

    [TestMethod]
    public void RequestExit_FailedRequestLeavesTrayCommandsAvailable()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        using var controller = new CoreTrayController(
            profile,
            tryActivate: () => true,
            startUi: _ => new FakeTrackedUiProcess(),
            requestShutdown: () => new ApplicationShutdownRequestResult(
                profile.ProfileId,
                MarkerWritten: false,
                SemaphoreOpened: true,
                SemaphoreBroadcast: true));

        Assert.AreEqual(
            CoreTrayExitResult.Failed,
            controller.RequestExit());
        Assert.AreEqual(
            CoreTrayOpenResult.Activated,
            controller.OpenOrActivate());
    }

    [TestMethod]
    public void CoreBuild_ContainsTrayResourceAndExecutableIcon()
    {
        string assemblyPath = typeof(CoreHost).Assembly.Location;
        using Stream? resource = typeof(CoreHost).Assembly
            .GetManifestResourceStream(
                "AgenTally.Core.Resources.AgenTally.ico");
        string executablePath = Path.ChangeExtension(assemblyPath, ".exe");

        Assert.IsNotNull(resource);
        Assert.IsTrue(File.Exists(executablePath));
        using System.Drawing.Icon? executableIcon =
            System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
        Assert.IsNotNull(executableIcon);
    }

    private static ApplicationShutdownRequestResult AcceptedShutdown(
        AgenTallyRuntimeProfile profile) => new(
            profile.ProfileId,
            MarkerWritten: true,
            SemaphoreOpened: true,
            SemaphoreBroadcast: true);

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory)
    {
        File.WriteAllText(directory.File("AgenTally.sln"), string.Empty);
        File.WriteAllText(directory.File(".agentally-root"), string.Empty);
        string codexHome = directory.File(Path.Combine("user", ".codex"));
        Directory.CreateDirectory(codexHome);
        return AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            codexHome);
    }

    private static void CreateUiExecutable(AgenTallyRuntimeProfile profile)
    {
        Directory.CreateDirectory(profile.ApplicationRoot);
        File.WriteAllText(profile.UiExecutablePath, string.Empty);
    }

    private sealed class FakeTrackedUiProcess : ITrackedUiProcess
    {
        public event EventHandler? Exited;

        public bool HasExited { get; private set; }

        public bool IsDisposed { get; private set; }

        public void Exit()
        {
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => IsDisposed = true;
    }
}
