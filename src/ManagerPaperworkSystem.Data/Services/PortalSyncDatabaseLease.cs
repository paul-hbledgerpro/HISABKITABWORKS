using System.Data;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

/// <summary>Coordinates portal imports into the same SQL store across Windows accounts and processes.</summary>
public sealed class PortalSyncDatabaseLease : IAsyncDisposable
{
    private readonly AppDbContext _db;
    private readonly string _resource;
    private bool _ownsConnection;
    private bool _disposed;
    public bool Acquired { get; private set; }

    private PortalSyncDatabaseLease(AppDbContext db, int storeId)
    {
        _db = db;
        _resource = $"HISABKITAB:PortalSync:Store:{storeId}";
    }

    public static async Task<PortalSyncDatabaseLease> TryAcquireAsync(
        AppDbContext db, int storeId, CancellationToken cancellationToken = default)
    {
        var lease = new PortalSyncDatabaseLease(db, storeId);
        if (!db.Database.IsSqlServer())
        {
            lease.Acquired = true;
            return lease;
        }
        try
        {
            lease._ownsConnection = db.Database.GetDbConnection().State != ConnectionState.Open;
            if (lease._ownsConnection)
                await db.Database.OpenConnectionAsync(cancellationToken);
            var result = await lease.ExecuteAsync(
                "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, " +
                "@LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @result;",
                cancellationToken);
            if (result < -1)
                throw new InvalidOperationException($"The store's portal sync lock could not be acquired (result {result}).");
            lease.Acquired = result >= 0;
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var resource = command.CreateParameter();
        resource.ParameterName = "@resource";
        resource.DbType = DbType.String;
        resource.Value = _resource;
        command.Parameters.Add(resource);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_db.Database.IsSqlServer() &&
                _db.Database.GetDbConnection().State == ConnectionState.Open)
            {
                // A canceled command can acquire the lock before its result is
                // observed. Check the session rather than only the Acquired flag.
                await ExecuteAsync(
                    "DECLARE @result int = 0; IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' " +
                    "EXEC @result = sys.sp_releaseapplock @Resource = @resource, " +
                    "@LockOwner = 'Session'; SELECT @result;", CancellationToken.None);
            }
        }
        finally
        {
            if (_ownsConnection)
                await _db.Database.CloseConnectionAsync();
        }
    }
}
