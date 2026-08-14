using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public sealed class SourcesViewModel : PageViewModel
{
    private readonly IUsageQueryService _queries;
    private ObservableCollection<SourceStatusPresentation> _sourceRows = [];
    private ObservableCollection<SourceStatusRow> _sources = [];

    public SourcesViewModel(IUsageQueryService queries, Dispatcher dispatcher)
        : base("数据来源", dispatcher)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None));
    }

    public ObservableCollection<SourceStatusRow> Sources
    {
        get => _sources;
        private set => SetProperty(ref _sources, value);
    }

    public ObservableCollection<SourceStatusPresentation> SourceRows
    {
        get => _sourceRows;
        private set => SetProperty(ref _sourceRows, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    protected override async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool showFeedback)
    {
        RefreshSession session = BeginRefresh(cancellationToken);
        await SetRefreshStartedAsync(session, showFeedback);
        try
        {
            IReadOnlyList<SourceStatusRow> sources =
                await _queries.GetSourcesAsync(session.Token);
            await ApplyIfCurrentAsync(session, () =>
            {
                SetCollectionIfChanged(ref _sources, sources, nameof(Sources));
                SetCollectionIfChanged(
                    ref _sourceRows,
                    sources.Select(static source =>
                        new SourceStatusPresentation(source)),
                    nameof(SourceRows));
            });
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetRefreshFailureAsync(session, exception);
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }
}

public sealed record SourceStatusPresentation(SourceStatusRow Source)
{
    public string CollectionStatusText => !string.IsNullOrWhiteSpace(Source.LastError)
        ? "异常"
        : Source.LastSuccessAtUtc.HasValue
            ? "正常"
            : "等待首次读取";

    public string CompatibilityText => Source.CompatibilityLevel switch
    {
        CompatibilityLevel.FullyCompatible => "完全兼容",
        CompatibilityLevel.PartiallyCompatible => "部分兼容",
        CompatibilityLevel.TemporarilyIncompatible => "暂不兼容",
        CompatibilityLevel.MissingCapability => "能力不可用",
        _ => "状态未知"
    };

    public string CompatibilityDescription
    {
        get
        {
            if (Source.RequiresRescan ||
                string.Equals(
                    Source.CompatibilityCode,
                    "parser_rescan_required",
                    StringComparison.Ordinal))
            {
                return "统计数据需要更新；完成前保留现有数据";
            }

            if (string.Equals(
                Source.CompatibilityCode,
                "session_metadata_partial",
                StringComparison.Ordinal))
            {
                return "部分会话或项目信息不可取得";
            }

            return Source.CompatibilityLevel switch
            {
                CompatibilityLevel.FullyCompatible =>
                    "核心与分类指标可正常统计",
                CompatibilityLevel.PartiallyCompatible =>
                    "部分指标不可取得，可靠指标继续统计",
                CompatibilityLevel.TemporarilyIncompatible =>
                    "核心 Token 语义无法确认，已暂停对应统计",
                CompatibilityLevel.MissingCapability =>
                    "来源未提供所需能力，相关指标不可取得",
                _ => "兼容状态未知，未作推断"
            };
        }
    }
}
