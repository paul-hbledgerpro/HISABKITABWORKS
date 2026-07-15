# HISAB KITAB project guidance

## Active scope

- The active desktop application is `src/ManagerPaperworkSystem.WinForms` targeting .NET 8 on Windows.
- `src/ManagerPaperworkSystem.UI` is the older WPF implementation. Do not move new work back to WPF unless the user explicitly requests it.
- Preserve existing business behavior outside the requested feature area. Prefer narrow, checkpointed changes over broad refactors.

## Verification

- Restore/build the WinForms project directly:
  `dotnet build src/ManagerPaperworkSystem.WinForms/ManagerPaperworkSystem.WinForms.csproj -c Release`
- Do not launch the desktop app unless the user explicitly asks for interactive testing.
- Do not commit generated output from `bin`, `obj`, `installer/publish`, or Inno Setup output folders.
- Never commit `%LOCALAPPDATA%/Hisab Kitab` data, databases, licenses, or connection settings.

## Current continuation point

- Continue with Reports PDF output validation and visual confirmation.
- Known follow-ups include bank-statement parsing totals, the `StatementMonth` schema issue if reproducible, Excel exports, and store-selection persistence.
- Report templates: `src/ManagerPaperworkSystem.Reports/Pdf/SelectedOptionReportPdf.cs`
- Report routing/viewer: `src/ManagerPaperworkSystem.WinForms/MainForm.cs` and `src/ManagerPaperworkSystem.WinForms/ReportViewerForm.cs`

