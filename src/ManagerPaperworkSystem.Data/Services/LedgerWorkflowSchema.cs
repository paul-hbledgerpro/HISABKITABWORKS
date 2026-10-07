using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace ManagerPaperworkSystem.Data.Services;

public static class LedgerWorkflowSchema
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlServer()) return;
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("DECLARE @lock int; EXEC @lock=sys.sp_getapplock @Resource='HK_LedgerWorkflowSchema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000; IF @lock<0 THROW 51121,'Ledger upgrade is busy. Please retry.',1;",ct);
        await db.Database.ExecuteSqlRawAsync("""
IF COL_LENGTH('dbo.PosSalesSummaries','PayoutReason') < 8000
 ALTER TABLE dbo.PosSalesSummaries ALTER COLUMN PayoutReason nvarchar(4000) NOT NULL;
IF OBJECT_ID('dbo.PendingShiftDrops') IS NULL
BEGIN
 CREATE TABLE dbo.PendingShiftDrops(Id int IDENTITY PRIMARY KEY,RequestId uniqueidentifier NOT NULL,StoreId int NOT NULL,ConfigurationId uniqueidentifier NOT NULL,PortalStoreName nvarchar(300) NOT NULL,Batch nvarchar(20) NOT NULL,[Drop] decimal(18,2) NOT NULL,Payout decimal(18,2) NOT NULL,Reason nvarchar(300) NOT NULL,UserId int NOT NULL,UserName nvarchar(120) NOT NULL,Status nvarchar(30) NOT NULL,Message nvarchar(1000) NOT NULL,CreatedUtc datetime2 NOT NULL,LastAttemptUtc datetime2 NULL,NotifiedUtc datetime2 NULL,ShiftId int NULL,Variance decimal(18,2) NULL);
 CREATE UNIQUE INDEX IX_PendingShiftDrops_RequestId ON dbo.PendingShiftDrops(RequestId);
 CREATE INDEX IX_PendingShiftDrops_StoreId_Batch ON dbo.PendingShiftDrops(StoreId,Batch);
END;
IF OBJECT_ID('dbo.LedgerMonths') IS NULL
BEGIN
 CREATE TABLE dbo.LedgerMonths(Id int IDENTITY PRIMARY KEY,StoreId int NOT NULL,Month date NOT NULL,IsClosed bit NOT NULL,ClosedUtc datetime2 NULL,ClosedBy nvarchar(120) NOT NULL,OpeningCash decimal(18,2) NULL,OpeningBy nvarchar(120) NOT NULL);
 CREATE UNIQUE INDEX IX_LedgerMonths_StoreId_Month ON dbo.LedgerMonths(StoreId,Month);
END;
IF OBJECT_ID('dbo.ShiftPayoutRollups') IS NULL
BEGIN
 CREATE TABLE dbo.ShiftPayoutRollups(Id int IDENTITY PRIMARY KEY,SummaryId int NOT NULL,AppliedAmount decimal(18,2) NOT NULL,AppliedReason nvarchar(max) NOT NULL);
 CREATE UNIQUE INDEX IX_ShiftPayoutRollups_SummaryId ON dbo.ShiftPayoutRollups(SummaryId);
END;
""", ct);
        // Database enforcement also protects against late sync and older clients.
        foreach (var (table, date, end) in new[] {("ShiftLogs","Date","Date"),("CashOnHand","Date","Date"),("CheckPayouts","Date","Date"),("PosSalesSummaries","ReportFrom","ReportTo")})
        {
            var name = "HK_ClosedMonth_" + table;
            var correctionGuard = table is "ShiftLogs" or "CashOnHand" ? $"""
 IF EXISTS(SELECT 1 FROM (SELECT StoreId,CorrectsId FROM inserted UNION ALL SELECT StoreId,CorrectsId FROM deleted) c
 JOIN dbo.[{table}] original ON original.Id=c.CorrectsId AND original.StoreId=c.StoreId
 JOIN dbo.LedgerMonths m WITH(HOLDLOCK) ON m.StoreId=original.StoreId AND m.IsClosed=1 AND original.[Date]>=m.Month AND original.[Date]<DATEADD(month,1,m.Month))
 THROW 51120,'The original entry belongs to a closed month. An owner must reopen it before correcting it.',1;
""" : "";
            var sql = $"""
CREATE OR ALTER TRIGGER dbo.[{name}] ON dbo.[{table}] AFTER INSERT,UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM (SELECT StoreId,[{date}] AS StartDate,[{end}] AS EndDate FROM inserted UNION ALL SELECT StoreId,[{date}],[{end}] FROM deleted) r
 JOIN dbo.LedgerMonths m WITH(HOLDLOCK) ON m.StoreId=r.StoreId AND m.IsClosed=1 AND r.StartDate<DATEADD(month,1,m.Month) AND r.EndDate>=m.Month)
 THROW 51120,'This month is closed. An owner must reopen it before changing or importing records.',1;
{correctionGuard}
END
""";
            // Avoid taking a schema lock on every background run.
            var connection=db.Database.GetDbConnection();
            var opened=connection.State!=System.Data.ConnectionState.Open;
            if(opened) await connection.OpenAsync(ct);
            try {using var command=connection.CreateCommand();command.Transaction=db.Database.CurrentTransaction!.GetDbTransaction();command.CommandText=$"SELECT OBJECT_ID('dbo.{name}')";var id=await command.ExecuteScalarAsync(ct);if(id is null or DBNull) await db.Database.ExecuteSqlRawAsync(sql,ct);}
            finally {if(opened) await connection.CloseAsync();}
        }
        if(transaction is not null) await transaction.CommitAsync(ct);
    }
}
