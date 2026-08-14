using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class PriceEstimatorTests
{
    [TestMethod]
    public void Catalog_PrefersMaintainedRulesAndIncludesUpstreamFallbacks()
    {
        OfflinePriceCatalog catalog = OfflinePriceCatalog.Default;

        Assert.IsTrue(catalog.TryResolve("gpt-5.6", out ResolvedPriceRule? alias));
        Assert.IsTrue(catalog.TryResolve(
            "gpt-5.6-sol",
            out ResolvedPriceRule? sol));
        Assert.IsTrue(catalog.TryResolve(
            "gpt-5.6-terra",
            out ResolvedPriceRule? terra));
        Assert.IsTrue(catalog.TryResolve(
            "gpt-5.6-luna",
            out ResolvedPriceRule? luna));
        Assert.IsTrue(catalog.TryResolve(
            "gpt-5.3-codex",
            out ResolvedPriceRule? codex));
        Assert.IsTrue(catalog.TryResolve(
            "deepseek-v4-flash",
            out ResolvedPriceRule? deepSeekFlash));
        Assert.IsTrue(catalog.TryResolve(
            "deepseek-v4-pro",
            out ResolvedPriceRule? deepSeekPro));
        Assert.IsFalse(catalog.TryResolve("internal-model", out _));
        Assert.AreEqual(sol?.RuleId, alias?.RuleId);
        Assert.AreEqual(5m, sol?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.5m, sol?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(6.25m, sol?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(30m, sol?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(2.5m, terra?.Rate.InputUsdPerMillion);
        Assert.AreEqual(1m, luna?.Rate.InputUsdPerMillion);
        Assert.AreEqual(1.75m, codex?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.175m, codex?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(14m, codex?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(0.14m, deepSeekFlash?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.0028m, deepSeekFlash?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(0.14m, deepSeekFlash?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(0.28m, deepSeekFlash?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(0.435m, deepSeekPro?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.003625m, deepSeekPro?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(0.435m, deepSeekPro?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(0.87m, deepSeekPro?.Rate.OutputUsdPerMillion);

        Assert.IsTrue(catalog.TryResolve("claude-sonnet-4-6", out var sonnet));
        Assert.AreEqual(3m, sonnet?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.3m, sonnet?.Rate.CachedInputUsdPerMillion);
        Assert.IsNull(sonnet?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(15m, sonnet?.Rate.OutputUsdPerMillion);
        Assert.IsTrue(catalog.TryResolve(
            "claude-haiku-4-5-20251001",
            out var haiku));
        Assert.AreEqual(1m, haiku?.Rate.InputUsdPerMillion);
        Assert.AreEqual(5m, haiku?.Rate.OutputUsdPerMillion);

        Assert.AreEqual(
            "openai/gpt-5.6-sol",
            sol?.RuleId,
            "AgenTally's maintained rule must shadow the upstream snapshot.");
        Assert.IsTrue(catalog.TryResolve("gpt-4o", out var gpt4o));
        Assert.AreEqual("models.dev:openai/gpt-4o", gpt4o?.RuleId);
        Assert.AreEqual(2.5m, gpt4o?.Rate.InputUsdPerMillion);
        Assert.AreEqual(10m, gpt4o?.Rate.OutputUsdPerMillion);
        Assert.IsTrue(catalog.TryResolve(
            "chatgpt-4o-latest",
            out var liteLlmFallback));
        Assert.AreEqual(
            "litellm:chatgpt-4o-latest",
            liteLlmFallback?.RuleId);
    }

    [TestMethod]
    public void Catalog_UsesCanonicalIdentityBeforeUpstreamPriceLookup()
    {
        Assert.AreEqual(
            "qwen3.8-max",
            ModelIdentityCanonicalizer.Canonicalize(
                "alibaba/qwen3.8-max"));
        Assert.IsTrue(
            OfflinePriceCatalog.Default.TryResolve(
                "qwen3.8-max",
                out ResolvedPriceRule? qwen));
        Assert.AreEqual(
            "models.dev:alibaba/qwen3.8-max",
            qwen?.RuleId);
        Assert.IsFalse(
            OfflinePriceCatalog.Default.TryResolve(
                "alibaba/qwen3.8-max",
                out _));

        var customRate = new ModelPriceRate(
            " OpenAI/GPT-5.6-Sol ",
            1m,
            null,
            null,
            2m);
        Assert.AreEqual("openai/gpt-5.6-sol", customRate.NormalizedModel);
        Assert.IsFalse(
            OfflinePriceCatalog.Default.TryResolve(
                customRate.NormalizedModel,
                out _));
        Assert.IsTrue(
            OfflinePriceCatalog.Default.TryResolve(
                ModelIdentityCanonicalizer.Canonicalize(
                    customRate.NormalizedModel,
                    providerId: "openai"),
                out _));
    }

    [TestMethod]
    public void Catalog_ReviewedPriceAliasesReuseExactOfficialApiRules()
    {
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k3",
            out ResolvedPriceRule? kimiK3));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k3-256k",
            out ResolvedPriceRule? kimiK3256k));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k2.7-code",
            out ResolvedPriceRule? kimiK27Code));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-for-coding",
            out ResolvedPriceRule? kimiForCoding));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k2.6",
            out ResolvedPriceRule? kimiK26));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k2.6-agent",
            out ResolvedPriceRule? kimiK26Agent));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "kimi-k3-agent",
            out ResolvedPriceRule? kimiK3Agent));

        Assert.AreEqual(
            kimiK3?.Rate.InputUsdPerMillion,
            kimiK3256k?.Rate.InputUsdPerMillion);
        Assert.AreEqual(
            kimiK3?.Rate.CachedInputUsdPerMillion,
            kimiK3256k?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(
            kimiK3?.Rate.OutputUsdPerMillion,
            kimiK3256k?.Rate.OutputUsdPerMillion);
        Assert.AreEqual("kimi-k3-256k", kimiK3256k?.MatchedModel);
        StringAssert.Contains(
            kimiK3256k?.RuleId,
            "models.dev:moonshotai/kimi-k3");

        Assert.AreEqual(
            kimiK27Code?.Rate.InputUsdPerMillion,
            kimiForCoding?.Rate.InputUsdPerMillion);
        Assert.AreEqual(
            kimiK27Code?.Rate.CachedInputUsdPerMillion,
            kimiForCoding?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(
            kimiK27Code?.Rate.OutputUsdPerMillion,
            kimiForCoding?.Rate.OutputUsdPerMillion);
        Assert.AreEqual("kimi-for-coding", kimiForCoding?.MatchedModel);
        StringAssert.Contains(
            kimiForCoding?.RuleId,
            "models.dev:moonshotai/kimi-k2.7-code");

        Assert.AreEqual(
            kimiK26?.Rate.InputUsdPerMillion,
            kimiK26Agent?.Rate.InputUsdPerMillion);
        Assert.AreEqual(
            kimiK26?.Rate.CachedInputUsdPerMillion,
            kimiK26Agent?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(
            kimiK26?.Rate.OutputUsdPerMillion,
            kimiK26Agent?.Rate.OutputUsdPerMillion);
        Assert.AreEqual("kimi-k2.6-agent", kimiK26Agent?.MatchedModel);
        StringAssert.Contains(
            kimiK26Agent?.RuleId,
            "models.dev:moonshotai/kimi-k2.6");

        Assert.AreEqual(
            kimiK3?.Rate.InputUsdPerMillion,
            kimiK3Agent?.Rate.InputUsdPerMillion);
        Assert.AreEqual(
            kimiK3?.Rate.CachedInputUsdPerMillion,
            kimiK3Agent?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(
            kimiK3?.Rate.OutputUsdPerMillion,
            kimiK3Agent?.Rate.OutputUsdPerMillion);
        Assert.AreEqual("kimi-k3-agent", kimiK3Agent?.MatchedModel);
        StringAssert.Contains(
            kimiK3Agent?.RuleId,
            "models.dev:moonshotai/kimi-k3");

        Assert.IsFalse(
            OfflinePriceCatalog.Default.TryResolve("hy3", out _),
            "Hy3 has an official CNY TokenHub price, not a source-backed USD rule.");
    }

    [TestMethod]
    public void Estimate_ClaudeCacheCreationKeepsKnownAmountPartial()
    {
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
            "claude-sonnet-4-6",
            out ResolvedPriceRule? rule));

        EventPriceEstimate estimate = PriceEstimator.Estimate(
            Tokens(
                inputReported: 16,
                uncachedInput: 10,
                cacheRead: 4,
                cacheWrite: 2,
                output: 7),
            rule);

        Assert.AreEqual(EventPricingStatus.Partial, estimate.Status);
        Assert.AreEqual(0.0001362m, estimate.KnownAmountUsd);
        Assert.AreEqual(
            PricingMissingCategory.CacheWriteRate,
            estimate.MissingCategories);
    }

    [TestMethod]
    public void Estimate_Gpt56UsesCacheWriteAndSeparateTokenRates()
    {
        OfflinePriceCatalog.Default.TryResolve(
            "gpt-5.6-sol",
            out ResolvedPriceRule? rule);

        EventPriceEstimate result = PriceEstimator.Estimate(
            Tokens(
                inputReported: 1_000,
                uncachedInput: 800,
                cacheRead: 200,
                cacheWrite: 50,
                output: 100),
            rule);

        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.AreEqual(PricingMissingCategory.None, result.MissingCategories);
        Assert.AreEqual(0.0074125m, result.KnownAmountUsd);
        Assert.AreEqual(1m, result.InputContextMultiplier);
        Assert.AreEqual(1m, result.OutputContextMultiplier);
    }

    [TestMethod]
    public void Estimate_Gpt56AppliesLongContextInputAndOutputMultipliers()
    {
        OfflinePriceCatalog.Default.TryResolve(
            "gpt-5.6-sol",
            out ResolvedPriceRule? rule);

        EventPriceEstimate result = PriceEstimator.Estimate(
            Tokens(
                inputReported: 300_000,
                uncachedInput: 200_000,
                cacheRead: 100_000,
                cacheWrite: 0,
                output: 10_000),
            rule);

        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.AreEqual(2.55m, result.KnownAmountUsd);
        Assert.AreEqual(2m, result.InputContextMultiplier);
        Assert.AreEqual(1.5m, result.OutputContextMultiplier);
    }

    [TestMethod]
    public void Estimate_MissingRequiredCacheWriteReturnsKnownPartialAmount()
    {
        OfflinePriceCatalog.Default.TryResolve(
            "gpt-5.6-terra",
            out ResolvedPriceRule? rule);
        TokenUsage tokens = Tokens(
            inputReported: 1_000,
            uncachedInput: 800,
            cacheRead: 200,
            cacheWrite: null,
            output: 100);

        EventPriceEstimate result = PriceEstimator.Estimate(tokens, rule);

        Assert.AreEqual(EventPricingStatus.Partial, result.Status);
        Assert.IsTrue(
            result.MissingCategories.HasFlag(
                PricingMissingCategory.CacheWriteTokens));
        Assert.IsNotNull(result.KnownAmountUsd);
    }

    [TestMethod]
    public void Estimate_Gpt53DoesNotInventUnpublishedCacheWriteRate()
    {
        OfflinePriceCatalog.Default.TryResolve(
            "gpt-5.3-codex",
            out ResolvedPriceRule? rule);

        EventPriceEstimate result = PriceEstimator.Estimate(
            Tokens(
                inputReported: 1_000,
                uncachedInput: 800,
                cacheRead: 200,
                cacheWrite: null,
                output: 100),
            rule);

        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.IsNull(result.CacheWriteUsdPerMillion);
        Assert.AreEqual(PricingMissingCategory.None, result.MissingCategories);
    }

    [TestMethod]
    public void Estimate_DeepSeekV4UsesPublishedCacheHitMissAndOutputRates()
    {
        OfflinePriceCatalog.Default.TryResolve(
            "deepseek-v4-flash",
            out ResolvedPriceRule? flashRule);
        OfflinePriceCatalog.Default.TryResolve(
            "deepseek-v4-pro",
            out ResolvedPriceRule? proRule);
        TokenUsage tokens = Tokens(
            inputReported: 3_000_000,
            uncachedInput: 1_000_000,
            cacheRead: 1_000_000,
            cacheWrite: 1_000_000,
            output: 1_000_000);

        EventPriceEstimate flash = PriceEstimator.Estimate(tokens, flashRule);
        EventPriceEstimate pro = PriceEstimator.Estimate(tokens, proRule);

        Assert.AreEqual(EventPricingStatus.Complete, flash.Status);
        Assert.AreEqual(PricingMissingCategory.None, flash.MissingCategories);
        Assert.AreEqual(0.5628m, flash.KnownAmountUsd);
        Assert.AreEqual(EventPricingStatus.Complete, pro.Status);
        Assert.AreEqual(PricingMissingCategory.None, pro.MissingCategories);
        Assert.AreEqual(1.743625m, pro.KnownAmountUsd);
    }

    [TestMethod]
    public void Estimate_UnknownModelIsUnpricedRatherThanZero()
    {
        EventPriceEstimate result =
            PriceEstimator.Estimate(Tokens(1, 1, 0, 0, 1), rule: null);

        Assert.AreEqual(EventPricingStatus.Unpriced, result.Status);
        Assert.IsNull(result.KnownAmountUsd);
        Assert.AreEqual(
            PricingMissingCategory.ModelRate,
            result.MissingCategories);
    }

    private static TokenUsage Tokens(
        long? inputReported,
        long? uncachedInput,
        long? cacheRead,
        long? cacheWrite,
        long? output) => new()
    {
        InputReported = Metric(inputReported),
        UncachedInput = Metric(uncachedInput),
        CacheRead = Metric(cacheRead),
        CacheWrite = Metric(cacheWrite),
        Output = Metric(output),
        CacheIncludedInInput = MetricInclusion.Included,
        ReasoningIncludedInOutput = MetricInclusion.Included
    };

    private static TokenMetric Metric(long? value) =>
        value.HasValue
            ? TokenMetric.Exact(value.Value)
            : TokenMetric.Unavailable;
}
