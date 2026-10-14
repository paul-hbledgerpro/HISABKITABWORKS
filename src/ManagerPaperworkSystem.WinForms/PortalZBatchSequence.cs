namespace ManagerPaperworkSystem.WinForms;

internal static class PortalZBatchSequence
{
    public static async Task RunAsync<T>(IEnumerable<T> batches, Func<T, Task> import,
        Action<T, PortalZReportConflictException> leaveConflictUnchanged)
    {
        foreach (var batch in batches)
        {
            try { await import(batch); }
            catch (PortalZReportConflictException exception)
            {
                leaveConflictUnchanged(batch, exception);
            }
        }
    }
}
