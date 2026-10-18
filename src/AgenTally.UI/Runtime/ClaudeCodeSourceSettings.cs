using AgenTally.Storage.Runtime;
using Microsoft.Win32;
using System.IO;

namespace AgenTally.UI.Runtime;

public interface IClaudeCodeSourceSettings
{
    bool IsAvailable { get; }

    IReadOnlyList<string> ReadAdditionalRoots();

    void WriteAdditionalRoots(IEnumerable<string> roots);
}

public interface IClaudeCodeDirectoryPicker
{
    string? PickDirectory();
}

internal sealed class UnavailableClaudeCodeSourceSettings :
    IClaudeCodeSourceSettings
{
    public bool IsAvailable => false;

    public IReadOnlyList<string> ReadAdditionalRoots() => [];

    public void WriteAdditionalRoots(IEnumerable<string> roots) =>
        throw new InvalidOperationException(
            "Claude Code source settings are unavailable.");
}

internal sealed class ProfileClaudeCodeSourceSettings :
    IClaudeCodeSourceSettings
{
    private readonly ClaudeCodeSourceConfigurationStore _store;

    public ProfileClaudeCodeSourceSettings(AgenTallyRuntimeProfile profile)
    {
        _store = new ClaudeCodeSourceConfigurationStore(profile);
    }

    public bool IsAvailable => true;

    public IReadOnlyList<string> ReadAdditionalRoots() =>
        _store.ReadAdditionalRoots();

    public void WriteAdditionalRoots(IEnumerable<string> roots) =>
        _store.WriteAdditionalRoots(roots);
}

internal sealed class WindowsClaudeCodeDirectoryPicker :
    IClaudeCodeDirectoryPicker
{
    public string? PickDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Claude Code 数据目录",
            Multiselect = false
        };
        return dialog.ShowDialog() == true
            ? Path.GetFullPath(dialog.FolderName)
            : null;
    }
}

internal sealed class UnavailableClaudeCodeDirectoryPicker :
    IClaudeCodeDirectoryPicker
{
    public string? PickDirectory() => null;
}
