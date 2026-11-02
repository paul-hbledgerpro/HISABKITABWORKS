using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.Data.Services;

public static class ShiftShortageSchema
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ShiftShortageSettings (
                  StoreId INTEGER NOT NULL PRIMARY KEY, StoreGuid TEXT NOT NULL, StoreName TEXT NOT NULL,
                  Enabled INTEGER NOT NULL, Threshold TEXT NOT NULL, RecipientsJson TEXT NOT NULL,
                  UpdatedUtc TEXT NOT NULL, UpdatedBy TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ShiftShortageEmails (
                  Id TEXT NOT NULL PRIMARY KEY, StoreId INTEGER NOT NULL, StoreGuid TEXT NOT NULL,
                  RecipientsJson TEXT NOT NULL, ReportJson TEXT NOT NULL, Status TEXT NOT NULL,
                  CreatedUtc TEXT NOT NULL, NextAttemptUtc TEXT NOT NULL, SentUtc TEXT NULL,
                  LeaseToken TEXT NOT NULL, Message TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_ShiftShortageEmails_StoreId_Status_NextAttemptUtc
                  ON ShiftShortageEmails(StoreId,Status,NextAttemptUtc);
                """, ct);
            return;
        }
        if (!db.Database.IsSqlServer()) return;
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @lock int;
            EXEC @lock=sys.sp_getapplock @Resource='HK_ShiftShortageSchema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
            IF @lock<0 THROW 51121,'Alert upgrade is busy. Please retry.',1;
            IF OBJECT_ID('dbo.ShiftShortageSettings') IS NULL
             CREATE TABLE dbo.ShiftShortageSettings (
              StoreId int NOT NULL PRIMARY KEY, StoreGuid nvarchar(100) NOT NULL, StoreName nvarchar(200) NOT NULL,
              Enabled bit NOT NULL, Threshold decimal(18,2) NOT NULL, RecipientsJson nvarchar(max) NOT NULL,
              UpdatedUtc datetime2 NOT NULL, UpdatedBy nvarchar(120) NOT NULL);
            IF OBJECT_ID('dbo.ShiftShortageEmails') IS NULL
            BEGIN
             CREATE TABLE dbo.ShiftShortageEmails (
              Id nvarchar(64) NOT NULL PRIMARY KEY, StoreId int NOT NULL, StoreGuid nvarchar(100) NOT NULL,
              RecipientsJson nvarchar(max) NOT NULL, ReportJson nvarchar(max) NOT NULL, Status nvarchar(30) NOT NULL,
              CreatedUtc datetime2 NOT NULL, NextAttemptUtc datetime2 NOT NULL, SentUtc datetime2 NULL,
              LeaseToken nvarchar(40) NOT NULL, Message nvarchar(500) NOT NULL);
             CREATE INDEX IX_ShiftShortageEmails_StoreId_Status_NextAttemptUtc
              ON dbo.ShiftShortageEmails(StoreId,Status,NextAttemptUtc);
            END;
            """, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }
}
