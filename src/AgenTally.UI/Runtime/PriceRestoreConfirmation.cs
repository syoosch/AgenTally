using System.Windows;

namespace AgenTally.UI.Runtime;

public interface IPriceRestoreConfirmation
{
    bool ConfirmModelRestore(string normalizedModel, bool hasBuiltInDefault);

    bool ConfirmModelRestore(string normalizedModel, bool hasBuiltInDefault, string effectiveDate) =>
        ConfirmModelRestore(normalizedModel, hasBuiltInDefault);

    bool ConfirmAllRestore(int customPriceCount);
}

public sealed class MessageBoxPriceRestoreConfirmation :
    IPriceRestoreConfirmation
{
    public bool ConfirmModelRestore(
        string normalizedModel,
        bool hasBuiltInDefault) => ConfirmModelRestore(normalizedModel, hasBuiltInDefault, "今天");

    public bool ConfirmModelRestore(string normalizedModel, bool hasBuiltInDefault, string effectiveDate)
    {
        string effect = hasBuiltInDefault
            ? "从该日期起使用默认价格，并取消之后计划的自定义价格。"
            : "该模型没有内置默认价格；从该日期起的记录将保持未计价。";
        return MessageBox.Show(
                   $"确定从 {effectiveDate} 恢复“{normalizedModel}”吗？\n\n{effect}\n此前费率与已计价历史保留。",
                   "恢复模型默认价格",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmAllRestore(int customPriceCount) =>
        MessageBox.Show(
            $"确定恢复全部默认价格吗？\n\n{customPriceCount} 个模型从现在起使用默认价格，" +
            "取消之后计划的自定义价格。未知模型将保持未计价；此前费率与已计价历史保留。",
            "恢复全部默认价格",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed class RejectingPriceRestoreConfirmation :
    IPriceRestoreConfirmation
{
    public bool ConfirmModelRestore(
        string normalizedModel,
        bool hasBuiltInDefault) => false;

    public bool ConfirmAllRestore(int customPriceCount) => false;
}
