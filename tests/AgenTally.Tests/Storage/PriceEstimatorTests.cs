using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.IO;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class PriceEstimatorTests
{
    [TestMethod]
    public void Estimate_CurrentCodexModelsUseIndependentOfficialRatesAndThresholds()
    {
        (string Model, decimal Input, decimal Read, decimal Write, decimal Output,
            decimal ShortAmount, decimal BoundaryAmount, decimal LongAmount)[] cases =
        [
            ("gpt-6.1-sol", 2m, 0.1m, 2.5m, 10m,
                0.002745m, 0.507325m, 0.9646502m),
            ("gpt-6-astra", 10m, 1m, 12.5m, 50m,
                0.013825m, 2.572625m, 4.895252m),
            ("gpt-6-sol", 2m, 0.2m, 2.5m, 10m,
                0.002765m, 0.514525m, 0.9790504m),
            ("gpt-6-luna", 0.1m, 0.01m, 0.125m, 0.5m,
                0.00013825m, 0.02572625m, 0.04895252m)
        ];
        foreach (var value in cases)
        {
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(
                ModelIdentityCanonicalizer.Canonicalize($"openai/{value.Model}"),
                out ResolvedPriceRule? rule));
            Assert.IsNotNull(rule);
            Assert.AreEqual($"openai/{value.Model}", rule.RuleId);
            Assert.AreEqual(value.Input, rule.Rate.InputUsdPerMillion);
            Assert.AreEqual(value.Read, rule.Rate.CachedInputUsdPerMillion);
            Assert.AreEqual(value.Write, rule.Rate.CacheWriteUsdPerMillion);
            Assert.AreEqual(value.Output, rule.Rate.OutputUsdPerMillion);
            Assert.AreEqual(272_000L, rule.Rate.LongContextThresholdTokens);
            EventPriceEstimate shortContext = PriceEstimator.Estimate(
                Tokens(1_000, 800, 200, 50, 100), rule);
            EventPriceEstimate boundary = PriceEstimator.Estimate(
                Tokens(272_000, 200_000, 72_000, 50, 10_000), rule);
            EventPriceEstimate longContext = PriceEstimator.Estimate(
                Tokens(272_001, 200_000, 72_001, 50, 10_000), rule);
            Assert.AreEqual(EventPricingStatus.Complete, shortContext.Status);
            Assert.AreEqual(value.ShortAmount, shortContext.KnownAmountUsd);
            Assert.AreEqual(1m, boundary.InputContextMultiplier);
            Assert.AreEqual(1m, boundary.OutputContextMultiplier);
            Assert.AreEqual(value.BoundaryAmount, boundary.KnownAmountUsd);
            Assert.AreEqual(2m, longContext.InputContextMultiplier);
            Assert.AreEqual(1.5m, longContext.OutputContextMultiplier);
            Assert.AreEqual(value.LongAmount, longContext.KnownAmountUsd);
            EventPriceEstimate missingWrite = PriceEstimator.Estimate(
                Tokens(1_000, 800, 200, null, 100), rule);
            Assert.AreEqual(EventPricingStatus.Partial, missingWrite.Status);
            Assert.AreEqual(PricingMissingCategory.CacheWriteTokens,
                missingWrite.MissingCategories);
            Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve(
                $"{value.Model}-preview", out _));
        }
        Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve("gpt-reserve", out _));
        Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve("codex-auto-review", out _));
    }

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
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            out ResolvedPriceRule? deepSeekFlash));
        Assert.IsTrue(catalog.TryResolve(
            "deepseek-v4-pro",
            out ResolvedPriceRule? deepSeekPro));
        Assert.IsFalse(catalog.TryResolve("internal-model", out _));
        Assert.AreEqual(sol?.RuleId, alias?.RuleId);
        Assert.AreEqual(4m, sol?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.4m, sol?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(5m, sol?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(20m, sol?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(2m, terra?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.2m, luna?.Rate.InputUsdPerMillion);
        Assert.AreEqual(1.75m, codex?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.175m, codex?.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(14m, codex?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(0.14m, deepSeekFlash?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.0028m, deepSeekFlash?.Rate.CachedInputUsdPerMillion);
        Assert.IsNull(deepSeekFlash?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(0.28m, deepSeekFlash?.Rate.OutputUsdPerMillion);
        Assert.AreEqual(0.66m, deepSeekPro?.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.022m, deepSeekPro?.Rate.CachedInputUsdPerMillion);
        Assert.IsNull(deepSeekPro?.Rate.CacheWriteUsdPerMillion);
        Assert.AreEqual(1.98m, deepSeekPro?.Rate.OutputUsdPerMillion);
        Assert.IsNotNull(deepSeekPro?.PeakOffPeak);

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
            rule, DateTimeOffset.UtcNow);

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
            rule, DateTimeOffset.UtcNow);

        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.AreEqual(PricingMissingCategory.None, result.MissingCategories);
        Assert.AreEqual(0.00553m, result.KnownAmountUsd);
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
            rule, DateTimeOffset.UtcNow);

        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.AreEqual(1.98m, result.KnownAmountUsd);
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

        EventPriceEstimate result = PriceEstimator.Estimate(tokens, rule, DateTimeOffset.UtcNow);

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
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
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

        EventPriceEstimate flash = PriceEstimator.Estimate(tokens, flashRule,
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        EventPriceEstimate pro = PriceEstimator.Estimate(tokens, proRule,
            new DateTimeOffset(2026, 10, 19, 4, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(EventPricingStatus.Partial, flash.Status);
        Assert.AreEqual(PricingMissingCategory.CacheWriteRate, flash.MissingCategories);
        Assert.AreEqual(0.4228m, flash.KnownAmountUsd);
        Assert.AreEqual(EventPricingStatus.Partial, pro.Status);
        Assert.AreEqual(PricingMissingCategory.CacheWriteRate, pro.MissingCategories);
        Assert.AreEqual(2.662m, pro.KnownAmountUsd);
    }

    [TestMethod]
    public void Estimate_DeepSeekPeakScheduleUsesCallInstantBoundariesAndChineseHolidays()
    {
        (string Timestamp, bool Peak)[] instants =
        [
            ("2026-10-19T00:59:59.999Z", false),
            ("2026-10-19T01:00:00Z", true),
            ("2026-10-19T03:59:59.999Z", true),
            ("2026-10-19T04:00:00Z", false),
            ("2026-10-19T05:59:59.999Z", false),
            ("2026-10-19T06:00:00Z", true),
            ("2026-10-19T09:59:59.999Z", true),
            ("2026-10-19T10:00:00Z", false),
            ("2026-10-16T06:00:00Z", true),
            ("2026-10-17T02:00:00Z", false),
            ("2026-10-18T06:00:00Z", false),
            ("2026-10-10T02:00:00Z", false), // Make-up working Saturday.
            ("2026-01-02T06:00:00Z", false),
            ("2026-02-23T02:00:00Z", false),
            ("2026-04-06T02:00:00Z", false),
            ("2026-05-05T06:00:00Z", false),
            ("2026-06-19T02:00:00Z", false),
            ("2026-09-25T06:00:00Z", false),
            ("2026-10-01T02:00:00Z", false),
            ("2026-10-07T06:00:00Z", false),
            ("2026-10-18T21:00:00-04:00", true)
        ];
        foreach ((string model, decimal amount, decimal input, decimal cached,
                     decimal output) in new[]
                 {
                     ("deepseek-flash", 0.753m, 0.15m, 0.003m, 0.6m),
                     ("deepseek-v4-pro", 2.662m, 0.66m, 0.022m, 1.98m)
                 })
        {
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, out var rule));
            // Exercise the recurring calendar independently of this API ID's
            // introduction date; the dated catalog is checked separately.
            rule = rule! with { EffectiveFromUtc = null, EffectiveUntilUtc = null };
            foreach ((string timestamp, bool peak) in instants)
            {
                EventPriceEstimate result = PriceEstimator.Estimate(
                    Tokens(2_000_000, 1_000_000, 1_000_000, 0, 1_000_000), rule,
                    DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture));
                decimal factor = peak ? 2m : 1m;
                Assert.AreEqual(EventPricingStatus.Complete, result.Status, timestamp);
                Assert.AreEqual(amount * factor, result.KnownAmountUsd, timestamp);
                Assert.AreEqual(input * factor, result.InputUsdPerMillion, timestamp);
                Assert.AreEqual(cached * factor, result.CachedInputUsdPerMillion, timestamp);
                Assert.AreEqual(output * factor, result.OutputUsdPerMillion, timestamp);
                Assert.AreEqual($"deepseek/{model}/{(peak ? "peak" : "off-peak")}",
                    result.RuleId, timestamp);
            }
        }
    }

    [TestMethod]
    public void Estimate_DeepSeekMissingTimeOrHolidayYearDoesNotGuessPeakPrice()
    {
        foreach (string model in new[] { "deepseek-flash", "deepseek-v4-pro" })
        {
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, out var rule));
            TokenUsage tokens = Tokens(1_000, 800, 200, 0, 100);
            foreach (DateTimeOffset? timestamp in new DateTimeOffset?[]
                     { null, new(2027, 1, 4, 1, 0, 0, TimeSpan.Zero) })
            {
                EventPriceEstimate result = PriceEstimator.Estimate(tokens, rule, timestamp);
                Assert.AreEqual(EventPricingStatus.Unpriced, result.Status);
                Assert.IsNull(result.KnownAmountUsd);
                Assert.IsNull(result.InputUsdPerMillion);
                Assert.IsTrue(result.MissingCategories is PricingMissingCategory.PriceTimeSchedule
                    or PricingMissingCategory.PriceEffectivePeriod);
            }

            // Outside a weekday peak window the holiday calendar cannot
            // change the rate, even if that year's calendar is unavailable.
            Assert.AreEqual(EventPricingStatus.Complete,
                PriceEstimator.Estimate(tokens, rule,
                    new DateTimeOffset(2027, 1, 2, 1, 0, 0, TimeSpan.Zero)).Status);
            Assert.AreEqual(EventPricingStatus.Complete,
                PriceEstimator.Estimate(tokens, rule,
                    new DateTimeOffset(2027, 1, 4, 4, 0, 0, TimeSpan.Zero)).Status);
        }
        Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve("deepseek-pro-preview", out _));
    }

    [TestMethod]
    public void Catalog_EffectivePeriodsUseOccurrenceBoundariesBeforePeakSelectionAndRejectOverlap()
    {
        DateTimeOffset change = new(2026, 10, 19, 1, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("deepseek-flash", out var flash));
        Assert.IsNotNull(flash);
        var before = new ResolvedPriceRule("period-v1", "before", "timed-model",
            new ModelPriceRate("timed-model", 1m, 0m, null, 2m), flash.PeakOffPeak,
            EffectiveUntilUtc: change);
        var after = new ResolvedPriceRule("period-v1", "after", "timed-model",
            new ModelPriceRate("timed-model", 2m, 0m, null, 4m), flash.PeakOffPeak,
            EffectiveFromUtc: change, EffectiveUntilUtc: change.AddDays(1));
        var catalog = new OfflinePriceCatalog("period-v1", [after, before]);
        TokenUsage tokens = Tokens(1_000_000, 1_000_000, 0, 0, 1_000_000);
        Assert.IsTrue(catalog.TryResolve("timed-model", change.AddMilliseconds(-1), out var oldRule));
        Assert.AreEqual("before", oldRule?.RuleId);
        Assert.AreEqual(3m, PriceEstimator.Estimate(tokens, oldRule, change.AddMilliseconds(-1)).KnownAmountUsd);
        Assert.IsTrue(catalog.TryResolve("timed-model", change, out var newRule));
        EventPriceEstimate result = PriceEstimator.Estimate(tokens, newRule, change);
        Assert.AreEqual(12m, result.KnownAmountUsd);
        Assert.AreEqual("after/peak", result.RuleId);
        Assert.IsFalse(catalog.TryResolve("timed-model", change.AddDays(1), out _));
        Assert.AreEqual(EventPricingStatus.Unpriced, PriceEstimator.Estimate(tokens, newRule).Status);
        Assert.AreEqual(EventPricingStatus.Unpriced, PriceEstimator.Estimate(tokens, newRule,
            change.AddMilliseconds(-1)).Status);
        Assert.ThrowsExactly<InvalidDataException>(() => new OfflinePriceCatalog("bad",
            [before with { EffectiveUntilUtc = change.AddSeconds(1) }, after]));
        Assert.ThrowsExactly<InvalidDataException>(() => new OfflinePriceCatalog("bad",
            [after with { EffectiveUntilUtc = change }]));
        Assert.ThrowsExactly<InvalidDataException>(() => new OfflinePriceCatalog("bad",
            [after with { EffectiveFromUtc = change.ToOffset(TimeSpan.FromHours(8)) }]));
        Assert.ThrowsExactly<InvalidDataException>(() => new OfflinePriceCatalog("bad",
            [after with { EffectiveFromBasis = "official-date-at-utc-midnight" }]));
        Assert.ThrowsExactly<InvalidDataException>(() => new OfflinePriceCatalog("bad",
            [before with { EffectiveFromBasis = "official-utc-instant" }]));
    }

    [TestMethod]
    public void Catalog_DeepSeekPublishedCutoversKeepLegacyPriceThenDailyAndWeekdaySchedules()
    {
        var august = new DateTimeOffset(2026, 8, 16, 16, 0, 0, TimeSpan.Zero);
        var september = new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero);
        var catalog = OfflinePriceCatalog.Default;
        Assert.IsTrue(catalog.TryResolve("deepseek-v4-flash", august.AddMilliseconds(-1), out var old));
        Assert.AreEqual(0.14m, old?.Rate.InputUsdPerMillion);
        Assert.IsTrue(catalog.TryResolve("deepseek-v4-flash", august, out var daily));
        Assert.AreEqual(0.22m, daily?.Rate.InputUsdPerMillion);
        Assert.IsFalse(daily?.PeakOffPeak?.WeekdaysOnly);
        var saturday = new DateTimeOffset(2026, 8, 22, 1, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(0.44m, PriceEstimator.Estimate(Tokens(1, 1, 0, 0, 1), daily, saturday).InputUsdPerMillion);
        Assert.IsTrue(catalog.TryResolve("deepseek-v4-flash", september.AddMilliseconds(-1), out var before));
        Assert.IsTrue(before?.PeakOffPeak?.WeekdaysOnly);
        Assert.AreEqual(0.22m, before?.Rate.InputUsdPerMillion);
        Assert.IsTrue(catalog.TryResolve("deepseek-v4-flash", september, out var weekly));
        Assert.AreEqual(0.15m, weekly?.Rate.InputUsdPerMillion);
        Assert.IsTrue(weekly?.PeakOffPeak?.WeekdaysOnly);
        Assert.IsFalse(catalog.TryResolve("deepseek-flash", september.AddMilliseconds(-1), out _));
        Assert.IsTrue(catalog.TryResolve("deepseek-flash", september, out _));
    }

    [TestMethod]
    public void Catalog_OfficialHistoryUsesPreChangeBaselinesAndExactCutovers()
    {
        (string Model, string Change, decimal OldInput, decimal NewInput,
            decimal OldOutput, decimal NewOutput)[] cases =
        [
            ("gpt-5.6-sol", "2026-08-21T00:00:00Z", 5m, 4m, 30m, 20m),
            ("gpt-5.6", "2026-08-21T00:00:00Z", 5m, 4m, 30m, 20m),
            ("gpt-5.6-terra", "2026-07-30T00:00:00Z", 2.5m, 2m, 15m, 12m),
            ("gpt-5.6-luna", "2026-07-30T00:00:00Z", 1m, 0.2m, 6m, 1.2m),
            ("o3", "2025-06-10T00:00:00Z", 10m, 2m, 40m, 8m),
            ("o3-2025-04-16", "2025-06-10T00:00:00Z", 10m, 2m, 40m, 8m),
            ("codestral-2405", "2024-09-17T00:00:00Z", 1m, 0.2m, 3m, 0.6m),
            ("mistral-large-2407", "2024-09-17T00:00:00Z", 3m, 2m, 9m, 6m),
            ("open-mistral-nemo-2407", "2024-09-17T00:00:00Z", 0.3m, 0.15m, 0.3m, 0.15m)
        ];
        foreach (var item in cases)
        {
            DateTimeOffset at = DateTimeOffset.Parse(item.Change, CultureInfo.InvariantCulture);
            foreach (DateTimeOffset earlier in new[] { DateTimeOffset.UnixEpoch, at.AddMilliseconds(-1) })
            {
                Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(item.Model, earlier, out var old));
                Assert.AreEqual(item.OldInput, old!.Rate.InputUsdPerMillion, item.Model);
                Assert.AreEqual(item.OldOutput, old.Rate.OutputUsdPerMillion, item.Model);
                Assert.IsNull(old.EffectiveFromUtc);
            }
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(item.Model, at, out var current));
            Assert.AreEqual(item.NewInput, current!.Rate.InputUsdPerMillion, item.Model);
            Assert.AreEqual(item.NewOutput, current.Rate.OutputUsdPerMillion, item.Model);
            Assert.IsNotNull(current.EffectiveDateSource);
        }
    }

    [TestMethod]
    public void Catalog_OfficialContextAndScheduledFuturePricesKeepBothSides()
    {
        var march = new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero);
        foreach (string model in new[] { "claude-opus-4-6", "claude-opus-4-6-20260205", "claude-sonnet-4-6" })
        {
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, march.AddMilliseconds(-1), out var old));
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, march, out var current));
            TokenUsage tokens = Tokens(200_001, 200_001, 0, 0, 10);
            EventPriceEstimate before = PriceEstimator.Estimate(tokens, old, march.AddMilliseconds(-1));
            EventPriceEstimate after = PriceEstimator.Estimate(tokens, current, march);
            Assert.AreEqual(2m, before.InputContextMultiplier);
            Assert.AreEqual(1.5m, before.OutputContextMultiplier);
            Assert.AreEqual(1m, after.InputContextMultiplier);
            Assert.AreEqual(1m, after.OutputContextMultiplier);
        }
        var february = new DateTimeOffset(2026, 2, 22, 16, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("qwen3.5-plus", february.AddMilliseconds(-1), out var qwenOld));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("qwen3.5-plus", february, out var qwenNew));
        Assert.AreEqual(1.2m, qwenOld!.Rate.InputUsdPerMillion * qwenOld.Rate.LongContextInputMultiplier);
        Assert.AreEqual(7.2m, qwenOld.Rate.OutputUsdPerMillion * qwenOld.Rate.LongContextOutputMultiplier);
        Assert.AreEqual(0.5m, qwenNew!.Rate.InputUsdPerMillion * qwenNew.Rate.LongContextInputMultiplier);
        Assert.AreEqual(3m, qwenNew.Rate.OutputUsdPerMillion * qwenNew.Rate.LongContextOutputMultiplier);
        var january = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("gemini-3.6-flash", january.AddMilliseconds(-1), out var introductory));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("gemini-3.6-flash", january, out var standard));
        Assert.AreEqual(0.75m, introductory!.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.075m, introductory.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(3.75m, introductory.Rate.OutputUsdPerMillion);
        Assert.AreEqual(1.5m, standard!.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.15m, standard.Rate.CachedInputUsdPerMillion);
        Assert.AreEqual(7.5m, standard.Rate.OutputUsdPerMillion);
    }

    [TestMethod]
    public void Catalog_DeepSeekHistoryKeepsPromotionsCacheCutsAndWeekendActivation()
    {
        (string At, decimal Input, decimal Cached, decimal Output)[] pro =
        [
            ("1970-01-01T00:00:00Z", 1.74m, 0.145m, 3.48m),
            ("2026-04-24T23:59:59.999Z", 1.74m, 0.145m, 3.48m),
            ("2026-04-25T00:00:00Z", 0.435m, 0.03625m, 0.87m),
            ("2026-04-26T16:35:10.999Z", 0.435m, 0.03625m, 0.87m),
            ("2026-04-26T16:35:11Z", 0.435m, 0.003625m, 0.87m),
            ("2026-06-01T00:00:00Z", 0.435m, 0.003625m, 0.87m),
            ("2026-08-22T01:00:00Z", 1.32m, 0.044m, 3.96m),
            ("2026-08-23T01:00:00Z", 0.66m, 0.022m, 1.98m)
        ];
        foreach (var item in pro)
        {
            DateTimeOffset at = DateTimeOffset.Parse(item.At, CultureInfo.InvariantCulture);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("deepseek-v4-pro", at, out var rule));
            EventPriceEstimate result = PriceEstimator.Estimate(Tokens(2, 1, 1, 0, 1), rule, at);
            Assert.AreEqual(item.Input, result.InputUsdPerMillion, item.At);
            Assert.AreEqual(item.Cached, result.CachedInputUsdPerMillion, item.At);
            Assert.AreEqual(item.Output, result.OutputUsdPerMillion, item.At);
        }
        var cut = new DateTimeOffset(2026, 4, 26, 16, 35, 11, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("deepseek-v4-flash", cut.AddMilliseconds(-1), out var old));
        Assert.AreEqual(0.028m, old!.Rate.CachedInputUsdPerMillion);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("deepseek-v4-flash", cut, out var discounted));
        Assert.AreEqual(0.0028m, discounted!.Rate.CachedInputUsdPerMillion);
        foreach (string model in new[] { "deepseek-chat", "deepseek-reasoner" })
        {
            var retired = new DateTimeOffset(2026, 7, 24, 16, 0, 0, TimeSpan.Zero);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, retired.AddMilliseconds(-1), out _));
            Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve(model, retired, out _));
        }
    }

    [TestMethod]
    public void Estimate_HistoricalDeepSeekNightDiscountUsesPublishedRoundedRatesAcrossMidnight()
    {
        (string At, decimal Input, decimal Cached, decimal Output)[] cases =
        [
            ("2025-02-26T00:15:00Z", 0.55m, 0.14m, 2.19m),
            ("2025-02-26T16:29:59.999Z", 0.55m, 0.14m, 2.19m),
            ("2025-02-26T16:30:00Z", 0.135m, 0.035m, 0.55m),
            ("2025-02-27T00:29:59.999Z", 0.135m, 0.035m, 0.55m),
            ("2025-02-27T00:30:00Z", 0.55m, 0.14m, 2.19m),
            ("2025-02-27T16:29:59.999Z", 0.55m, 0.14m, 2.19m)
        ];
        foreach (string model in new[] { "deepseek-reasoner", "deepseek-r1" })
        foreach (var item in cases)
        {
            DateTimeOffset at = DateTimeOffset.Parse(item.At, CultureInfo.InvariantCulture);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, at, out var rule));
            EventPriceEstimate result = PriceEstimator.Estimate(Tokens(2, 1, 1, 0, 1), rule, at);
            Assert.AreEqual(item.Input, result.InputUsdPerMillion, item.At);
            Assert.AreEqual(item.Cached, result.CachedInputUsdPerMillion, item.At);
            Assert.AreEqual(item.Output, result.OutputUsdPerMillion, item.At);
        }
    }

    [TestMethod]
    public void Catalog_FreeTrialsAndKimiPromotionExpiryUseOfficialDates()
    {
        (string At, decimal Input, decimal Cached, decimal Output)[] kimi =
        [
            ("1970-01-01T00:00:00Z", 1.2m, 0.3m, 5m),
            ("2025-09-01T00:00:00Z", 2.4m, 0.6m, 10m),
            ("2025-09-16T00:00:00Z", 1.2m, 0.3m, 5m),
            ("2025-10-15T23:59:59.999Z", 1.2m, 0.3m, 5m),
            ("2025-10-16T00:00:00Z", 2.4m, 0.6m, 10m),
            ("2025-11-06T00:00:00Z", 1.15m, 0.15m, 8m)
        ];
        foreach (var item in kimi)
        {
            var at = DateTimeOffset.Parse(item.At, CultureInfo.InvariantCulture);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("kimi-k2-turbo-preview", at, out var rule));
            Assert.AreEqual(item.Input, rule!.Rate.InputUsdPerMillion);
            Assert.AreEqual(item.Cached, rule.Rate.CachedInputUsdPerMillion);
            Assert.AreEqual(item.Output, rule.Rate.OutputUsdPerMillion);
        }
        foreach (var item in new[]
                 {
                     (Model: "minimax-m2", At: "2025-11-07T00:00:00Z", Input: 0.3m, Output: 1.2m),
                     (Model: "devstral-2512", At: "2026-01-27T00:00:00Z", Input: 0.4m, Output: 2m)
                 })
        {
            var at = DateTimeOffset.Parse(item.At, CultureInfo.InvariantCulture);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(item.Model, at.AddMilliseconds(-1), out var free));
            Assert.AreEqual(0m, free!.Rate.InputUsdPerMillion);
            Assert.AreEqual(0m, free.Rate.OutputUsdPerMillion);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(item.Model, at, out var paid));
            Assert.AreEqual(item.Input, paid!.Rate.InputUsdPerMillion);
            Assert.AreEqual(item.Output, paid.Rate.OutputUsdPerMillion);
        }
    }

    [TestMethod]
    public void Catalog_MiMoFreeBillingContextCutAndAliasRetirementKeepTheirDates()
    {
        var billing = new DateTimeOffset(2026, 1, 26, 8, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("mimo-v2-flash", billing.AddMilliseconds(-1), out var free));
        Assert.AreEqual(0m, free!.Rate.InputUsdPerMillion);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("mimo-v2-flash", billing, out var paid));
        Assert.AreEqual(0.1m, paid!.Rate.InputUsdPerMillion);
        Assert.AreEqual(0.01m, paid.Rate.CachedInputUsdPerMillion);
        var cut = new DateTimeOffset(2026, 5, 26, 16, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("mimo-v2.5-pro", cut.AddMilliseconds(-1), out var old));
        Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve("mimo-v2.5-pro", cut, out var current));
        TokenUsage tokens = Tokens(256_001, 256_001, 0, 0, 1);
        Assert.AreEqual(2m, PriceEstimator.Estimate(tokens, old, cut.AddMilliseconds(-1)).InputContextMultiplier);
        Assert.AreEqual(2m, PriceEstimator.Estimate(tokens, old, cut.AddMilliseconds(-1)).OutputContextMultiplier);
        Assert.AreEqual(1m, PriceEstimator.Estimate(tokens, current, cut).InputContextMultiplier);
        Assert.AreEqual(0.435m, current!.Rate.InputUsdPerMillion);
        foreach (string model in new[] { "mimo-v2-flash", "mimo-v2-pro", "mimo-v2-omni" })
        {
            var end = new DateTimeOffset(2026, 6, 29, 16, 0, 0, TimeSpan.Zero);
            Assert.IsTrue(OfflinePriceCatalog.Default.TryResolve(model, end.AddMilliseconds(-1), out _));
            Assert.IsFalse(OfflinePriceCatalog.Default.TryResolve(model, end, out _));
        }
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
