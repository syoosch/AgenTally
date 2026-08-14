using System.IO;
using System.Text.Json;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Runtime;

internal interface IUiPreferencesStore
{
    int? ReadRefreshIntervalSeconds();

    bool TryWriteRefreshIntervalSeconds(int value);

    UiWindowSize? ReadWindowSize();

    bool TryWriteWindowSize(UiWindowSize value);
}

internal sealed record UiWindowSize(double Width, double Height)
{
    private const double MinimumStoredDimension = 100d;
    private const double MaximumStoredDimension = 32768d;

    public bool IsValid =>
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width is >= MinimumStoredDimension and <= MaximumStoredDimension &&
        Height is >= MinimumStoredDimension and <= MaximumStoredDimension;
}

internal sealed class UnavailableUiPreferencesStore : IUiPreferencesStore
{
    public int? ReadRefreshIntervalSeconds() => null;

    public bool TryWriteRefreshIntervalSeconds(int value) => false;

    public UiWindowSize? ReadWindowSize() => null;

    public bool TryWriteWindowSize(UiWindowSize value) => false;
}

internal sealed class JsonUiPreferencesStore : IUiPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumFileBytes = 4096;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _sync = new();

    public JsonUiPreferencesStore(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _path = Path.GetFullPath(profile.UiPreferencesPath);
        if (profile.Channel == AgenTallyChannel.Development &&
            !profile.IsDevelopmentOwnedPath(_path))
        {
            throw new InvalidOperationException(
                "Development UI preferences must stay inside artifacts/development.");
        }
    }

    public int? ReadRefreshIntervalSeconds()
    {
        lock (_sync)
        {
            return ReadDocument()?.RefreshIntervalSeconds;
        }
    }

    public UiWindowSize? ReadWindowSize()
    {
        lock (_sync)
        {
            UiPreferencesDocument? document = ReadDocument();
            if (document?.WindowWidth is not double width ||
                document.WindowHeight is not double height)
            {
                return null;
            }

            var result = new UiWindowSize(width, height);
            return result.IsValid ? result : null;
        }
    }

    public bool TryWriteRefreshIntervalSeconds(int value) =>
        TryUpdate(document => document with
        {
            RefreshIntervalSeconds = value
        });

    public bool TryWriteWindowSize(UiWindowSize value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IsValid && TryUpdate(document => document with
        {
            WindowWidth = value.Width,
            WindowHeight = value.Height
        });
    }

    private UiPreferencesDocument? ReadDocument()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists ||
                info.Length is <= 0 or > MaximumFileBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            UiPreferencesDocument? document =
                JsonSerializer.Deserialize<UiPreferencesDocument>(
                    stream,
                    SerializerOptions);
            return document?.SchemaVersion == CurrentSchemaVersion
                ? document
                : null;
        }
        catch (Exception exception)
            when (IsExpectedFileFailure(exception) || exception is JsonException)
        {
            return null;
        }
    }

    private bool TryUpdate(
        Func<UiPreferencesDocument, UiPreferencesDocument> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            UiPreferencesDocument current = ReadDocument() ??
                new UiPreferencesDocument(
                    CurrentSchemaVersion,
                    null,
                    null,
                    null);
            return TryWriteDocument(update(current));
        }
    }

    private bool TryWriteDocument(UiPreferencesDocument document)
    {
        string temporaryPath = _path + ".tmp";
        try
        {
            if (File.Exists(_path) &&
                (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            string directory = Path.GetDirectoryName(_path) ??
                throw new InvalidOperationException(
                    "UI preference path has no parent directory.");
            Directory.CreateDirectory(directory);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                document,
                SerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (IsExpectedFileFailure(exception))
            {
                // A failed best-effort cleanup cannot affect the active UI value.
            }
        }
    }

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    private sealed record UiPreferencesDocument(
        int SchemaVersion,
        int? RefreshIntervalSeconds,
        double? WindowWidth,
        double? WindowHeight);
}
