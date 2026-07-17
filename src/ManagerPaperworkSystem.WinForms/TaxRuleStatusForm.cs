using System.Diagnostics;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class TaxRuleStatusForm : Form
{
    private readonly DataGridView _grid = WinTheme.Grid();
    private readonly Label _summary = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.BlueDark,
        Font = WinTheme.BoldFont(10),
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = WinTheme.Muted,
        Font = WinTheme.BodyFont(9.5f),
        TextAlign = ContentAlignment.MiddleLeft
    };

    public TaxRuleStatusForm()
    {
        PayrollUi.Prepare(this, "Payroll Tax Rules - HISAB KITAB", new Size(1180, 800));
        MinimumSize = new Size(980, 700);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            BackColor = WinTheme.Bg,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.Controls.Add(PayrollUi.Heading(
            "PAYROLL TAX RULE STATUS  •  VERIFIED RULES ONLY"), 0, 0);
        root.Controls.Add(_summary, 0, 1);
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(_status, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = WinTheme.Panel
        };
        var close = PayrollUi.Button("CLOSE", false, 150);
        close.Click += (_, _) => Close();
        var source = PayrollUi.Button("OPEN OFFICIAL SOURCE", false, 210);
        source.Click += (_, _) => OpenSelectedSource();
        var update = PayrollUi.Button("CHECK TAX UPDATES", true, 210);
        update.Click += async (_, _) => await CheckUpdatesAsync();
        actions.Controls.AddRange(new Control[] { close, source, update });
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);

        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Shown += (_, _) => RefreshGrid();
    }

    private void RefreshGrid(string? message = null)
    {
        try
        {
            var package = TaxRulePackageService.LoadCurrent();
            var verified = package.RuleSet.States.Count(rule => rule.Verified);
            _summary.Text =
                $"Rule package {package.RuleSet.Version}  •  Federal {package.RuleSet.Federal.TaxYear}  •  " +
                $"{verified} of 51 state/DC rules verified  •  Effective {package.RuleSet.EffectiveFrom:MM/dd/yyyy} - {package.RuleSet.EffectiveTo:MM/dd/yyyy}";
            _grid.DataSource = package.RuleSet.States
                .OrderBy(rule => rule.StateName)
                .Select(rule => new
                {
                    State = $"{rule.StateName} ({rule.StateCode})",
                    Status = rule.Verified ? "VERIFIED" : "NOT YET SUPPORTED",
                    Method = rule.Verified ? MethodName(rule.Method) : "Payroll blocked",
                    Effective = $"{rule.EffectiveFrom:MM/dd/yyyy} - {rule.EffectiveTo:MM/dd/yyyy}",
                    Rule = rule.RuleId,
                    Source = rule.SourceAuthority,
                    SourceUrl = rule.SourceUrl
                })
                .ToList();
            if (_grid.Columns["SourceUrl"] is { } sourceColumn)
                sourceColumn.Visible = false;
            _status.ForeColor = message?.StartsWith("Tax update check failed", StringComparison.OrdinalIgnoreCase) == true
                ? Color.Firebrick
                : WinTheme.Muted;
            _status.Text = message ??
                "A state marked NOT YET SUPPORTED cannot be calculated. Install a signed update after its official rule is verified.";
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = ex.Message;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        _status.ForeColor = WinTheme.Muted;
        _status.Text = "Checking GitHub for a signed payroll tax package...";
        var result = await TaxRulePackageService.CheckForUpdatesAsync(true);
        RefreshGrid(result.Message);
    }

    private void OpenSelectedSource()
    {
        if (_grid.CurrentRow?.Cells["SourceUrl"].Value is not string url ||
            !Uri.TryCreate(url, UriKind.Absolute, out _))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static string MethodName(Core.Payroll.StateWithholdingMethod method) => method switch
    {
        Core.Payroll.StateWithholdingMethod.NoWithholding => "No state wage withholding",
        Core.Payroll.StateWithholdingMethod.FlatPercentage => "Flat percentage",
        Core.Payroll.StateWithholdingMethod.AnnualizedBrackets => "Annualized brackets",
        Core.Payroll.StateWithholdingMethod.PercentageOfFederalWithholding => "Percentage of federal",
        _ => "Unavailable"
    };
}
