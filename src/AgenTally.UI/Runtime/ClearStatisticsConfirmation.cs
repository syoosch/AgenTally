using System.Windows;

namespace AgenTally.UI.Runtime;

public interface IClearStatisticsConfirmation
{
    bool ConfirmClearStatistics();
}

public sealed class MessageBoxClearStatisticsConfirmation :
    IClearStatisticsConfirmation
{
    public bool ConfirmClearStatistics() =>
        MessageBox.Show(
            """
            确定清除全部本地统计吗？

            AgenTally 会先只读扫描全部已支持 Agent 的当前日志到末尾，再清除当前频道保存的 Token、会话、Prompt 摘要和已有事件的计价快照。自定义模型价格会保留，任何 Agent 原始日志都不会被修改。

            清除后只累计新记录。以后手动重新扫描可以恢复原始日志中仍然存在的历史，但无法恢复已从原始日志删除、只保存在 AgenTally 数据库中的历史。
            """,
            "清除全部本地统计",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed class RejectingClearStatisticsConfirmation :
    IClearStatisticsConfirmation
{
    public bool ConfirmClearStatistics() => false;
}
