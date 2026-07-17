namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed partial class MainForm
{
    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSetupArea(), 0, 1);
        root.Controls.Add(BuildWorkflowArea(), 0, 2);
        root.Controls.Add(BuildStatusBar(), 0, 3);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.BlueDark, Padding = new Padding(28, 8, 28, 8) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);
        var logo = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = 112,
            Image = AdminTheme.LoadLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        var titles = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18, 10, 0, 6)
        };
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        titles.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        var title = AdminTheme.Label("HISAB KITAB WORKS", Color.White, 20, true);
        title.Dock = DockStyle.Fill;
        var subtitle = AdminTheme.Label("DEVELOPER LICENSE GENERATOR  •  STORE & PC ACTIVATION", AdminTheme.Copper, 10.5f, true);
        subtitle.Dock = DockStyle.Fill;
        titles.Controls.Add(title, 0, 0);
        titles.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(titles);
        header.Controls.Add(logo);
        return header;
    }

    private Control BuildSetupArea()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, Padding = new Padding(20, 14, 20, 8) };
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(16, 10, 16, 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.Controls.Add(BuildDatabaseSetup(), 0, 0);
        layout.Controls.Add(BuildSigningSetup(), 1, 0);
        card.Controls.Add(layout);
        host.Controls.Add(card);
        return host;
    }

    private Control BuildDatabaseSetup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 4, RowCount = 3, Margin = new Padding(0, 0, 14, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = AdminTheme.Label("LICENSING DATABASE", AdminTheme.Copper, 10, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 4);
        AddSetupInput(layout, "SQL SERVER", _server, 0);
        AddSetupInput(layout, "USERNAME", _username, 1);
        AddSetupInput(layout, "PASSWORD", _password, 2);
        _connect.Dock = DockStyle.Fill;
        _connect.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(_connect, 3, 1);
        _connectionStatus.Dock = DockStyle.Fill;
        layout.Controls.Add(_connectionStatus, 0, 2);
        layout.SetColumnSpan(_connectionStatus, 4);
        return layout;
    }

    private static void AddSetupInput(TableLayoutPanel layout, string placeholder, TextBox input, int column)
    {
        input.Dock = DockStyle.Fill;
        input.PlaceholderText = placeholder;
        input.Margin = column == 0 ? new Padding(0, 0, 8, 0) : new Padding(8, 0, 8, 0);
        layout.Controls.Add(input, column, 1);
    }

    private Control BuildSigningSetup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, RowCount = 3, Margin = new Padding(14, 0, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = AdminTheme.Label("PRIVATE LICENSE SIGNING", AdminTheme.Copper, 10, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        _setupSigning.Dock = DockStyle.Fill;
        _backupSigning.Dock = DockStyle.Fill;
        _setupSigning.Margin = new Padding(0, 0, 6, 0);
        _backupSigning.Margin = new Padding(6, 0, 0, 0);
        layout.Controls.Add(_setupSigning, 0, 1);
        layout.Controls.Add(_backupSigning, 1, 1);
        _signingStatus.Dock = DockStyle.Fill;
        layout.Controls.Add(_signingStatus, 0, 2);
        layout.SetColumnSpan(_signingStatus, 2);
        return layout;
    }

    private Control BuildWorkflowArea()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(20, 6, 20, 8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.Controls.Add(BuildActivationCard(), 0, 0);
        layout.Controls.Add(BuildResultsColumn(), 1, 0);
        return layout;
    }

    private Control BuildActivationCard()
    {
        var card = AdminTheme.Card(AdminTheme.CopperDark);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 10, 0);
        card.Padding = new Padding(20, 10, 20, 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 9 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        var heading = AdminTheme.Label("CUSTOMER ACTIVATION INFORMATION", AdminTheme.Copper, 12, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        var directions = AdminTheme.Label(
            "Paste each value into its matching field. For a first-time PC, use the COPY button beside PC ID on the customer screen so its protected proof is included.",
            AdminTheme.Muted, 9.5f);
        directions.Dock = DockStyle.Fill;
        layout.Controls.Add(directions, 0, 1);

        _storeGuid.BackColor = Color.FromArgb(25, 25, 25);
        _storeGuid.ForeColor = Color.FromArgb(255, 224, 58);
        _storeGuid.Font = AdminTheme.Bold(10.5f);
        _pcId.BackColor = Color.FromArgb(225, 252, 228);
        _pcId.ForeColor = AdminTheme.Green;
        _pcId.Font = AdminTheme.Bold(10.5f);
        layout.Controls.Add(BuildPasteField("1. STORE GUID", _storeGuid, _pasteStoreGuid), 0, 2);
        layout.Controls.Add(BuildPasteField("2. PC ID", _pcId, _pastePcId), 0, 3);
        layout.Controls.Add(BuildPasteField("3. STORE NAME", _storeName, _pasteStoreName), 0, 4);
        layout.Controls.Add(BuildPasteField("4. STORE ZIP", _storeZip, _pasteStoreZip), 0, 5);
        layout.Controls.Add(BuildDatabaseField(), 0, 6);
        layout.Controls.Add(BuildSubscriptionLimits(), 0, 7);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        _generate.Dock = DockStyle.Fill;
        _clear.Dock = DockStyle.Fill;
        _generate.Margin = new Padding(0, 4, 8, 0);
        _clear.Margin = new Padding(8, 4, 0, 0);
        actions.Controls.Add(_generate, 0, 0);
        actions.Controls.Add(_clear, 1, 0);
        layout.Controls.Add(actions, 0, 8);
        card.Controls.Add(layout);
        return card;
    }

    private static Control BuildPasteField(string caption, Control input, Button paste)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        layout.Controls.Add(label, 0, 0);
        layout.SetColumnSpan(label, 2);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 0, 8, 3);
        paste.Dock = DockStyle.Fill;
        paste.Margin = new Padding(8, 0, 0, 3);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(paste, 1, 1);
        return layout;
    }

    private Control BuildDatabaseField()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label("5. EXISTING SQL DATABASE  (BLANK CREATES A NEW DATABASE)", AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        layout.Controls.Add(label, 0, 0);
        _databaseName.Dock = DockStyle.Fill;
        _databaseName.Margin = new Padding(0, 0, 0, 3);
        layout.Controls.Add(_databaseName, 0, 1);
        return layout;
    }

    private Control BuildSubscriptionLimits()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel2, ColumnCount = 3, RowCount = 2, Padding = new Padding(10, 5, 10, 5) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(AdminTheme.Label("PAID PC SEATS", AdminTheme.Muted, 8.5f, true), 0, 0);
        card.Controls.Add(AdminTheme.Label("BUSINESS LIMIT", AdminTheme.Muted, 8.5f, true), 1, 0);
        card.Controls.Add(AdminTheme.Label("LICENSE EXPIRES", AdminTheme.Muted, 8.5f, true), 2, 0);
        _maxDevices.Dock = DockStyle.Fill;
        _maxBusinesses.Dock = DockStyle.Fill;
        _expires.Dock = DockStyle.Fill;
        _maxDevices.Margin = new Padding(0, 0, 8, 0);
        _maxBusinesses.Margin = new Padding(8, 0, 8, 0);
        _expires.Margin = new Padding(8, 0, 0, 0);
        card.Controls.Add(_maxDevices, 0, 1);
        card.Controls.Add(_maxBusinesses, 1, 1);
        card.Controls.Add(_expires, 2, 1);
        return card;
    }

    private Control BuildResultsColumn()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 1, RowCount = 2, Margin = new Padding(10, 0, 0, 0) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.Controls.Add(BuildLicenseResultCard(), 0, 0);
        layout.Controls.Add(BuildPcListCard(), 0, 1);
        return layout;
    }

    private Control BuildLicenseResultCard()
    {
        var card = AdminTheme.Card(AdminTheme.CopperDark);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 0, 8);
        card.Padding = new Padding(16, 12, 16, 12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var heading = AdminTheme.Label("GENERATED CUSTOMER LICENSE KEY", AdminTheme.Copper, 11, true);
        heading.Dock = DockStyle.Fill;
        layout.Controls.Add(heading, 0, 0);
        _resultSummary.Dock = DockStyle.Fill;
        layout.Controls.Add(_resultSummary, 0, 1);
        _licenseOutput.Dock = DockStyle.Fill;
        layout.Controls.Add(_licenseOutput, 0, 2);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _copyLicense.Dock = DockStyle.Fill;
        _saveLicense.Dock = DockStyle.Fill;
        _copyLicense.Margin = new Padding(0, 6, 6, 0);
        _saveLicense.Margin = new Padding(6, 6, 0, 0);
        actions.Controls.Add(_copyLicense, 0, 0);
        actions.Controls.Add(_saveLicense, 1, 0);
        layout.Controls.Add(actions, 0, 3);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildPcListCard()
    {
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 8, 0, 0);
        card.Padding = new Padding(12, 10, 12, 12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var heading = AdminTheme.Label("REGISTERED PCS FOR THIS STORE GUID", AdminTheme.BlueDark, 10, true);
        heading.Dock = DockStyle.Fill;
        _manageBusinesses.Dock = DockStyle.Fill;
        _manageBusinesses.Margin = new Padding(8, 0, 0, 0);
        titleRow.Controls.Add(heading, 0, 0);
        titleRow.Controls.Add(_manageBusinesses, 1, 0);
        layout.Controls.Add(titleRow, 0, 0);
        _registeredPcs.Dock = DockStyle.Fill;
        layout.Controls.Add(_registeredPcs, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildStatusBar()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, Padding = new Padding(20, 6, 20, 14) };
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(16, 4, 16, 4);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(_status);
        host.Controls.Add(card);
        return host;
    }
}
