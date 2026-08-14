using System.Diagnostics;

namespace AgenTally.UI.Updates;

internal interface IReleasePageLauncher
{
    bool TryOpen(Uri releasePageUri);
}

internal sealed class ShellReleasePageLauncher : IReleasePageLauncher
{
    public bool TryOpen(Uri releasePageUri)
    {
        ArgumentNullException.ThrowIfNull(releasePageUri);
        if (!releasePageUri.IsAbsoluteUri ||
            !string.Equals(
                releasePageUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(releasePageUri.Host) ||
            !string.IsNullOrEmpty(releasePageUri.UserInfo))
        {
            return false;
        }

        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = releasePageUri.AbsoluteUri,
                UseShellExecute = true
            }) is not null;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                NotSupportedException or
                System.ComponentModel.Win32Exception or
                System.Security.SecurityException)
        {
            return false;
        }
    }
}

internal sealed class UnavailableReleasePageLauncher : IReleasePageLauncher
{
    public bool TryOpen(Uri releasePageUri)
    {
        ArgumentNullException.ThrowIfNull(releasePageUri);
        return false;
    }
}
