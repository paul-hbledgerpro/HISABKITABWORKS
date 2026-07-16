using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class CustomerBusinessesForm : Form
{
    private readonly string _licensingConnectionString;
    private readonly int _customerId;
    private readonly int _maxBusinesses;
    private readonly DataGridView _grid = new();
    private readonly TextBox _name = AdminTheme.TextBox();
    private readonly TextBox _address = AdminTheme.TextBox();
    private readonly TextBox _database = AdminTheme.TextBox();
    private readonly Label _status = AdminTheme.Label("", AdminTheme.Muted, 9.5f);
    private int? _editingId;
    private bool _editingPrimary;

    public CustomerBusinessesForm(
        string licensingConnectionString,
        int customerId,
        string customerName,
        int maxBusinesses)
    {
        _licensingConnectionString = licensingConnectionString;
        _customerId = customerId;
        _maxBusinesses = Math.Max(1, maxBusinesses);

        Text = $"Approved Businesses - {customerName}";
        BackColor = AdminTheme.Bg;
        ForeColor = AdminTheme.Text;
        Font = AdminTheme.Body();
        Icon = AdminTheme.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1020, 680);
        MinimumSize = new Size(900, 620);
        Controls.Add(BuildLayout(customerName));
        ConfigureGrid();
        Load += (_, _) => RefreshGrid();
    }

    private Control BuildLayout(string customerName)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Bg,
            Padding = new Padding(18),
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, Padding = new Padding(20, 10, 20, 8) };
        header.Controls.Add(new Label
        {
            Text = "CLIENT BUSINESS DIRECTORY",
            Dock = DockStyle.Top,
            Height = 34,
            ForeColor = AdminTheme.BlueDark,
            Font = AdminTheme.Header(17),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = $"Client account: {customerName}   |   Licensed businesses: maximum {_maxBusinesses}",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = AdminTheme.Copper,
            Font = AdminTheme.Body(9.5f),
            BackColor = Color.Transparent
        });
        root.Controls.Add(header, 0, 0);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AdminTheme.Panel,
            Padding = new Padding(16, 10, 16, 10),
            ColumnCount = 6,
            RowCount = 2
        };
        for (var i = 0; i < 6; i++)
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666f));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddField(fields, "BUSINESS / STORE NAME *", _name, 0, 0, 2);
        AddField(fields, "ADDRESS", _address, 3, 0, 2);
        AddField(fields, "DATABASE NAME *", _database, 0, 1, 5);
        root.Controls.Add(fields, 0, 1);

        _grid.Dock = DockStyle.Fill;
        root.Controls.Add(_grid, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Bg, ColumnCount = 5, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        var addNew = AdminTheme.Button("ADD NEW");
        var save = AdminTheme.Button("SAVE BUSINESS", true);
        var delete = AdminTheme.Button("DELETE");
        var close = AdminTheme.Button("CLOSE");
        foreach (var button in new[] { addNew, save, delete, close })
            button.Dock = DockStyle.Fill;
        addNew.Click += (_, _) => BeginNew();
        save.Click += (_, _) => SaveBusiness();
        delete.Click += (_, _) => DeleteBusiness();
        close.Click += (_, _) => Close();
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(addNew, 1, 0);
        footer.Controls.Add(save, 2, 0);
        footer.Controls.Add(delete, 3, 0);
        footer.Controls.Add(close, 4, 0);
        root.Controls.Add(footer, 0, 3);
        return root;
    }

    private static void AddField(TableLayoutPanel layout, string caption, Control field, int column, int row, int span)
    {
        var cell = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = AdminTheme.Panel, RowCount = 2, Margin = new Padding(4) };
        cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = AdminTheme.Label(caption, AdminTheme.Muted, 8.5f, true);
        label.Dock = DockStyle.Fill;
        field.Dock = DockStyle.Fill;
        cell.Controls.Add(label, 0, 0);
        cell.Controls.Add(field, 0, 1);
        layout.Controls.Add(cell, column, row);
        layout.SetColumnSpan(cell, span + 1);
    }

    private void ConfigureGrid()
    {
        _grid.BackgroundColor = Color.White;
        _grid.ForeColor = AdminTheme.Text;
        _grid.GridColor = AdminTheme.Panel2;
        _grid.BorderStyle = BorderStyle.None;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = AdminTheme.Copper;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.SelectionChanged += (_, _) => LoadSelected();
    }

    private void RefreshGrid()
    {
        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand(@"
SELECT Id, BusinessName, StoreAddress, DatabaseName, IsPrimary, IsActive
FROM dbo.CustomerBusinesses
WHERE CustomerId = @customerId
ORDER BY IsPrimary DESC, BusinessName", connection);
            command.Parameters.AddWithValue("@customerId", _customerId);
            using var reader = command.ExecuteReader();
            var rows = new List<BusinessRow>();
            while (reader.Read())
                rows.Add(new BusinessRow(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5)));
            _grid.DataSource = rows;
            _status.ForeColor = AdminTheme.Muted;
            _status.Text = $"{rows.Count(x => x.IsActive)} of {_maxBusinesses} approved business slot(s) configured.";
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void LoadSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not BusinessRow row)
            return;
        _editingId = row.Id;
        _editingPrimary = row.IsPrimary;
        _name.Text = row.BusinessName;
        _address.Text = row.StoreAddress;
        _database.Text = row.DatabaseName;
        _name.ReadOnly = row.IsPrimary;
        _database.ReadOnly = row.IsPrimary;
    }

    private void BeginNew()
    {
        _editingId = null;
        _editingPrimary = false;
        _name.ReadOnly = false;
        _database.ReadOnly = false;
        _name.Clear();
        _address.Clear();
        _database.Clear();
        _name.Focus();
        _status.ForeColor = AdminTheme.Copper;
        _status.Text = "Enter the approved business and its database, then save.";
    }

    private void SaveBusiness()
    {
        var name = _name.Text.Trim();
        var address = _address.Text.Trim();
        var database = _database.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(database))
        {
            SetError("Business name and database name are required.");
            return;
        }

        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            if (_editingId is null)
            {
                using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.CustomerBusinesses WHERE CustomerId=@customerId AND IsActive=1", connection);
                count.Parameters.AddWithValue("@customerId", _customerId);
                if (Convert.ToInt32(count.ExecuteScalar()) >= _maxBusinesses)
                    throw new InvalidOperationException($"This client is licensed for {_maxBusinesses} business(es). Increase the limit before adding another.");
            }

            using var duplicate = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.CustomerBusinesses
