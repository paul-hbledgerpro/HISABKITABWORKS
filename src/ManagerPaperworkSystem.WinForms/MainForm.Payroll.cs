using ManagerPaperworkSystem.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed partial class MainForm
{
    private Control BuildPayroll()
    {
        using var db = CreateDb();
        var employees = db.Employees.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToList();
        var runs = db.PayrollRuns.AsNoTracking().Where(x => x.StoreId == _currentStoreId).OrderByDescending(x => x.PayDate).Take(20).ToList();

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = WinTheme.Bg };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = WinTheme.Bg };
        MetricCard(metrics, 0, 0, "ACTIVE EMPLOYEES", employees.Count(x => x.IsActive).ToString(), WinTheme.Green, "Current store");
        MetricCard(metrics, 1, 0, "DRAFT PAYROLLS", runs.Count(x => x.Status == PayrollRunStatus.Draft).ToString(), WinTheme.Copper, "Awaiting approval");
        MetricCard(metrics, 2, 0, "LAST PAY DATE", runs.FirstOrDefault(x => x.Status == PayrollRunStatus.Finalized)?.PayDate.ToString("MM/dd/yyyy") ?? "—", WinTheme.Blue, "Finalized payroll");
        MetricCard(metrics, 3, 0, "YEAR-TO-DATE GROSS", db.PayrollEntries.AsNoTracking().Where(x => x.PayrollRun!.StoreId == _currentStoreId && x.PayrollRun.TaxYear == DateTime.Today.Year && x.PayrollRun.Status == PayrollRunStatus.Finalized).Sum(x => (decimal?)x.GrossPay).GetValueOrDefault().ToString("C2"), WinTheme.Green, "Finalized entries");
        body.Controls.Add(metrics, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = WinTheme.Panel, Padding = new Padding(16, 16, 8, 8), WrapContents = false };
        AddPayrollAction(actions, "EMPLOYEES", () => { using var form = new EmployeeManagerForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); ShowModule("Payroll"); }, true);
        AddPayrollAction(actions, "ENTER EMPLOYEE HOURS", () => { using var form = new EmployeeHoursForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); }, true);
        AddPayrollAction(actions, "RUN PAYROLL", () => { using var form = new PayrollRunForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); ShowModule("Payroll"); }, true);
        AddPayrollAction(actions, "PAYROLL HISTORY", () => { using var form = new PayrollHistoryForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); }, false);
        if (LicenseRuntime.HasService("Scheduling"))
            AddPayrollAction(actions, "SCHEDULING", () => ShowModule("Scheduling"), false);
        actions.Controls.Add(new Label
        {
            Text = $"PAYROLL STATE: {LicenseRuntime.PayrollState}  •  DEVELOPER ASSIGNED",
            AutoSize = true,
            ForeColor = WinTheme.Blue,
            Padding = new Padding(16, 12, 0, 0),
            Font = WinTheme.BoldFont(9.5f)
        });
        body.Controls.Add(actions, 0, 1);

        // Tax-rule packages remain automatic and read-only on the customer PC.
        // The signed license decides which state can be used.
        _ = Task.Run(async () => await TaxRulePackageService.CheckForUpdatesAsync(false));

        var grid = WinTheme.Grid();
        grid.DataSource = runs.Select(x => new
        {
            x.Id,
            Period = $"{x.PeriodStart:MM/dd/yyyy} - {x.PeriodEnd:MM/dd/yyyy}",
            PayDate = x.PayDate.ToString("MM/dd/yyyy"),
            Frequency = x.PayFrequency.ToString(),
            Status = x.Status.ToString(),
            CreatedBy = x.CreatedByName,
            Created = x.CreatedUtc.ToLocalTime().ToString("MM/dd/yyyy h:mm tt")
        }).ToList();
        body.Controls.Add(grid, 0, 2);
        return ModuleShell("\uE8C7", "Payroll", "Secure employee records, scheduled hours, payroll calculation, checks, and pay stubs.", body);
    }

    private Control BuildScheduling()
    {
        using var db = CreateDb();
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek));
        var to = from.AddDays(13);
        var employees = db.Employees.AsNoTracking().Where(x => x.StoreId == _currentStoreId && x.IsActive).ToDictionary(x => x.Id, x => x.FullName);
        var shifts = db.ScheduleShifts.AsNoTracking().Where(x => x.StoreId == _currentStoreId && x.ShiftDate >= from && x.ShiftDate <= to).OrderBy(x => x.ShiftDate).ThenBy(x => x.StartTime).ToList();

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = WinTheme.Bg };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = WinTheme.Panel, Padding = new Padding(16), WrapContents = false };
        AddPayrollAction(actions, "ADD SCHEDULE", () => { using var form = new ScheduleBuilderForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); ShowModule("Scheduling"); }, true);
        AddPayrollAction(actions, "MANAGE SCHEDULE", () => { using var form = new ScheduleManagerForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); ShowModule("Scheduling"); }, true);
        AddPayrollAction(actions, "SMS SETUP", () => { using var form = new ScheduleSmsSettingsForm(CreateDb); form.ShowDialog(this); }, false);
        AddPayrollAction(actions, "TEXT DELIVERY LOG", () => { using var form = new ScheduleNotificationLogForm(CreateDb, _currentStoreId); form.ShowDialog(this); }, false);
        if (LicenseRuntime.HasService("Payroll"))
            AddPayrollAction(actions, "RUN PAYROLL", () => { using var form = new PayrollRunForm(CreateDb, _currentStoreId, _session.DisplayName); form.ShowDialog(this); }, false);
        actions.Controls.Add(new Label { Text = $"Showing {from:MMM d} - {to:MMM d, yyyy}", AutoSize = true, ForeColor = WinTheme.Muted, Padding = new Padding(24, 11, 0, 0), Font = WinTheme.BodyFont(10) });
        body.Controls.Add(actions, 0, 0);

        var grid = WinTheme.Grid();
        grid.DataSource = shifts.Select(x => new
        {
            x.Id,
            Date = x.ShiftDate.ToString("ddd MM/dd/yyyy"),
            Employee = employees.GetValueOrDefault(x.EmployeeId, $"Employee #{x.EmployeeId}"),
            Start = DateTime.Today.Add(x.StartTime).ToString("h:mm tt"),
            End = DateTime.Today.Add(x.EndTime).ToString("h:mm tt"),
            Hours = x.ScheduledHours,
            BreakMinutes = x.UnpaidBreakMinutes,
            Status = x.Status.ToString(),
            x.Notes
        }).ToList();
        body.Controls.Add(grid, 0, 1);
        return ModuleShell("\uE787", "Scheduling", "Build employee schedules now; approved hours flow into Payroll for final admin review.", body);
    }

    private static void AddPayrollAction(FlowLayoutPanel host, string text, Action action, bool primary)
    {
        var button = WinTheme.Button(text, primary);
        button.Width = 190;
        button.Height = 44;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Click += (_, _) => action();
        host.Controls.Add(button);
    }
}
