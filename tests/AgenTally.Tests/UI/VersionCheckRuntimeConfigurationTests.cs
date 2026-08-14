using AgenTally.Storage.Runtime;
using AgenTally.UI.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class VersionCheckRuntimeConfigurationTests
{
    private static readonly VersionCheckConfiguration TestConfiguration =
        new(
            new Uri("https://updates.invalid/agentally/stable.json"),
            new Uri("https://releases.invalid/agentally"));

    [TestMethod]
    public void Resolve_DevelopmentNeverEvaluatesStableConfiguration()
    {
        bool factoryCalled = false;

        VersionCheckRuntimeConfiguration result =
            VersionCheckRuntimeConfigurationResolver.Resolve(
                AgenTallyChannel.Development,
                new Version(1, 2, 3, 0),
                () =>
                {
                    factoryCalled = true;
                    return TestConfiguration;
                });

        Assert.AreEqual(
            VersionCheckAvailability.DevelopmentDisabled,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
        Assert.IsNull(result.CurrentVersion);
        Assert.IsNull(result.ServiceConfiguration);
        Assert.IsFalse(factoryCalled);
    }

    [TestMethod]
    public void Resolve_UnconfiguredStableRetainsStrictCurrentVersion()
    {
        VersionCheckRuntimeConfiguration result =
            VersionCheckRuntimeConfigurationResolver.Resolve(
                AgenTallyChannel.Stable,
                new Version(1, 2, 3, 0),
                () => null);

        Assert.AreEqual(
            VersionCheckAvailability.StableChannelNotConfigured,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
        Assert.AreEqual(
            new ReleaseVersion(1, 2, 3),
            result.CurrentVersion);
        Assert.IsNull(result.ServiceConfiguration);
    }

    [TestMethod]
    public void Resolve_ConfiguredStableProvidesValidatedInputs()
    {
        VersionCheckRuntimeConfiguration result =
            VersionCheckRuntimeConfigurationResolver.Resolve(
                AgenTallyChannel.Stable,
                new Version(4, 5, 6, 0),
                () => TestConfiguration);

        Assert.AreEqual(
            VersionCheckAvailability.Available,
            result.Availability);
        Assert.IsTrue(result.CanCheck);
        Assert.AreEqual(
            new ReleaseVersion(4, 5, 6),
            result.CurrentVersion);
        Assert.AreSame(TestConfiguration, result.ServiceConfiguration);
    }

    [TestMethod]
    public void Resolve_InvalidStableVersionDoesNotEvaluateConfiguration()
    {
        bool factoryCalled = false;
        VersionCheckRuntimeConfiguration result =
            VersionCheckRuntimeConfigurationResolver.Resolve(
                AgenTallyChannel.Stable,
                new Version(1, 2, 3, 4),
                () =>
                {
                    factoryCalled = true;
                    return TestConfiguration;
                });

        Assert.AreEqual(
            VersionCheckAvailability.InvalidCurrentVersion,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
        Assert.IsNull(result.CurrentVersion);
        Assert.IsNull(result.ServiceConfiguration);
        Assert.IsFalse(factoryCalled);
    }

    [TestMethod]
    public void Resolve_RejectsIncompleteStableVersion()
    {
        VersionCheckRuntimeConfiguration result =
            VersionCheckRuntimeConfigurationResolver.Resolve(
                AgenTallyChannel.Stable,
                new Version(1, 2),
                () => TestConfiguration);

        Assert.AreEqual(
            VersionCheckAvailability.InvalidCurrentVersion,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
    }

    [TestMethod]
    public void ProductionConfiguration_LeavesStableChannelUnconfigured()
    {
        VersionCheckRuntimeConfiguration result =
            VersionCheckProductionConfiguration.Resolve(
                AgenTallyChannel.Stable,
                typeof(VersionCheckProductionConfiguration).Assembly);

        Assert.AreEqual(
            VersionCheckAvailability.StableChannelNotConfigured,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
        Assert.IsNotNull(result.CurrentVersion);
        Assert.IsNull(result.ServiceConfiguration);
    }

    [TestMethod]
    public void ProductionConfiguration_DisablesDevelopment()
    {
        VersionCheckRuntimeConfiguration result =
            VersionCheckProductionConfiguration.Resolve(
                AgenTallyChannel.Development,
                typeof(VersionCheckProductionConfiguration).Assembly);

        Assert.AreEqual(
            VersionCheckAvailability.DevelopmentDisabled,
            result.Availability);
        Assert.IsFalse(result.CanCheck);
        Assert.IsNull(result.CurrentVersion);
        Assert.IsNull(result.ServiceConfiguration);
    }
}
