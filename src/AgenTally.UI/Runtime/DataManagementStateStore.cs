using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Windows;
using AgenTally.Storage.Runtime;
using Microsoft.Win32;

namespace AgenTally.UI.Runtime;

public interface IDataManagementStateStore
{
    DateTimeOffset? ReadLastSuccessfulBackupUtc();

    bool TryWriteLastSuccessfulBackupUtc(DateTimeOffset value);
}

public sealed class UnavailableDataManagementStateStore : IDataManagementStateStore
{
    public DateTimeOffset? ReadLastSuccessfulBackupUtc() => null;

    public bool TryWriteLastSuccessfulBackupUtc(DateTimeOffset value) => false;
}

public sealed class JsonDataManagementStateStore : IDataManagementStateStore
{
    private const int MaximumFileBytes = 4096;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly string _path;

    public JsonDataManagementStateStore(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _path = Path.GetFullPath(profile.DataManagementStatePath);
        if (profile.Channel == AgenTallyChannel.Development &&
            !profile.IsDevelopmentOwnedPath(_path))
        {
            throw new InvalidOperationException(
                "Development data-management state must stay inside artifacts/development.");
        }
    }

    public DateTimeOffset? ReadLastSuccessfulBackupUtc()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            byte[] payload = BoundedFileReader.ReadAllBytes(
                _path,
                MaximumFileBytes);
            try
            {
                DataManagementState? state =
                    JsonSerializer.Deserialize<DataManagementState>(
                        payload,
                        SerializerOptions);
                return state?.SchemaVersion == 1 &&
                    state.LastSuccessfulBackupUtc is { Offset: { } offset } value &&
                    offset == TimeSpan.Zero
                        ? value
                        : null;
            }
            finally
            {
                Array.Clear(payload);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    public bool TryWriteLastSuccessfulBackupUtc(DateTimeOffset value)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new DataManagementState(1, value.ToUniversalTime()),
                    SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record DataManagementState(
        int SchemaVersion,
        DateTimeOffset? LastSuccessfulBackupUtc);
}

public interface IDataBackupInteraction
{
    string? ChooseBackupDestination(string suggestedFileName);

    string? ChooseBackupToRestore();

    bool ConfirmRestore(string backupPath);
}

public sealed class RejectingDataBackupInteraction : IDataBackupInteraction
{
    public string? ChooseBackupDestination(string suggestedFileName) => null;

    public string? ChooseBackupToRestore() => null;

    public bool ConfirmRestore(string backupPath) => false;
}

public sealed class WindowsDataBackupInteraction : IDataBackupInteraction
{
    private readonly string? _initialDirectory;

    public WindowsDataBackupInteraction(AgenTallyRuntimeProfile? profile = null)
    {
        if (profile?.Channel == AgenTallyChannel.Development)
        {
            _initialDirectory = Path.Combine(profile.DataRoot, "backups");
        }
    }

    public string? ChooseBackupDestination(string suggestedFileName)
    {
        if (_initialDirectory is not null)
        {
            Directory.CreateDirectory(_initialDirectory);
        }
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".agentally-backup",
            FileName = suggestedFileName,
            InitialDirectory = _initialDirectory,
            Filter = "AgenTally 备份 (*.agentally-backup)|*.agentally-backup",
            OverwritePrompt = true,
            Title = "保存 AgenTally 备份"
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public string? ChooseBackupToRestore()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            DefaultExt = ".agentally-backup",
            Filter = "AgenTally 备份 (*.agentally-backup)|*.agentally-backup",
            InitialDirectory = _initialDirectory,
            Multiselect = false,
            Title = "选择 AgenTally 备份"
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public bool ConfirmRestore(string backupPath) => MessageBox.Show(
        Application.Current?.MainWindow,
        "当前频道的全部 AgenTally 数据将被备份文件替换。\n\n这是整库恢复，不会合并两份数据。是否继续？",
        "恢复本地备份",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
