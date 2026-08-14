using System.IO;
using System.Security;
using System.Text.Json;
using AgenTally.Storage.Runtime;
using Microsoft.Win32;

namespace AgenTally.UI.Runtime;

internal enum StartupRegistrationState
{
    Disabled,
    Enabled,
    Conflict,
    Unavailable
}

internal sealed record StartupRegistrationStatus(
    StartupRegistrationState State,
    string? Message = null)
{
    public bool IsEnabled => State == StartupRegistrationState.Enabled;
}

internal interface IStartupRegistrationStore
{
    StartupRegistrationStatus Read();

    StartupRegistrationStatus SetEnabled(bool enabled);
}

internal static class StartupRegistrationCommand
{
    public const string BackgroundArgument = "--background";
    public const string RegistryValueName = "AgenTally";

    public static string Create(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(fullPath) ||
            !string.Equals(
                Path.GetExtension(fullPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains('"'))
        {
            throw new ArgumentException(
                "The startup executable path is invalid.",
                nameof(executablePath));
        }

        return $"\"{fullPath}\" {BackgroundArgument}";
    }
}

internal readonly record struct StartupRegistrationEntry(
    bool Exists,
    string? Command);

internal interface IStartupRegistrationBackend
{
    StartupRegistrationEntry Read();

    void Write(string command);

    void Delete();
}

internal sealed class ExactStartupRegistrationStore : IStartupRegistrationStore
{
    private const string ConflictMessage =
        "检测到同名启动项，AgenTally 不会覆盖或删除它。";
    private const string ReadFailureMessage =
        "无法读取开机自启状态，请稍后重试。";
    private const string WriteFailureMessage =
        "无法修改开机自启，请检查当前用户权限后重试。";
    private readonly IStartupRegistrationBackend _backend;
    private readonly string _expectedCommand;
    private readonly object _sync = new();

    public ExactStartupRegistrationStore(
        string expectedCommand,
        IStartupRegistrationBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommand);
        _expectedCommand = expectedCommand;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public StartupRegistrationStatus Read()
    {
        lock (_sync)
        {
            return ReadCore();
        }
    }

    public StartupRegistrationStatus SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            StartupRegistrationStatus current = ReadCore();
            if (current.State is StartupRegistrationState.Conflict or
                StartupRegistrationState.Unavailable)
            {
                return current;
            }

            if (current.IsEnabled == enabled)
            {
                return current;
            }

            try
            {
                if (enabled)
                {
                    _backend.Write(_expectedCommand);
                }
                else
                {
                    _backend.Delete();
                }

                StartupRegistrationStatus updated = ReadCore();
                if (updated.State == (enabled
                        ? StartupRegistrationState.Enabled
                        : StartupRegistrationState.Disabled))
                {
                    return updated;
                }

                return new StartupRegistrationStatus(
                    StartupRegistrationState.Unavailable,
                    WriteFailureMessage);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                return new StartupRegistrationStatus(
                    StartupRegistrationState.Unavailable,
                    WriteFailureMessage);
            }
        }
    }

    private StartupRegistrationStatus ReadCore()
    {
        try
        {
            StartupRegistrationEntry entry = _backend.Read();
            if (!entry.Exists)
            {
                return new StartupRegistrationStatus(
                    StartupRegistrationState.Disabled);
            }

            return string.Equals(
                    entry.Command,
                    _expectedCommand,
                    StringComparison.Ordinal)
                ? new StartupRegistrationStatus(
                    StartupRegistrationState.Enabled)
                : new StartupRegistrationStatus(
                    StartupRegistrationState.Conflict,
                    ConflictMessage);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return new StartupRegistrationStatus(
                StartupRegistrationState.Unavailable,
                ReadFailureMessage);
        }
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is ArgumentException or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            JsonException or
            SecurityException or
            UnauthorizedAccessException;
}

internal sealed class DevelopmentStartupRegistrationBackend :
    IStartupRegistrationBackend
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumFileBytes = 2048;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _path;

    public DevelopmentStartupRegistrationBackend(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public StartupRegistrationEntry Read()
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            return new StartupRegistrationEntry(false, null);
        }

        if (info.Length is <= 0 or > MaximumFileBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Development startup state is invalid.");
        }

        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 2048,
            FileOptions.SequentialScan);
        DevelopmentStartupDocument? document =
            JsonSerializer.Deserialize<DevelopmentStartupDocument>(
                stream,
                SerializerOptions);
        if (document?.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(document.Command))
        {
            throw new InvalidDataException(
                "Development startup state is invalid.");
        }

        return new StartupRegistrationEntry(true, document.Command);
    }

    public void Write(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException(
                "Development startup state has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_path) &&
            (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Development startup state cannot be a reparse point.");
        }

        string temporaryPath = _path + ".tmp";
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new DevelopmentStartupDocument(
                    CurrentSchemaVersion,
                    command),
                SerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 2048,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public void Delete()
    {
        if (File.Exists(_path) &&
            (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Development startup state cannot be a reparse point.");
        }

        File.Delete(_path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
        }
    }

    private sealed record DevelopmentStartupDocument(
        int SchemaVersion,
        string Command);
}

internal sealed class WindowsRunStartupRegistrationBackend :
    IStartupRegistrationBackend
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupRegistrationEntry Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: false);
        if (key is null)
        {
            return new StartupRegistrationEntry(false, null);
        }

        string? actualName = key.GetValueNames().FirstOrDefault(name =>
            string.Equals(
                name,
                StartupRegistrationCommand.RegistryValueName,
                StringComparison.OrdinalIgnoreCase));
        if (actualName is null)
        {
            return new StartupRegistrationEntry(false, null);
        }

        object? value = key.GetValue(
            actualName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new StartupRegistrationEntry(true, value as string);
    }

    public void Write(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            RunKeyPath,
            writable: true) ?? throw new InvalidOperationException(
            "Unable to open the current-user Run key.");
        key.SetValue(
            StartupRegistrationCommand.RegistryValueName,
            command,
            RegistryValueKind.String);
    }

    public void Delete()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RunKeyPath,
            writable: true);
        key?.DeleteValue(
            StartupRegistrationCommand.RegistryValueName,
            throwOnMissingValue: false);
    }
}

internal static class StartupRegistrationProductionComposition
{
    public static IStartupRegistrationStore Create(
        AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string expectedCommand = StartupRegistrationCommand.Create(
            profile.UiExecutablePath);
        return profile.Channel switch
        {
            AgenTallyChannel.Development => CreateDevelopment(
                profile,
                expectedCommand),
            AgenTallyChannel.Stable => new ExactStartupRegistrationStore(
                expectedCommand,
                new WindowsRunStartupRegistrationBackend()),
            _ => throw new InvalidOperationException(
                "Unsupported AgenTally channel.")
        };
    }

    private static IStartupRegistrationStore CreateDevelopment(
        AgenTallyRuntimeProfile profile,
        string expectedCommand)
    {
        if (!profile.IsDevelopmentOwnedPath(
                profile.StartupRegistrationStatePath))
        {
            throw new InvalidOperationException(
                "Development startup state must stay inside artifacts/development.");
        }

        return new ExactStartupRegistrationStore(
            expectedCommand,
            new DevelopmentStartupRegistrationBackend(
                profile.StartupRegistrationStatePath));
    }
}

internal sealed class UnavailableStartupRegistrationStore :
    IStartupRegistrationStore
{
    public StartupRegistrationStatus Read() => new(
        StartupRegistrationState.Unavailable,
        "当前诊断界面不能修改开机自启。");

    public StartupRegistrationStatus SetEnabled(bool enabled) => Read();
}
