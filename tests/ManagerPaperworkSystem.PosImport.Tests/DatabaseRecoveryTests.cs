using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Data.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ManagerPaperworkSystem.PosImport.Tests;

public sealed class DatabaseRecoveryTests
{
    [SqlServerFact] public async Task IndependentSessionsCoordinatePerStoreAndReleaseAfterInterruption()
    {
        var name = "HK_Lock_Test_" + Guid.NewGuid().ToString("N");
        var cs = $"Server={Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")};Database={name};Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        await using var db = Db();
        try
        {
            await db.Database.EnsureCreatedAsync(); await using var other = Db();
            await using (var first = await PortalSyncDatabaseLease.TryAcquireAsync(db, 1))
            {
                Assert.True(first.Acquired);
                await using (var blocked = await PortalSyncDatabaseLease.TryAcquireAsync(other, 1)) Assert.False(blocked.Acquired);
                await using (var separateStore = await PortalSyncDatabaseLease.TryAcquireAsync(other, 2)) Assert.True(separateStore.Acquired);
            }
            await using var retry = await PortalSyncDatabaseLease.TryAcquireAsync(other, 1); Assert.True(retry.Acquired);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    [SqlServerFact] public async Task NativeChecksumBackupRestoresIntoSeparateDatabaseWithRecordsIntact()
    {
        var name = "HK_Restore_Test_" + Guid.NewGuid().ToString("N"); var restored = name + "_copy";
        var masterCs = $"Server={Environment.GetEnvironmentVariable("HK_TEST_SQL_SERVER")};Database=master;Integrated Security=True;TrustServerCertificate=True;Pooling=False";
        using var master = new SqlConnection(masterCs); await master.OpenAsync(); string? backupPath = null;
        async Task Execute(string sql, params (string, object)[] parameters)
        { using var command = new SqlCommand(sql, master) { CommandTimeout = 120 }; foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value); await command.ExecuteNonQueryAsync(); }
        try
        {
            await Execute($"CREATE DATABASE [{name}];");
            await Execute($"CREATE TABLE [{name}].dbo.RecoveryProbe (StoreId int, Batch int, DropAmount decimal(18,2)); INSERT [{name}].dbo.RecoveryProbe VALUES(1,3004,123.45),(2,3004,67.89);");
            using var pathCommand = new SqlCommand("SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath'));", master);
            var folder = (string)(await pathCommand.ExecuteScalarAsync())!;
            backupPath = Path.Combine(folder, name + ".bak");
            await Execute($"BACKUP DATABASE [{name}] TO DISK=@path WITH COPY_ONLY,CHECKSUM,INIT; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;", ("@path", backupPath));
            var files = new List<(string Logical, string Physical)>();
            using (var fileCommand = new SqlCommand($"SELECT name,physical_name FROM [{name}].sys.database_files", master))
            using (var reader = await fileCommand.ExecuteReaderAsync()) while (await reader.ReadAsync()) files.Add((reader.GetString(0), reader.GetString(1)));
            var moves = string.Join(",", files.Select((x, i) => $"MOVE N'{x.Logical.Replace("'", "''")}' TO N'{Path.Combine(Path.GetDirectoryName(x.Physical)!, restored + "_" + i + Path.GetExtension(x.Physical)).Replace("'", "''")}'"));
            await Execute($"RESTORE DATABASE [{restored}] FROM DISK=@path WITH CHECKSUM,{moves};", ("@path", backupPath));
            using var check = new SqlCommand($"SELECT COUNT(*) FROM [{restored}].dbo.RecoveryProbe WHERE (StoreId=1 AND Batch=3004 AND DropAmount=123.45) OR (StoreId=2 AND Batch=3004 AND DropAmount=67.89);", master);
            Assert.Equal(2, (int)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var db in new[] { restored, name }) await Execute($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END;");
            if (backupPath is not null && File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
