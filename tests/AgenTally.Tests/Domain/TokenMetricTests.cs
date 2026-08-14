using AgenTally.Domain.Usage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Domain;

[TestClass]
public sealed class TokenMetricTests
{
    [TestMethod]
    public void ExactZero_RemainsAvailable()
    {
        TokenMetric metric = TokenMetric.Exact(0);

        Assert.AreEqual(0L, metric.Value);
        Assert.AreEqual(MetricOrigin.Exact, metric.Origin);
        Assert.IsTrue(metric.IsAvailable);
    }

    [TestMethod]
    public void Unavailable_HasNoValue()
    {
        Assert.IsNull(TokenMetric.Unavailable.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, TokenMetric.Unavailable.Origin);
    }

    [TestMethod]
    public void DefaultValue_IsUnavailable()
    {
        TokenMetric metric = default;

        Assert.IsNull(metric.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, metric.Origin);
        Assert.IsFalse(metric.IsAvailable);
    }

    [TestMethod]
    public void Constructor_RejectsNegativeValue()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new TokenMetric(-1, MetricOrigin.Exact));
    }

    [TestMethod]
    public void DerivedValue_PreservesItsOrigin()
    {
        TokenMetric metric = new(42, MetricOrigin.Derived);

        Assert.AreEqual(42L, metric.Value);
        Assert.AreEqual(MetricOrigin.Derived, metric.Origin);
        Assert.IsTrue(metric.IsAvailable);
    }
}
