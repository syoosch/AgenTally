using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Runtime;

internal sealed class UiLifecycleLog
{
    private readonly string _path;

    public UiLifecycleLog(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using Process process = Process.GetCurrentProcess();
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"ui-lifecycle-{process.Id}-{process.StartTime.ToUniversalTime().Ticks}.log");
        _path = Path.Combine(profile.LogRoot, name);
    }

    public void Write(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException(
                "UI lifecycle codes must be controlled identifiers.",
                nameof(code));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(
                _path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O} {code}{Environment.NewLine}"));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // Lifecycle diagnostics are best effort and never gate UI shutdown.
        }
    }

    public void WriteHashedIdentity(string prefix, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24]
            .ToLowerInvariant();
        Write($"{prefix}_{hash}");
    }
}
