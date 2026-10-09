// 验证同步错误报告保留原始原因码并匹配客户端语义。
using Microsoft.Data.Sqlite;

namespace NutstoreMcp.Tests;

public sealed class SyncErrorMappingTests
{
    [Fact]
    public void PathErrorsKeepCodesAndDoNotSwapReservedNamesWithIllegalCharacters()
    {
        var dataDir = TestScratch.NewDirectory("sync-errors");
        var dbDir = Path.Combine(dataDir, "db1");
        Directory.CreateDirectory(dbDir);
        try
        {
            using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dbDir, "nutstore.db"), Pooling = false,
            }.ToString()))
            {
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = "CREATE TABLE sync_errors(file_path TEXT, reason INTEGER, type INTEGER);"
                    + "INSERT INTO sync_errors VALUES('/illegal?.txt',3,0),('/CON.txt',4,0),('/unknown.txt',99,0);";
                command.ExecuteNonQuery();
            }
            using var local = new LocalNutstoreData(dataDir);
            var errors = local.GetSyncErrors().Errors.ToDictionary(item => item.ReasonCode!.Value);
            Assert.Equal("SPECIAL_CHARACTER", errors[3].ReasonName);
            Assert.Equal("RESERVED_NAME", errors[4].ReasonName);
            Assert.Null(errors[99].ReasonName);
            Assert.Equal("/unknown.txt", errors[99].Path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
