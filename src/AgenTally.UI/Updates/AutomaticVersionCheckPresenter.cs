using System.Windows;

namespace AgenTally.UI.Updates;

internal sealed class MessageBoxAutomaticVersionCheckPresenter(
    IReleasePageLauncher releasePageLauncher) :
    IAutomaticVersionCheckPresenter
{
    private readonly IReleasePageLauncher _releasePageLauncher =
        releasePageLauncher ??
        throw new ArgumentNullException(nameof(releasePageLauncher));

    public void ShowUpdateAvailable(
        ReleaseVersion currentVersion,
        ReleaseVersion latestVersion,
        Uri releasePageUri)
    {
        ArgumentNullException.ThrowIfNull(releasePageUri);
        string message =
            $"AgenTally {latestVersion} 已可用，当前版本为 {currentVersion}。\n\n" +
            "是否打开发布页面？";
        MessageBoxResult result = ShowQuestion(message);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_releasePageLauncher.TryOpen(releasePageUri))
        {
            ShowOpenFailure();
        }
    }

    private static MessageBoxResult ShowQuestion(string message)
    {
        Window? owner = Application.Current?.MainWindow;
        return owner is null
            ? MessageBox.Show(
                message,
                "AgenTally 有新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information)
            : MessageBox.Show(
                owner,
                message,
                "AgenTally 有新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
    }

    private static void ShowOpenFailure()
    {
        const string message = "无法打开发布页面，请稍后重试。";
        Window? owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            _ = MessageBox.Show(
                message,
                "AgenTally",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _ = MessageBox.Show(
            owner,
            message,
            "AgenTally",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
