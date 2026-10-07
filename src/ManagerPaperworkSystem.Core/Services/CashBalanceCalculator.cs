using ManagerPaperworkSystem.Core.Models;
namespace ManagerPaperworkSystem.Core.Services;

public static class CashBalanceCalculator
{
    public static decimal BalanceThrough(IEnumerable<CashOnHandEntry> effectiveRows,DateOnly through)
    {
        var rows=effectiveRows.Where(x=>x.Date<=through).ToList();
        var opening=rows.Where(x=>x.Reference=="CARRY_FORWARD").OrderByDescending(x=>x.Date).ThenByDescending(x=>x.Id).FirstOrDefault();
        return (opening?.CashAdded ?? 0)+rows.Where(x=>x.Reference!="CARRY_FORWARD" && (opening is null || x.Date>=opening.Date)).Sum(x=>x.CashAdded-x.PayoutAmount);
    }
    public static decimal OpeningForRange(IEnumerable<CashOnHandEntry> effectiveRows, DateOnly from)
    {
        var rows = effectiveRows.ToList();
        return rows.Where(x => x.Date == from && x.Reference == "CARRY_FORWARD").OrderByDescending(x => x.Id).FirstOrDefault()?.CashAdded
            ?? BalanceThrough(rows, from.AddDays(-1));
    }
}
