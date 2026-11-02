using System.Data.Common;

namespace ManagerPaperworkSystem.Data.Services;

internal static class ShiftLogStartupNormalization
{
    // SQL Server fires UPDATE triggers even when every assigned value stays the same.
    // Repair only legacy NULLs in editable rows; never touch a closed ledger period.
    public static async Task RunAsync(DbConnection connection, bool sqlServer, CancellationToken ct = default)
    {
        async Task<bool> Exists(string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sqlServer
                ? "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(@table)"
                : "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@table";
            parameter.Value = sqlServer ? "dbo." + table : table; command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
        }
        if (!await Exists("ShiftLogs")) return;
        var tableName = sqlServer ? "dbo.ShiftLogs" : "ShiftLogs";
        var guard = "";
        if (await Exists("LedgerMonths"))
        {
            var months = sqlServer ? "dbo.LedgerMonths" : "LedgerMonths";
            var end = sqlServer ? "DATEADD(month,1,m.Month)" : "date(m.Month,'+1 month')";
            guard = $"""
                AND NOT EXISTS (SELECT 1 FROM {months} m WHERE m.StoreId=s.StoreId AND m.IsClosed=1 AND s.[Date]>=m.Month AND s.[Date]<{end})
                AND NOT EXISTS (SELECT 1 FROM {tableName} original JOIN {months} m ON m.StoreId=original.StoreId AND m.IsClosed=1
                    AND original.[Date]>=m.Month AND original.[Date]<{end} WHERE original.Id=s.CorrectsId AND original.StoreId=s.StoreId)
                """;
        }
        using var normalize = connection.CreateCommand();
        normalize.CommandText = $"""
            {(sqlServer ? "UPDATE s" : "UPDATE ShiftLogs AS s")}
            SET Employee=COALESCE(Employee,''), ShiftNo=COALESCE(ShiftNo,''), PayoutReason=COALESCE(PayoutReason,''),
                PosReportKey=COALESCE(PosReportKey,''), PosReportPath=COALESCE(PosReportPath,''), CreatedByName=COALESCE(CreatedByName,''),
                CreatedByUserId=COALESCE(CreatedByUserId,0), CorrectionReason=COALESCE(CorrectionReason,'')
            {(sqlServer ? "FROM dbo.ShiftLogs s" : "")}
            WHERE (s.Employee IS NULL OR s.ShiftNo IS NULL OR s.PayoutReason IS NULL OR s.PosReportKey IS NULL
                OR s.PosReportPath IS NULL OR s.CreatedByName IS NULL OR s.CreatedByUserId IS NULL OR s.CorrectionReason IS NULL)
            {guard};
            """;
        await normalize.ExecuteNonQueryAsync(ct);
    }
}
