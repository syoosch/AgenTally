using AgenTally.Domain.Usage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Domain;

[TestClass]
public sealed class ModelIdentityCanonicalizerTests
{
    [TestMethod]
    public void Canonicalize_RemovesOnlyConfirmedSourceOrProviderNamespaces()
    {
        Assert.AreEqual(
            "kimi-k3-256k",
            ModelIdentityCanonicalizer.Canonicalize(
                " KIMI-CODE/K3-256K ",
                "kimi-code"));
        Assert.AreEqual(
            "claude-opus-4-6",
            ModelIdentityCanonicalizer.Canonicalize(
                "anthropic/claude-opus-4-6",
                "workbuddy",
                "anthropic"));
        Assert.AreEqual(
            "custom/deployment-blue",
            ModelIdentityCanonicalizer.Canonicalize(
                "custom/deployment-blue",
                "workbuddy",
                "anthropic"));
    }

    [TestMethod]
    public void Canonicalize_MergesKimiFamilyShorthandWithoutDroppingVariants()
    {
        Assert.AreEqual(
            "kimi-k3",
            ModelIdentityCanonicalizer.Canonicalize("k3"));
        Assert.AreEqual(
            "kimi-k3-256k",
            ModelIdentityCanonicalizer.Canonicalize("k3-256k"));
        Assert.AreEqual(
            "kimi-k2.7-code",
            ModelIdentityCanonicalizer.Canonicalize("k2.7-code"));
        Assert.AreNotEqual(
            ModelIdentityCanonicalizer.Canonicalize("k3"),
            ModelIdentityCanonicalizer.Canonicalize("k3-256k"));
    }

    [TestMethod]
    public void Canonicalize_DoesNotGuessSubscriptionRouteAliases()
    {
        Assert.AreEqual(
            "kimi-for-coding",
            ModelIdentityCanonicalizer.Canonicalize(
                "kimi-code/kimi-for-coding",
                "kimi-code"));
    }

    [TestMethod]
    public void Canonicalize_UsesSeparatelyReviewedExactAliases()
    {
        Assert.AreEqual(
            "local-reviewed-model-aliases-v1",
            ModelIdentityCanonicalizer.ReviewedAliasCatalogVersion);
        Assert.AreEqual(4, ModelIdentityCanonicalizer.ReviewedAliasCount);
        Assert.AreEqual(3, ModelIdentityCanonicalizer.ReviewedGlobalAliasCount);
        Assert.AreEqual(1, ModelIdentityCanonicalizer.ReviewedSourceAliasCount);

        Assert.AreEqual(
            "qwen3.8-max",
            ModelIdentityCanonicalizer.Canonicalize("qmodel_38max"));
        Assert.AreEqual(
            "kimi-k2.6-agent",
            ModelIdentityCanonicalizer.Canonicalize("k2d6-agent"));
        Assert.AreEqual(
            "deepseek-v4-pro",
            ModelIdentityCanonicalizer.Canonicalize("DeepSeek-V4 Pro"));

        Assert.AreNotEqual(
            ModelIdentityCanonicalizer.Canonicalize("k2d6-agent"),
            ModelIdentityCanonicalizer.Canonicalize("kimi-k2.6"));
        Assert.AreNotEqual(
            ModelIdentityCanonicalizer.Canonicalize("k3-agent"),
            ModelIdentityCanonicalizer.Canonicalize("k3-256k"));
    }

    [TestMethod]
    public void ReviewedSourceAlias_RequiresItsExactAgentScope()
    {
        Assert.IsTrue(
            ModelIdentityCanonicalizer.TryResolveReviewedSourceAlias(
                "workbuddy",
                "MiniMax-M3-Play",
                out string? canonical));
        Assert.AreEqual("minimax-m3", canonical);
        Assert.IsFalse(
            ModelIdentityCanonicalizer.TryResolveReviewedSourceAlias(
                "qoder",
                "minimax-m3-play",
                out _));
        Assert.AreEqual(
            "minimax-m3-play",
            ModelIdentityCanonicalizer.Canonicalize(
                "minimax-m3-play",
                "workbuddy"));
    }

    [TestMethod]
    public void Canonicalize_UsesGeneratedExactCatalogWithoutDisplayingLabPrefix()
    {
        Assert.AreEqual("market-models-2026-08-10-r2",
            ModelIdentityCanonicalizer.CatalogVersion);
        Assert.AreEqual(2, ModelIdentityCanonicalizer.CatalogDataSourceCount);
        Assert.AreEqual(2, ModelIdentityCanonicalizer.CatalogReferenceProjectCount);
        Assert.IsTrue(ModelIdentityCanonicalizer.CatalogModelCount >= 750);
        Assert.IsTrue(ModelIdentityCanonicalizer.CatalogAliasCount >= 4_200);
        Assert.IsTrue(
            ModelIdentityCanonicalizer.CatalogCorroboratedAliasCount >= 200);

        Assert.AreEqual(
            "qwen3.8-max",
            ModelIdentityCanonicalizer.Canonicalize(
                "alibaba/qwen3.8-max"));
        Assert.AreEqual(
            "gpt-4o",
            ModelIdentityCanonicalizer.Canonicalize(
                "openrouter/openai/gpt-4o"));
        Assert.AreEqual(
            "claude-opus-4-6",
            ModelIdentityCanonicalizer.Canonicalize(
                "anthropic/claude-opus-4-6"));
        Assert.AreEqual(
            "gpt-5.5",
            ModelIdentityCanonicalizer.Canonicalize(
                "azure_ai/gpt-5.5"));

        Assert.AreEqual(
            "unlisted-lab/deployment-blue",
            ModelIdentityCanonicalizer.Canonicalize(
                "unlisted-lab/deployment-blue"));
    }
}
