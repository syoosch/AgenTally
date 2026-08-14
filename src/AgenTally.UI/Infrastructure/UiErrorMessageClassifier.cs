using System.IO;
using Microsoft.Data.Sqlite;

namespace AgenTally.UI.Infrastructure;

public static class UiErrorMessageClassifier
{
    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            UnauthorizedAccessException =>
                "无法读取 AgenTally 派生数据库；请检查文件权限后重试。",
            SqliteException =>
                "AgenTally 派生数据库暂时不可用；请检查磁盘空间和文件权限后重试。数据库不会被自动删除。",
            IOException =>
                "暂时无法读取 AgenTally 派生数据库；请检查磁盘和文件占用后重试。",
            _ => "暂时无法读取本地统计，请重试。"
        };
    }
}
