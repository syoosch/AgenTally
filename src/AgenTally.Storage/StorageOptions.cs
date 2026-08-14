namespace AgenTally.Storage;

public sealed record StorageOptions(string DatabasePath)
{
    public static StorageOptions CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string databasePath = Path.Combine(localAppData, "AgenTally", "agentally.db");

        return new StorageOptions(databasePath);
    }
}
