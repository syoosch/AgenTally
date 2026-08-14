using System.Collections.ObjectModel;
using System.Globalization;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;

namespace AgenTally.UI.ViewModels;

public sealed record ProjectFilterOption(
    string SelectionValue,
    string? ProjectId,
    string DisplayText,
    string ToolTipText);

public enum PricePresentationState
{
    Unavailable = 0,
    NoData = 1,
    Complete = 2,
    Partial = 3,
    Unpriced = 4
}

public sealed record PricePresentation(
    string ValueText,
    string Caption,
    PricePresentationState State);

internal static class UsageFilterPresentation
{
    public static ObservableCollection<ProjectFilterOption> CreateProjectOptions(
        IReadOnlyList<ProjectFilterValue> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var options = new ObservableCollection<ProjectFilterOption>
        {
            new(
                DashboardViewModel.AllProjects,
                null,
                DashboardViewModel.AllProjects,
                DashboardViewModel.AllProjects)
        };
        foreach (ProjectFilterValue project in projects
                     .DistinctBy(static value => value.ProjectId))
        {
            string displayText =
                project.PathAvailability == PathAvailability.Available &&
                !string.IsNullOrWhiteSpace(project.ProjectPath)
                    ? project.ProjectPath
                    : $"项目 {project.ProjectId}（路径不可取得）";
            options.Add(new ProjectFilterOption(
                project.ProjectId,
                project.ProjectId,
                displayText,
                displayText));
        }

        return options;
    }
}

internal static class PricePresentationFormatter
{
    public static PricePresentation Describe(PricingAggregate? pricing)
    {
        if (pricing is null)
        {
            return new PricePresentation(
                "—",
                "计价信息不可取得",
                PricePresentationState.Unavailable);
        }

        return pricing.Coverage switch
        {
            PricingCoverageStatus.NoData => new PricePresentation(
                "—",
                "暂无数据",
                PricePresentationState.NoData),
            PricingCoverageStatus.Complete when pricing.KnownAmountUsd is decimal amount =>
                new PricePresentation(
                    FormatUsd(amount),
                    "完整计价 · 非实际账单",
                    PricePresentationState.Complete),
            PricingCoverageStatus.Complete => new PricePresentation(
                "—",
                "计价信息不可取得",
                PricePresentationState.Unavailable),
            PricingCoverageStatus.Partial when pricing.KnownAmountUsd is decimal amount =>
                new PricePresentation(
                    FormatUsd(amount),
                    "部分计价 · 仅含已知金额",
                    PricePresentationState.Partial),
            PricingCoverageStatus.Partial => new PricePresentation(
                "—",
                "部分计价 · 无已知金额",
                PricePresentationState.Partial),
            PricingCoverageStatus.Unpriced => new PricePresentation(
                "未计价",
                "缺少适用价格",
                PricePresentationState.Unpriced),
            _ => new PricePresentation(
                "—",
                "计价信息不可取得",
                PricePresentationState.Unavailable)
        };
    }

    public static string FormatUsd(decimal usd)
    {
        if (usd <= 0m)
        {
            return "$0.00";
        }

        string format = usd >= 0.01m ? "0.00" : "0.0000";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"${usd.ToString(format, CultureInfo.InvariantCulture)}");
    }
}
