using System.IO;
using AgenTally.Core.Hosting;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
[DoNotParallelize]
public sealed class VersionCheckLifecycleGateTests
{
    [TestMethod]
    public void DevelopmentNeverCreatesOrOpensLifecycleState()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateDevelopmentProfile(directory);

        using VersionCheckLifecycleRegistration? owner =
            VersionCheckLifecycleRegistration.TryCreateCoreOwner(profile);
        using var gate = new AutomaticVersionCheckLifecycleGate(profile);

        Assert.IsNull(owner);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.DevelopmentDisabled,
            gate.TryClaim());
        Assert.IsFalse(IsLifecycleStatePresent(profile));
    }

    [TestMethod]
    public void StableUiCannotCreateLifecycleStateWithoutCoreOwner()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        using var gate = new AutomaticVersionCheckLifecycleGate(profile);

        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.Unavailable,
            gate.TryClaim());
        Assert.IsFalse(IsLifecycleStatePresent(profile));
    }

    [TestMethod]
    public void StableCoreSessionOwnsFirstAutomaticClaim()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        using var session = new CoreRuntimeSession(profile);
        using var gate = new AutomaticVersionCheckLifecycleGate(profile);

        Assert.IsTrue(IsLifecycleStatePresent(profile));
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.Claimed,
            gate.TryClaim());
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.AlreadyClaimed,
            gate.TryClaim());
    }

    [TestMethod]
    public void OrdinaryUiReopenDoesNotClaimAgainWhileCoreLives()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        using VersionCheckLifecycleRegistration owner =
            CreateCoreOwner(profile);
        using (var firstUi = new AutomaticVersionCheckLifecycleGate(profile))
        {
            Assert.AreEqual(
                AutomaticVersionCheckClaimResult.Claimed,
                firstUi.TryClaim());
        }

        using var reopenedUi = new AutomaticVersionCheckLifecycleGate(profile);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.AlreadyClaimed,
            reopenedUi.TryClaim());
    }

    [TestMethod]
    public void UiHandleBridgesMaintenanceCoreReplacement()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        VersionCheckLifecycleRegistration firstCore = CreateCoreOwner(profile);
        using var currentUi = new AutomaticVersionCheckLifecycleGate(profile);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.Claimed,
            currentUi.TryClaim());

        firstCore.Dispose();
        using VersionCheckLifecycleRegistration replacementCore =
            CreateCoreOwner(profile);
        currentUi.Dispose();

        using var reopenedUi = new AutomaticVersionCheckLifecycleGate(profile);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.AlreadyClaimed,
            reopenedUi.TryClaim());
    }

    [TestMethod]
    public void FullExitResetsAutomaticClaimForNextLifecycle()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        using (VersionCheckLifecycleRegistration firstCore =
               CreateCoreOwner(profile))
        using (var firstUi = new AutomaticVersionCheckLifecycleGate(profile))
        {
            Assert.AreEqual(
                AutomaticVersionCheckClaimResult.Claimed,
                firstUi.TryClaim());
        }

        Assert.IsFalse(IsLifecycleStatePresent(profile));
        using VersionCheckLifecycleRegistration nextCore =
            CreateCoreOwner(profile);
        using var nextUi = new AutomaticVersionCheckLifecycleGate(profile);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.Claimed,
            nextUi.TryClaim());
    }

    [TestMethod]
    public void LifecycleStateIsIsolatedByProfile()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile first = CreateStableProfile(
            directory,
            "first");
        AgenTallyRuntimeProfile second = CreateStableProfile(
            directory,
            "second");
        using VersionCheckLifecycleRegistration owner = CreateCoreOwner(first);
        using var unrelatedUi = new AutomaticVersionCheckLifecycleGate(second);

        Assert.AreNotEqual(
            first.VersionCheckLifecycleEventName,
            second.VersionCheckLifecycleEventName);
        Assert.AreEqual(
            AutomaticVersionCheckClaimResult.Unavailable,
            unrelatedUi.TryClaim());
    }

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

    private static AgenTallyRuntimeProfile CreateStableProfile(
        TestTempDirectory directory,
        string suffix = "default") =>
        AgenTallyRuntimeProfile.CreateStable(
            directory.File($"app-{suffix}"),
            directory.File($"local-{suffix}"),
            directory.File($"user-{suffix}"));

    private static VersionCheckLifecycleRegistration CreateCoreOwner(
        AgenTallyRuntimeProfile profile) =>
        VersionCheckLifecycleRegistration.TryCreateCoreOwner(profile) ??
        throw new AssertFailedException(
            "Stable Core should own the version-check lifecycle state.");

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
}
