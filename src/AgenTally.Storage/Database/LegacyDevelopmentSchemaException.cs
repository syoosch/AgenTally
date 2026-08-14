namespace AgenTally.Storage.Database;

public sealed class LegacyDevelopmentSchemaException : InvalidOperationException
{
    public LegacyDevelopmentSchemaException()
        : base(
            "检测到未发布的 AgenTally 开发期 SQLite Schema v1。" +
            "为避免误删数据，程序不会自动删除或重建该数据库。")
    {
    }
}
