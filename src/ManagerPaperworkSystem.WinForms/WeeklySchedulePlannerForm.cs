using System.Globalization;
using System.Text.RegularExpressions;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.Reports.Pdf;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class WeeklySchedulePlannerForm : Form
{
    private static readonly Regex ShiftRangePattern = new(
        @"^\s*(?<start>open|\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:-|to)\s*(?<end>close|\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly string _user;
    private readonly DateTimePicker _weekStart = new()
    {
        Format = DateTimePickerFormat.Short
    };
    private readonly DateTimePicker _openingTime = TimePicker(9);
    private readonly DateTimePicker _closingTime = TimePicker(22);
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly TextBox _quickShift = new()
    {
        Text = "9-5",
        Width = 115,
        TextAlign = HorizontalAlignment.Center,
        Font = WinTheme.BoldFont(10)
    };
    private readonly Label _selectionStatus = new()
    {
        AutoSize = true,
        ForeColor = WinTheme.Muted,
        Padding = new Padding(8, 11, 4, 0),
        Text = "Select one or more schedule cells"
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Muted,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };
    private bool _loadingGrid;
    private bool _hasUnsavedChanges;

    public WeeklySchedulePlannerForm(
        Func<AppDbContext> createDb,
        int storeId,
        string user)
    {
        _createDb = createDb;
        _storeId = storeId;
        _user = user;

        PayrollUi.Prepare(this, "Weekly Employee Schedule Planner - HISAB KITAB", new Size(1500, 860));
        MinimumSize = new Size(1050, 700);

        var today = DateOnly.FromDateTime(DateTime.Today);
        _weekStart.Value = StartOfWeek(today).ToDateTime(TimeOnly.MinValue);

        ConfigureGrid();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 1,
            BackColor = WinTheme.Bg,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.Controls.Add(PayrollUi.Heading("WEEKLY EMPLOYEE SCHEDULE  •  ENTER EVERY EMPLOYEE ON ONE BOARD"), 0, 0);

        var selectors = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            BackColor = WinTheme.Panel,
            Padding = new Padding(8, 4, 8, 4)
        };
        var previous = PayrollUi.Button("PREVIOUS WEEK", false, 160);
        previous.Click += async (_, _) =>
        {
            if (!ConfirmDiscardUnsavedChanges())
                return;
            _weekStart.Value = _weekStart.Value.AddDays(-7);
            await LoadWeekAsync();
        };
        var next = PayrollUi.Button("NEXT WEEK", false, 150);
        next.Click += async (_, _) =>
        {
            if (!ConfirmDiscardUnsavedChanges())
                return;
            _weekStart.Value = _weekStart.Value.AddDays(7);
            await LoadWeekAsync();
        };
        var load = PayrollUi.Button("LOAD WEEK", true, 145);
        load.Click += async (_, _) =>
        {
            if (ConfirmDiscardUnsavedChanges())
                await LoadWeekAsync();
        };
        var copyPrevious = PayrollUi.Button("COPY PREVIOUS WEEK", false, 190);
        copyPrevious.Click += async (_, _) => await CopyPreviousWeekAsync();
        selectors.Controls.Add(previous);
        selectors.Controls.Add(PayrollUi.Field("WEEK STARTING MONDAY", _weekStart, 215));
        selectors.Controls.Add(next);
        selectors.Controls.Add(PayrollUi.Field("STORE OPEN TIME", _openingTime, 190));
        selectors.Controls.Add(PayrollUi.Field("STORE CLOSE TIME", _closingTime, 190));
        selectors.Controls.Add(load);
        selectors.Controls.Add(copyPrevious);
        root.Controls.Add(selectors, 0, 1);

        var instructions = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(232, 241, 252),
            ForeColor = WinTheme.BlueDark,
            Font = WinTheme.BoldFont(9),
            Padding = new Padding(12, 4, 12, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Each employee is one row. Select cells across one or many employees with Ctrl or by dragging, then apply a quick shift below. You can also type directly: 9-5, 2-8 PM, OPEN-5, 5-CLOSE, or OFF. Use semicolons for split shifts."
        };
        root.Controls.Add(instructions, 0, 2);

        var quickActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(8, 6, 8, 4)
        };
        var applyShift = PayrollUi.Button("APPLY TO SELECTED", true, 185);
        applyShift.Click += (_, _) => ApplyValueToSelectedCells(_quickShift.Text);
        var opening = PayrollUi.Button("OPEN-5", false, 110);
        opening.Click += (_, _) => ApplyValueToSelectedCells("OPEN-5");
        var closing = PayrollUi.Button("5-CLOSE", false, 110);
        closing.Click += (_, _) => ApplyValueToSelectedCells("5-CLOSE");
        var fullDay = PayrollUi.Button("OPEN-CLOSE", false, 135);
        fullDay.Click += (_, _) => ApplyValueToSelectedCells("OPEN-CLOSE");
        var off = PayrollUi.Button("SET OFF", false, 105);
        off.Click += (_, _) => ApplyValueToSelectedCells("OFF");
        var clearWeek = PayrollUi.Button("CLEAR DRAFT WEEK", false, 165);
        clearWeek.Click += (_, _) => ClearEditableWeek();
        quickActions.Controls.Add(PayrollUi.Field("QUICK SHIFT", _quickShift, 145));
        quickActions.Controls.Add(applyShift);
        quickActions.Controls.Add(opening);
        quickActions.Controls.Add(closing);
        quickActions.Controls.Add(fullDay);
        quickActions.Controls.Add(off);
        quickActions.Controls.Add(clearWeek);
        quickActions.Controls.Add(_selectionStatus);
        root.Controls.Add(quickActions, 0, 3);
        root.Controls.Add(_grid, 0, 4);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            BackColor = WinTheme.Panel
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        footer.Controls.Add(_status, 0, 0);
        var close = PayrollUi.Button("CLOSE", false, 155);
        close.Click += (_, _) => Close();
        var export = PayrollUi.Button("SAVE & EXPORT PDF", true, 230);
        export.Click += async (_, _) => await ExportPdfAsync();
        var save = PayrollUi.Button("SAVE WEEKLY SCHEDULE", false, 220);
        save.Click += async (_, _) => await SaveWeekAsync(showConfirmation: true);
        footer.Controls.Add(close, 1, 0);
        footer.Controls.Add(export, 2, 0);
        footer.Controls.Add(save, 3, 0);
        root.Controls.Add(footer, 0, 5);

        Controls.Add(root);
        Shown += async (_, _) => await LoadWeekAsync();
        FormClosing += (_, eventArgs) =>
        {
            if (!ConfirmDiscardUnsavedChanges())
                eventArgs.Cancel = true;
        };
    }

    private void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeight = 52;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.RowTemplate.Height = 52;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Employee",
            HeaderText = "EMPLOYEE",
            ReadOnly = true,
            Frozen = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 185,
            MinimumWidth = 165,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        for (var day = 0; day < 7; day++)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"Day{day}",
                HeaderText = day.ToString(CultureInfo.InvariantCulture),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 125,
                FillWeight = 100,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        _grid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex <= 0)
                return;
            ApplyCellStyle(_grid.Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex]);
            if (!_loadingGrid)
            {
                _hasUnsavedChanges = true;
                UpdateSelectionStatus();
            }
        };
        _grid.SelectionChanged += (_, _) => UpdateSelectionStatus();
        _grid.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode is Keys.Delete or Keys.Back)
            {
                ApplyValueToSelectedCells("OFF");
                eventArgs.SuppressKeyPress = true;
            }
        };
        _grid.DataError += (_, eventArgs) => eventArgs.ThrowException = false;
    }

    private async Task LoadWeekAsync()
    {
        var from = StartOfWeek(DateOnly.FromDateTime(_weekStart.Value));
        var to = from.AddDays(6);
        _weekStart.Value = from.ToDateTime(TimeOnly.MinValue);

        await using var db = _createDb();
        var employees = await db.Employees.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.IsActive)
            .OrderBy(x => x.FirstName)
            .ThenBy(x => x.LastName)
            .ToListAsync();
        var shifts = await db.ScheduleShifts.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.ShiftDate >= from && x.ShiftDate <= to)
            .OrderBy(x => x.StartTime)
            .ToListAsync();

        _loadingGrid = true;
        try
        {
            for (var day = 0; day < 7; day++)
            {
                var date = from.AddDays(day);
                _grid.Columns[day + 1].HeaderText =
                    $"{date:dddd}\n{date:M/d/yyyy}".ToUpperInvariant();
            }

            _grid.Rows.Clear();
            foreach (var employee in employees)
            {
                var rowIndex = _grid.Rows.Add();
                var row = _grid.Rows[rowIndex];
                row.Tag = employee.Id;
                row.Cells[0].Value = employee.FullName;
                row.Cells[0].Style.Font = WinTheme.BoldFont(9);
                row.Cells[0].Style.BackColor = Color.FromArgb(232, 241, 252);
                for (var day = 0; day < 7; day++)
                {
                    var date = from.AddDays(day);
                    var daily = shifts
                        .Where(x => x.EmployeeId == employee.Id && x.ShiftDate == date)
                        .OrderBy(x => x.StartTime)
                        .ToList();
                    var cell = row.Cells[day + 1];
                    cell.Value = daily.Count == 0
                        ? "OFF"
                        : string.Join("; ", daily.Select(FormatShift));
                    var locked = daily.Any(x => x.Status != ScheduleShiftStatus.Draft);
                    cell.ReadOnly = locked;
                    cell.ToolTipText = locked
                        ? "This day contains a published or completed shift. Use Manage Schedule to change its status first."
                        : "Enter a time range or OFF.";
                    ApplyCellStyle(cell);
                }
            }
        }
        finally
        {
            _loadingGrid = false;
        }

        _hasUnsavedChanges = false;
        _grid.ClearSelection();
        UpdateSelectionStatus();
        _status.Text = employees.Count == 0
            ? "No active employees exist for this store. Add employees in Payroll first."
            : $"Loaded {employees.Count} active employee(s) for {from:MMM d} - {to:MMM d, yyyy}.";
    }

    private async Task CopyPreviousWeekAsync()
    {
        var currentFrom = StartOfWeek(DateOnly.FromDateTime(_weekStart.Value));
        var previousFrom = currentFrom.AddDays(-7);
        var previousTo = previousFrom.AddDays(6);
        await using var db = _createDb();
        var previousShifts = await db.ScheduleShifts.AsNoTracking()
            .Where(x => x.StoreId == _storeId &&
                        x.ShiftDate >= previousFrom &&
                        x.ShiftDate <= previousTo &&
                        x.Status != ScheduleShiftStatus.Cancelled)
            .OrderBy(x => x.StartTime)
            .ToListAsync();
        if (previousShifts.Count == 0)
        {
            MessageBox.Show(
                this,
                $"No schedule entries were found for {previousFrom:MMM d} - {previousTo:MMM d, yyyy}.",
                "Copy Previous Week",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _loadingGrid = true;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not int employeeId)
                    continue;
                for (var day = 0; day < 7; day++)
                {
                    var cell = row.Cells[day + 1];
                    if (cell.ReadOnly)
                        continue;
                    var sourceDate = previousFrom.AddDays(day);
                    var daily = previousShifts
                        .Where(x => x.EmployeeId == employeeId && x.ShiftDate == sourceDate)
                        .OrderBy(x => x.StartTime)
                        .ToList();
                    cell.Value = daily.Count == 0
                        ? "OFF"
                        : string.Join("; ", daily.Select(FormatShift));
                    ApplyCellStyle(cell);
                }
            }
        }
        finally
        {
            _loadingGrid = false;
        }

        _hasUnsavedChanges = true;
        _status.Text = $"Copied the {previousFrom:MMM d} - {previousTo:MMM d, yyyy} schedule into this board. Review it, then save or export.";
        UpdateSelectionStatus();
    }

    private void ApplyValueToSelectedCells(string value)
    {
        _grid.EndEdit();
        var open = TimeOnly.FromDateTime(_openingTime.Value).ToTimeSpan();
        var close = TimeOnly.FromDateTime(_closingTime.Value).ToTimeSpan();
        if (!TryParseCell(value.Trim(), open, close, out _, out var error))
        {
            MessageBox.Show(this, error, "Invalid Quick Shift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _quickShift.Focus();
            _quickShift.SelectAll();
            return;
        }

        var cells = _grid.SelectedCells
            .Cast<DataGridViewCell>()
            .Where(cell => cell.ColumnIndex > 0 && !cell.ReadOnly)
            .ToList();
        if (cells.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select one or more editable day cells first. You can drag across cells or hold Ctrl while selecting employees and days.",
                "Select Schedule Cells",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _loadingGrid = true;
        try
        {
            foreach (var cell in cells)
            {
                cell.Value = string.IsNullOrWhiteSpace(value) ? "OFF" : value.Trim().ToUpperInvariant();
                ApplyCellStyle(cell);
            }
        }
        finally
        {
            _loadingGrid = false;
        }

        _hasUnsavedChanges = true;
        _status.Text = $"Applied '{value.Trim().ToUpperInvariant()}' to {cells.Count} selected schedule cell(s).";
        UpdateSelectionStatus();
    }

    private void ClearEditableWeek()
    {
        if (MessageBox.Show(
                this,
                "Set every editable cell on this weekly board to OFF? Published or completed cells will remain unchanged.",
                "Clear Draft Week",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _loadingGrid = true;
        var changed = 0;
        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                for (var column = 1; column < _grid.Columns.Count; column++)
                {
                    var cell = row.Cells[column];
                    if (cell.ReadOnly)
                        continue;
                    cell.Value = "OFF";
                    ApplyCellStyle(cell);
                    changed++;
                }
            }
        }
        finally
        {
            _loadingGrid = false;
        }

        _hasUnsavedChanges |= changed > 0;
        _status.Text = $"Set {changed} editable schedule cell(s) to OFF. Save the week to apply the change.";
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        var selected = _grid.SelectedCells.Cast<DataGridViewCell>().Count(cell => cell.ColumnIndex > 0);
        var editable = _grid.SelectedCells.Cast<DataGridViewCell>().Count(cell => cell.ColumnIndex > 0 && !cell.ReadOnly);
        _selectionStatus.Text = selected == 0
            ? "Select one or more schedule cells"
            : editable == selected
                ? $"{editable} editable cell(s) selected"
                : $"{editable} editable selected; {selected - editable} locked ignored";
    }

    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!_hasUnsavedChanges)
            return true;
        return MessageBox.Show(
                   this,
                   "This weekly board contains unsaved changes. Discard them?",
                   "Unsaved Schedule",
                   MessageBoxButtons.YesNo,
                   MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private async Task<bool> SaveWeekAsync(bool showConfirmation)
    {
        _grid.EndEdit();
        var from = StartOfWeek(DateOnly.FromDateTime(_weekStart.Value));
        var to = from.AddDays(6);
        var open = TimeOnly.FromDateTime(_openingTime.Value).ToTimeSpan();
        var close = TimeOnly.FromDateTime(_closingTime.Value).ToTimeSpan();
        var planned = new List<PlannedCell>();
        var errors = new List<string>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not int employeeId)
                continue;
            var employeeName = Convert.ToString(row.Cells[0].Value, CultureInfo.CurrentCulture) ?? $"Employee #{employeeId}";
            for (var day = 0; day < 7; day++)
            {
                var cell = row.Cells[day + 1];
                if (cell.ReadOnly)
                    continue;
                var value = Convert.ToString(cell.Value, CultureInfo.CurrentCulture)?.Trim() ?? "";
                var date = from.AddDays(day);
                if (!TryParseCell(value, open, close, out var ranges, out var error))
                {
                    errors.Add($"{employeeName}, {date:ddd M/d}: {error}");
                    continue;
                }
                planned.Add(new PlannedCell(employeeId, date, ranges));
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                "Correct these schedule entries before saving:\n\n" +
                string.Join("\n", errors.Take(12)) +
                (errors.Count > 12 ? $"\n...and {errors.Count - 12} more." : ""),
                "Invalid Schedule Entry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        await using var db = _createDb();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var employeeIds = planned.Select(x => x.EmployeeId).Distinct().ToList();
        var plannedKeys = planned
            .Select(x => (x.EmployeeId, x.Date))
            .ToHashSet();
        var existing = await db.ScheduleShifts
            .Where(x => x.StoreId == _storeId &&
                        employeeIds.Contains(x.EmployeeId) &&
                        x.ShiftDate >= from &&
                        x.ShiftDate <= to &&
                        x.Status == ScheduleShiftStatus.Draft)
            .ToListAsync();
        db.ScheduleShifts.RemoveRange(existing.Where(x => plannedKeys.Contains((x.EmployeeId, x.ShiftDate))));

        foreach (var cell in planned)
        {
            foreach (var range in cell.Ranges)
            {
                db.ScheduleShifts.Add(new ScheduleShift
                {
                    StoreId = _storeId,
                    EmployeeId = cell.EmployeeId,
                    ShiftDate = cell.Date,
                    StartTime = range.Start,
                    EndTime = range.End,
                    UnpaidBreakMinutes = 0,
                    Status = ScheduleShiftStatus.Draft,
                    UpdatedByName = _user,
                    UpdatedUtc = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await LoadWeekAsync();
        _status.Text = $"Weekly schedule saved for {from:MMM d} - {to:MMM d, yyyy}. OFF days contain no payable hours.";
        if (showConfirmation)
        {
            MessageBox.Show(
                this,
                $"The weekly schedule for {from:MMM d} - {to:MMM d, yyyy} was saved for all employees.",
                "Weekly Schedule Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        return true;
    }

    private async Task ExportPdfAsync()
    {
        if (!await SaveWeekAsync(showConfirmation: false))
            return;
        var from = StartOfWeek(DateOnly.FromDateTime(_weekStart.Value));
        var to = from.AddDays(6);
        await using var db = _createDb();
        var shifts = await db.ScheduleShifts.AsNoTracking()
            .Where(x => x.StoreId == _storeId &&
                        x.ShiftDate >= from &&
                        x.ShiftDate <= to &&
                        x.Status != ScheduleShiftStatus.Cancelled)
            .OrderBy(x => x.ShiftDate)
            .ThenBy(x => x.StartTime)
            .ToListAsync();
        var employees = await db.Employees.AsNoTracking()
            .Where(x => x.StoreId == _storeId && x.IsActive)
            .ToDictionaryAsync(x => x.Id);
        if (employees.Count == 0)
        {
            MessageBox.Show(this, "Add at least one active employee before exporting a schedule.", "Export Schedule", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _storeId)
                    ?? await db.Stores.AsNoTracking().FirstOrDefaultAsync();

        using var save = new SaveFileDialog
        {
            Title = "Export Weekly Employee Schedule",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = $"Employee_Schedule_{from:yyyyMMdd}_{to:yyyyMMdd}.pdf",
            AddExtension = true,
            DefaultExt = "pdf"
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        SchedulePdf.Generate(
            store?.Name ?? "Business",
            from,
            to,
            shifts,
            employees,
            save.FileName);
        _status.Text = $"Schedule PDF exported to {save.FileName}";
        MessageBox.Show(
            this,
            $"The weekly schedule PDF was exported successfully.\n\n{save.FileName}",
            "Schedule Exported",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static bool TryParseCell(
        string value,
        TimeSpan openingTime,
        TimeSpan closingTime,
        out IReadOnlyList<ShiftRange> ranges,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("R.O.", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RO", StringComparison.OrdinalIgnoreCase))
        {
            ranges = Array.Empty<ShiftRange>();
            error = "";
            return true;
        }

        var result = new List<ShiftRange>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = ShiftRangePattern.Match(part);
            if (!match.Success)
            {
                ranges = Array.Empty<ShiftRange>();
                error = $"'{part}' must look like 9-5, 2-8 PM, OPEN-5, 5-CLOSE, or OFF.";
                return false;
            }

            if (!TryParseTimeToken(match.Groups["start"].Value, openingTime, closingTime, true, null, out var start) ||
                !TryParseTimeToken(match.Groups["end"].Value, openingTime, closingTime, false, start, out var end))
            {
                ranges = Array.Empty<ShiftRange>();
                error = $"'{part}' contains an invalid time.";
                return false;
            }

            var adjustedEnd = end <= start ? end.Add(TimeSpan.FromDays(1)) : end;
            var duration = adjustedEnd - start;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(18))
            {
                ranges = Array.Empty<ShiftRange>();
                error = $"'{part}' creates an invalid shift longer than 18 hours.";
                return false;
            }
            result.Add(new ShiftRange(start, end));
        }

        ranges = result;
        error = "";
        return true;
    }

    private static bool TryParseTimeToken(
        string token,
        TimeSpan openingTime,
        TimeSpan closingTime,
        bool isStart,
        TimeSpan? start,
        out TimeSpan value)
    {
        token = token.Trim();
        if (token.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            value = openingTime;
            return true;
        }
        if (token.Equals("CLOSE", StringComparison.OrdinalIgnoreCase))
        {
            value = closingTime;
            return true;
        }

        var hasMeridiem = token.Contains("AM", StringComparison.OrdinalIgnoreCase) ||
                           token.Contains("PM", StringComparison.OrdinalIgnoreCase);
        if (hasMeridiem &&
            DateTime.TryParse(token, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dated))
        {
            value = dated.TimeOfDay;
            return true;
        }

        var pieces = token.Split(':', StringSplitOptions.TrimEntries);
        if (pieces.Length is < 1 or > 2 ||
            !int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            hour is < 0 or > 23)
        {
            value = default;
            return false;
        }
        var minute = 0;
        if (pieces.Length == 2 &&
            (!int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) ||
             minute is < 0 or > 59))
        {
            value = default;
            return false;
        }

        if (hour is >= 1 and <= 6)
            hour += 12;
        var candidate = TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(minute);
        if (!isStart && start.HasValue && candidate <= start.Value && hour <= 11)
            candidate = candidate.Add(TimeSpan.FromHours(12));
        value = candidate;
        return true;
    }

    private static string FormatShift(ScheduleShift shift)
    {
        var start = DateTime.Today.Add(shift.StartTime).ToString("h:mm tt");
        var end = DateTime.Today.Add(shift.EndTime).ToString("h:mm tt");
        return $"{start}-{end}";
    }

    private static void ApplyCellStyle(DataGridViewCell cell)
    {
        var off = string.IsNullOrWhiteSpace(Convert.ToString(cell.Value, CultureInfo.CurrentCulture)) ||
                  string.Equals(Convert.ToString(cell.Value, CultureInfo.CurrentCulture)?.Trim(), "OFF", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(Convert.ToString(cell.Value, CultureInfo.CurrentCulture)?.Trim(), "R.O.", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(Convert.ToString(cell.Value, CultureInfo.CurrentCulture)?.Trim(), "RO", StringComparison.OrdinalIgnoreCase);
        cell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        cell.Style.WrapMode = DataGridViewTriState.True;
        cell.Style.Font = WinTheme.BoldFont(8.5f);
        if (cell.ReadOnly)
        {
            cell.Style.BackColor = Color.FromArgb(232, 232, 232);
            cell.Style.ForeColor = WinTheme.Muted;
        }
        else if (off)
        {
            cell.Style.BackColor = Color.FromArgb(255, 220, 220);
            cell.Style.ForeColor = Color.FromArgb(183, 28, 28);
        }
        else
        {
            cell.Style.BackColor = Color.White;
            cell.Style.ForeColor = WinTheme.Text;
        }
    }

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static DateTimePicker TimePicker(int hour) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "h:mm tt",
        ShowUpDown = true,
        Value = DateTime.Today.AddHours(hour)
    };

    private sealed record PlannedCell(
        int EmployeeId,
        DateOnly Date,
        IReadOnlyList<ShiftRange> Ranges);

    private readonly record struct ShiftRange(TimeSpan Start, TimeSpan End);
}
