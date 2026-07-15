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
            RowCount = 2,
            Padding = Padding.Empty,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18, 16, 18, 18)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        body.Controls.Add(BuildLeftColumn(), 0, 0);
        body.Controls.Add(BuildRightColumn(), 1, 0);
        root.Controls.Add(body, 0, 1);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.BlueDark, Padding = new Padding(24, 10, 28, 10) };
        header.Paint += (_, e) => AdminTheme.PaintGradient(e, header.ClientRectangle);

        var logo = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = 104,
            Image = AdminTheme.LoadLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 12, 0)
        };

        var titles = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 8, 0, 0)
        };
        var title = AdminTheme.Label("HISAB KITAB WORKS", Color.White, 19, true);
        title.Size = new Size(420, 40);
        var subtitle = AdminTheme.Label("ADMIN LICENSE GENERATOR", AdminTheme.Copper, 10.5f, true);
        subtitle.Size = new Size(320, 26);
        subtitle.Margin = new Padding(0, 4, 0, 0);
        titles.Controls.Add(title);
        titles.Controls.Add(subtitle);

        // Add the fill control before the left-docked logo so the title receives
        // all remaining header width.
        header.Controls.Add(titles);
        header.Controls.Add(logo);
        return header;
    }

    private Control BuildLeftColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 9, 0)
        };
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 262));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.Controls.Add(BuildConnectionCard(), 0, 0);
        column.Controls.Add(BuildCustomerCard(), 0, 1);
        return column;
    }

    private Control BuildConnectionCard()
    {
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 0, 10);
        card.Padding = new Padding(18, 14, 18, 14);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(SectionTitle("DATABASE CONNECTION", "\uE968"), 0, 0);
        layout.Controls.Add(BuildField("SQL SERVER *", _server), 0, 1);

        var credentials = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        credentials.Controls.Add(BuildField("USERNAME *", _username), 0, 0);
        credentials.Controls.Add(BuildField("PASSWORD *", _password), 1, 0);
        layout.Controls.Add(credentials, 0, 2);

        var connectionRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2, Padding = new Padding(0, 7, 0, 0) };
        connectionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        connectionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _connect.Dock = DockStyle.Fill;
        _connect.Margin = new Padding(0, 0, 12, 4);
        connectionRow.Controls.Add(_connect, 0, 0);
        _dbStatus.Dock = DockStyle.Fill;
        _dbStatus.TextAlign = ContentAlignment.MiddleLeft;
        connectionRow.Controls.Add(_dbStatus, 1, 0);
        layout.Controls.Add(connectionRow, 0, 3);

        card.Controls.Add(layout);
        return card;
    }

    private Control BuildCustomerCard()
    {
        var card = AdminTheme.Card();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(18, 14, 18, 14);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        layout.Controls.Add(SectionTitle("CUSTOMER INFORMATION", "\uE716"), 0, 0);
        layout.Controls.Add(BuildField("STORE / BUSINESS NAME *", _storeName), 0, 1);
        layout.Controls.Add(BuildField("OWNER NAME *", _ownerName), 0, 2);
        layout.Controls.Add(BuildField("EMAIL *", _email), 0, 3);
        layout.Controls.Add(BuildNumberField("MAXIMUM PC SEATS *", _maxDevices), 0, 4);

        var contact = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        contact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        contact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        contact.Controls.Add(BuildField("STORE ZIP CODE", _zip), 0, 0);
        contact.Controls.Add(BuildField("PHONE", _phone), 1, 0);
        layout.Controls.Add(contact, 0, 5);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildRightColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(9, 0, 0, 0)
        };
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 266));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.Controls.Add(BuildSecurityCard(), 0, 0);
        column.Controls.Add(BuildActions(), 0, 1);
        column.Controls.Add(BuildResultCard(), 0, 2);
        column.Controls.Add(BuildStatusCard(), 0, 3);
        return column;
    }

    private Control BuildSecurityCard()
    {
        var card = AdminTheme.Card(AdminTheme.CopperDark);
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 0, 10);
        card.Padding = new Padding(18, 12, 18, 12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 3, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = AdminTheme.Label("\uE72E", AdminTheme.Copper, 20);
        icon.Font = AdminTheme.Icon(20);
        icon.Dock = DockStyle.Fill;
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(AdminTheme.Label("OFFLINE LICENSE SIGNING", AdminTheme.BlueDark, 10.5f, true), 1, 0);
        _signingStatus.Dock = DockStyle.Fill;
        layout.Controls.Add(_signingStatus, 1, 1);
        _importSigningKey.Dock = DockStyle.Fill;
        _importSigningKey.Margin = new Padding(10, 7, 0, 7);
        layout.Controls.Add(_importSigningKey, 2, 0);
        layout.SetRowSpan(_importSigningKey, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildActions()
    {
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 3, Padding = new Padding(0, 0, 0, 10) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _generate.Dock = DockStyle.Fill;
        _lookup.Dock = DockStyle.Fill;
        _deviceLicenses.Dock = DockStyle.Fill;
        _generate.Margin = new Padding(0, 0, 8, 0);
        _lookup.Margin = new Padding(8, 0, 8, 0);
        _deviceLicenses.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(_generate, 0, 0);
        actions.Controls.Add(_lookup, 1, 0);
        actions.Controls.Add(_deviceLicenses, 2, 0);
        return actions;
    }

    private Control BuildResultCard()
    {
        _resultCard = AdminTheme.Card(AdminTheme.CopperDark);
        _resultCard.Dock = DockStyle.Fill;
        _resultCard.Margin = new Padding(0, 0, 0, 10);
        _resultCard.Padding = new Padding(22, 12, 22, 12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var heading = AdminTheme.Label("SUBSCRIPTION KEY - DEVICE FILES ARE ISSUED SEPARATELY", AdminTheme.Muted, 9.5f, true);
        heading.Dock = DockStyle.Fill;
        heading.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(heading, 0, 0);
        _keyValue.Dock = DockStyle.Fill;
        _keyValue.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(_keyValue, 0, 1);
        _databaseDetails.Dock = DockStyle.Fill;
        _databaseDetails.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(_databaseDetails, 0, 2);

        var hint = AdminTheme.Label("Import the customer's .hbrequest file to issue a license for that specific PC.", AdminTheme.Muted, 10);
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(hint, 0, 3);

        var resultActions = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        resultActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        resultActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        _copyKey.Dock = DockStyle.Fill;
        _exportLicense.Dock = DockStyle.Fill;
        _copyKey.Margin = new Padding(0, 4, 8, 6);
        _exportLicense.Margin = new Padding(8, 4, 0, 6);
        resultActions.Controls.Add(_copyKey, 0, 0);
        resultActions.Controls.Add(_exportLicense, 1, 0);
        layout.Controls.Add(resultActions, 0, 4);
        _resultCard.Controls.Add(layout);
        return _resultCard;
    }

    private Control BuildStatusCard()
    {
        _statusCard = AdminTheme.Card(AdminTheme.Panel2);
        _statusCard.Dock = DockStyle.Fill;
        _statusCard.Padding = new Padding(20);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _statusIcon.Dock = DockStyle.Fill;
        _statusIcon.Font = AdminTheme.Icon(26);
        _statusIcon.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(_statusIcon, 0, 0);
        _statusText.Dock = DockStyle.Fill;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_statusText, 1, 0);
        _statusCard.Controls.Add(layout);
        return _statusCard;
    }

    private static Control BuildField(string caption, TextBox input)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 5) };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        input.Dock = DockStyle.Fill;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private static Control BuildNumberField(string caption, NumericUpDown input)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 5) };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        input.Dock = DockStyle.Fill;
        field.Controls.Add(label, 0, 0);
        field.Controls.Add(input, 0, 1);
        return field;
    }

    private static Label SectionTitle(string text, string glyph)
    {
        var label = AdminTheme.Label($"{glyph}  {text}", AdminTheme.Copper, 10.5f, true);
        label.Dock = DockStyle.Fill;
        return label;
    }
}
