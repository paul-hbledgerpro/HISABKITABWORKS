namespace ManagerPaperworkSystem.WinForms;

// Only a conflict with an existing local row is recoverable per batch.
// Portal store/header, authentication, parsing and database errors still stop a run.
internal sealed class PortalZReportConflictException(string batch, DateOnly date, int shiftId)
    : InvalidOperationException(
        $"Batch {batch} on {date:M/d/yyyy} conflicts with an existing unverified report (shift {shiftId}). " +
        "No existing shift was overwritten. Collect store recovery evidence for this row.");