WHERE CustomerId=@customerId AND Id<>@id AND (BusinessName=@name OR DatabaseName=@database)", connection);
            duplicate.Parameters.AddWithValue("@customerId", _customerId);
            duplicate.Parameters.AddWithValue("@id", _editingId ?? 0);
            duplicate.Parameters.AddWithValue("@name", name);
            duplicate.Parameters.AddWithValue("@database", database);
            if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0)
                throw new InvalidOperationException("That business name or database is already assigned to this client.");

            if (_editingId is null)
            {
                using var insert = new SqlCommand(@"
INSERT dbo.CustomerBusinesses (CustomerId, BusinessName, StoreAddress, DatabaseName, IsPrimary, IsActive, CreatedUtc)
VALUES (@customerId, @name, @address, @database, 0, 1, SYSUTCDATETIME())", connection);
                AddParameters(insert, name, address, database);
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = new SqlCommand(@"
UPDATE dbo.CustomerBusinesses
SET BusinessName=@name, StoreAddress=@address, DatabaseName=@database, IsActive=1
WHERE Id=@id AND CustomerId=@customerId", connection);
                AddParameters(update, name, address, database);
                update.Parameters.AddWithValue("@id", _editingId.Value);
                update.ExecuteNonQuery();
            }

            _status.ForeColor = AdminTheme.Green;
            _status.Text = $"{name} saved. Reissue the PC license to deliver the updated business list.";
            RefreshGrid();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void AddParameters(SqlCommand command, string name, string address, string database)
    {
        command.Parameters.AddWithValue("@customerId", _customerId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@address", address);
        command.Parameters.AddWithValue("@database", database);
    }

    private void DeleteBusiness()
    {
        if (_editingId is null)
            return;
        if (_editingPrimary)
        {
            SetError("The primary login business cannot be deleted. Create a new client subscription if the primary business changes.");
            return;
        }
        if (MessageBox.Show(this, "Remove this business from the client's approved list?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            using var connection = new SqlConnection(_licensingConnectionString);
            connection.Open();
            using var command = new SqlCommand("UPDATE dbo.CustomerBusinesses SET IsActive=0 WHERE Id=@id AND CustomerId=@customerId AND IsPrimary=0", connection);
            command.Parameters.AddWithValue("@id", _editingId.Value);
            command.Parameters.AddWithValue("@customerId", _customerId);
            command.ExecuteNonQuery();
            BeginNew();
            RefreshGrid();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void SetError(string message)
    {
        _status.ForeColor = AdminTheme.Red;
        _status.Text = message;
    }

    private sealed record BusinessRow(int Id, string BusinessName, string StoreAddress, string DatabaseName, bool IsPrimary, bool IsActive);
}
