using System.Windows;

namespace AgenTally.UI.Runtime;

public interface IPriceRestoreConfirmation
{
    bool ConfirmModelRestore(string normalizedModel, bool hasBuiltInDefault);

    bool ConfirmAllRestore(int customPriceCount);
}

public sealed class MessageBoxPriceRestoreConfirmation :
    IPriceRestoreConfirmation
{
    public bool ConfirmModelRestore(
        string normalizedModel,
        bool hasBuiltInDefault)
    {
        string effect = hasBuiltInDefault
            ? "将删除自定义价格，后续未计价记录改用当前版本默认价格。"
            : "该模型没有内置默认价格；删除后，后续记录将保持未计价。";
        return MessageBox.Show(
                   $"确定恢复“{normalizedModel}”吗？\n\n{effect}\n已计价历史不会改变。",
                   "恢复模型默认价格",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmAllRestore(int customPriceCount) =>
        MessageBox.Show(
            $"确定恢复全部默认价格吗？\n\n将删除 {customPriceCount} 个自定义价格。" +
            "未知模型会重新变为未计价；已计价历史不会改变。",
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
