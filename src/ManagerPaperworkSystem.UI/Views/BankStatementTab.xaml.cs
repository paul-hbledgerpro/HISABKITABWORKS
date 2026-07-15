using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WpfMessageBox = System.Windows.MessageBox;

namespace ManagerPaperworkSystem.UI.Views;

public partial class BankStatementTab : System.Windows.Controls.UserControl
{
    private IDbContextFactory<AppDbContext>? _dbFactory;
    private int _storeId = 1;
    private bool _initialized;
    private bool _loading;

    // Plaid bank connection
    private string? _plaidApiBaseUrl;
    private string? _authToken;
    private SessionState? _session;

    public BankStatementTab()
    {
        InitializeComponent();

        var today = DateTime.Today;
        cmbYear.ItemsSource = Enumerable.Range(today.Year - 5, 6).Reverse().ToList();
        cmbYear.SelectedItem = today.Year;

        var months = Enumerable.Range(1, 12)
            .Select(m => new KeyValuePair<int, string>(m, new DateTime(2000, m, 1).ToString("MMMM")))
            .ToList();
        cmbMonth.ItemsSource = months;
        cmbMonth.DisplayMemberPath = "Value";
        cmbMonth.SelectedValuePath = "Key";
        cmbMonth.SelectedValue = today.Month;
    }

    public void Initialize(IDbContextFactory<AppDbContext> dbFactory, int storeId, SessionState? session = null)
    {
        _dbFactory = dbFactory;
        _storeId = storeId;
        _session = session;
        _initialized = true;
        _ = EnsureTableAsync();
        _ = LoadDataAsync();
        _ = LoadPlaidConnectionStatusAsync();
    }

    /// <summary>Ensure we have a valid API token. Prompts user to login if needed. Returns true if token available.</summary>
    private async Task<bool> EnsureApiTokenAsync()
    {
        // Already have a token? Verify it's still valid
        if (!string.IsNullOrEmpty(_authToken))
        {
            try
            {
                var testResp = await ApiCallAsync(HttpMethod.Get, "/api/plaid/connections");
                if (testResp.IsSuccessStatusCode || (int)testResp.StatusCode != 401)
                    return true;
            }
            catch { }
            _authToken = null;
        }

        // Prompt user for password
        var username = _session?.Username ?? "";
        string password = "";

        var loginDialog = new Window
        {
            Title = "API Login — Required for Bank Connection",
            Width = 420, Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF0B0B0F")),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock
        {
            Text = "🔐 Enter your credentials to use bank features",
            FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD4AF37")),
            Margin = new Thickness(0, 0, 0, 16)
        });

        stack.Children.Add(new TextBlock { Text = "Username / Email", FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        var txtUser = new System.Windows.Controls.TextBox
        {
            Text = username, FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF13131A")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF222230"))
        };
        stack.Children.Add(txtUser);

        stack.Children.Add(new TextBlock { Text = "Password", FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 10, 0, 4) });
        var txtPass = new System.Windows.Controls.PasswordBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF13131A")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF222230"))
        };
        stack.Children.Add(txtPass);

        var btnLogin = new System.Windows.Controls.Button
        {
            Content = "🔑 Login & Continue", FontSize = 13, FontWeight = FontWeights.Bold,
            Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 20, 0, 0),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD4AF37")),
            Foreground = System.Windows.Media.Brushes.Black,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        btnLogin.Click += (s, ev) => { password = txtPass.Password; username = txtUser.Text; loginDialog.DialogResult = true; };
        stack.Children.Add(btnLogin);

        // Allow Enter key to submit from password field
        txtPass.KeyDown += (s, ev) => { if (ev.Key == System.Windows.Input.Key.Enter) { password = txtPass.Password; username = txtUser.Text; loginDialog.DialogResult = true; } };

        loginDialog.Content = stack;

        if (loginDialog.ShowDialog() != true || string.IsNullOrEmpty(password))
            return false;

        // Login to API
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync(
                GetApiBaseUrl().TrimEnd('/') + "/api/auth/login",
                new StringContent(JsonSerializer.Serialize(new { emailOrUsername = username.Trim(), password }),
                    System.Text.Encoding.UTF8, "application/json"));
            var respJson = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                WpfMessageBox.Show($"Login failed. Check your credentials.\n\n{respJson}",
                    "Auth Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            using var doc = JsonDocument.Parse(respJson);
            _authToken = doc.RootElement.GetProperty("token").GetString();

            // Handle store selection if required
            if (doc.RootElement.TryGetProperty("requiresStoreSelection", out var rss) && rss.GetBoolean())
            {
                var cs = GetConnectionString();
                var dbMatch = Regex.Match(cs, @"(?:Database|Initial Catalog)\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
                if (dbMatch.Success)
                {
                    using var client2 = new HttpClient();
                    var selectReq = new HttpRequestMessage(HttpMethod.Post,
                        GetApiBaseUrl().TrimEnd('/') + "/api/auth/select-store");
                    selectReq.Headers.Add("Authorization", $"Bearer {_authToken}");
                    selectReq.Content = new StringContent(
                        JsonSerializer.Serialize(new { databaseName = dbMatch.Groups[1].Value }),
                        System.Text.Encoding.UTF8, "application/json");
                    var selectResp = await client2.SendAsync(selectReq);
                    if (selectResp.IsSuccessStatusCode)
                    {
                        var selectJson = await selectResp.Content.ReadAsStringAsync();
                        using var selectDoc = JsonDocument.Parse(selectJson);
                        if (selectDoc.RootElement.TryGetProperty("token", out var newToken))
                            _authToken = newToken.GetString();
                    }
                }
            }

            return !string.IsNullOrEmpty(_authToken);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"API login error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  DATA MODEL
    // ══════════════════════════════════════════════════════════

    public class BankTransaction : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Description { get; set; } = "";
        public decimal Credit { get; set; }
        public decimal Debit { get; set; }
        public string? CheckNumber { get; set; }

        private string _category = "Other";
        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(nameof(Category)); }
        }

        public int StatementMonth { get; set; }
        public int StatementYear { get; set; }
        public string? ReconcileStatus { get; set; }

        /// <summary>True when a check image exists in the database for this check number.</summary>
        private bool _hasCheckImage;
        public bool HasCheckImage
        {
            get => _hasCheckImage;
            set { _hasCheckImage = value; OnPropertyChanged(nameof(HasCheckImage)); OnPropertyChanged(nameof(CheckImageLabel)); OnPropertyChanged(nameof(CheckColumnVisibility)); }
        }

        /// <summary>Show 📷 if image exists, or 🔍 if it's a check without image. Used by DataGrid button.</summary>
        public string CheckImageLabel => HasCheckImage ? "📷" : "🔍";

        /// <summary>Visibility for the check image/view column — visible only when there's a check number.</summary>
        public Visibility CheckColumnVisibility =>
            !string.IsNullOrWhiteSpace(CheckNumber) ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MonthlySummary
    {
        public string MonthLabel { get; set; } = "";
        public decimal TotalCredits { get; set; }
        public decimal TotalDebits { get; set; }
        public decimal Net => TotalCredits - TotalDebits;
        public int TransactionCount { get; set; }
    }

    // ══════════════════════════════════════════════════════════
    //  P&L CATEGORY ENGINE
    //  Enhanced keyword matching for automatic categorization
    // ══════════════════════════════════════════════════════════

    private static readonly (string Category, string[] Keywords)[] _categoryRules =
    {
        ("Utilities", new[] { "COMCAST", "COMED", "COM ED", "NICOR", "AT&T", "VERIZON", "TMOBILE", "T-MOBILE",
            "ELECTRIC", "GAS BILL", "WATER", "SEWER", "INTERNET", "PHONE", "XFINITY", "UTILITY" }),
        ("Rent", new[] { "RENT", "LEASE", "LANDLORD", "PROPERTY MGMT", "PROPERTY MANAGEMENT" }),
        ("Payroll", new[] { "PAYROLL", "ADP", "GUSTO", "PAYCHEX", "SALARY", "WAGE" }),
        ("Insurance", new[] { "INSURANCE", "STATE FARM", "ALLSTATE", "GEICO", "PROGRESSIVE", "PREMIUM" }),
        ("Inventory/COGS", new[] { "SYSCO", "MCLANE", "CORE-MARK", "WHOLESALE", "TOBACCO", "DISTRIBUTOR", "SUPPLIER" }),
        ("Bank Fees", new[] { "FEE", "SERVICE CHARGE", "OVERDRAFT", "NSF", "MONTHLY MAINTENANCE" }),
        ("Tax Payment", new[] { "TAX", "IRS", "STATE TAX", "IDOR", "IL DEPT OF REVEN", "REVENUE" }),
        ("Loan/Debt", new[] { "LOAN", "MORTGAGE", "INTEREST", "FINANCE CHARGE" }),
        ("Advertising", new[] { "GOOGLE ADS", "FACEBOOK", "YELP", "MARKETING", "ADVERTISING" }),
        ("Equipment", new[] { "EQUIPMENT", "REPAIR", "MAINTENANCE", "HVAC" }),
        ("Revenue/Deposit", new[] { "DEPOSIT", "CASH DEP", "POS", "MERCHANT", "SQUARE", "CLOVER" }),
        ("Transfer", new[] { "TRANSFER", "XFER", "ONLINE TRANSFER" }),
    };

    /// <summary>
    /// Auto-categorize a transaction based on its description and check number.
    /// </summary>
    private static string CategorizeTransaction(string desc, string? checkNum)
    {
        if (!string.IsNullOrWhiteSpace(checkNum)) return "Check";

        var d = desc.ToUpperInvariant();

        // Check specific compound patterns first (before single-keyword rules)
        if (d.Contains("MERCHANT") && (d.Contains("DEP") || d.Contains("CREDIT"))) return "Merchant Deposit";
        if (d.Contains("ACH") && d.Contains("CREDIT")) return "ACH Credit";
        if (d.Contains("ACH") && d.Contains("DEBIT")) return "ACH Debit";
        if (d.Contains("BILL PAY")) return "Bill Pay";
        if (d.Contains("POS") && d.Contains("REFUND")) return "POS Refund";
        if (d.Contains("CREDIT CRD") || d.Contains("CREDIT CARD")) return "Credit Card Payment";
        if (d.Contains("PREAUTHORIZED DEBIT")) return "Pre-Auth Debit";
        if (d.Contains("PREAUTHORIZED CREDIT")) return "Pre-Auth Credit";
        if (d.Contains("WIRE")) return "Wire Transfer";

        // Run through the keyword rules
        foreach (var (category, keywords) in _categoryRules)
        {
            foreach (var kw in keywords)
            {
                if (d.Contains(kw))
                    return category;
            }
        }

        // Remaining POS without refund
        if (d.Contains("POS") || d.Contains("PURCHASE")) return "POS Purchase";

        return "Other";
    }

    // ══════════════════════════════════════════════════════════
    //  CONNECTION HELPERS
    // ══════════════════════════════════════════════════════════

    private string GetConnectionString()
    {
        using var db = _dbFactory!.CreateDbContext();
        return db.Database.GetConnectionString() ?? "";
    }

    private bool IsSqlServer()
    {
        var cs = GetConnectionString();
        return cs.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || cs.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) && !cs.Contains(".db", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════
    //  TABLE CREATION
    // ══════════════════════════════════════════════════════════

    private async Task EnsureTableAsync()
    {
        try
        {
            var cs = GetConnectionString();
            if (IsSqlServer())
            {
                using var conn = new SqlConnection(cs);
                await conn.OpenAsync();

                // Main transactions table
                var sql = @"IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BankStatementTransactions')
CREATE TABLE [dbo].[BankStatementTransactions] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATE NOT NULL,
    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
    [Credit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Debit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [CheckNumber] NVARCHAR(20) NULL,
    [Category] NVARCHAR(50) NOT NULL DEFAULT 'Other',
    [StatementMonth] INT NOT NULL,
    [StatementYear] INT NOT NULL,
    [ImportedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [CreatedByName] NVARCHAR(100) NOT NULL DEFAULT ''
);";
                using var cmd = new SqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                // Check images table
                var sqlImg = @"IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'BankStatementCheckImages')
CREATE TABLE [dbo].[BankStatementCheckImages] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL,
    [CheckNumber] NVARCHAR(20) NOT NULL,
    [ImageData] VARBINARY(MAX) NOT NULL,
    [ImageFormat] NVARCHAR(10) NOT NULL DEFAULT 'png',
    [StatementMonth] INT NOT NULL,
    [StatementYear] INT NOT NULL,
    [ImportedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);";
                using var cmdImg = new SqlCommand(sqlImg, conn);
                await cmdImg.ExecuteNonQueryAsync();
            }
            else
            {
                using var conn = new SqliteConnection(cs);
                await conn.OpenAsync();

                var sql = @"CREATE TABLE IF NOT EXISTS BankStatementTransactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL DEFAULT 1,
    Date TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '',
    Credit REAL NOT NULL DEFAULT 0,
    Debit REAL NOT NULL DEFAULT 0,
    CheckNumber TEXT,
    Category TEXT NOT NULL DEFAULT 'Other',
    StatementMonth INTEGER NOT NULL,
    StatementYear INTEGER NOT NULL,
    ImportedUtc TEXT NOT NULL DEFAULT (datetime('now')),
    CreatedByName TEXT NOT NULL DEFAULT ''
);";
                using var cmd = new SqliteCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                var sqlImg = @"CREATE TABLE IF NOT EXISTS BankStatementCheckImages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StoreId INTEGER NOT NULL,
    CheckNumber TEXT NOT NULL,
    ImageData BLOB NOT NULL,
    ImageFormat TEXT NOT NULL DEFAULT 'png',
    StatementMonth INTEGER NOT NULL,
    StatementYear INTEGER NOT NULL,
    ImportedUtc TEXT NOT NULL DEFAULT (datetime('now'))
);";
                using var cmdImg = new SqliteCommand(sqlImg, conn);
                await cmdImg.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnsureTable error: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  LOAD DATA
    // ══════════════════════════════════════════════════════════

    private async Task LoadDataAsync()
    {
        if (!_initialized || _loading) return;
        _loading = true;
        try
        {
            var month = cmbMonth?.SelectedValue is int m ? m : DateTime.Today.Month;
            var year = cmbYear?.SelectedItem is int y ? y : DateTime.Today.Year;

            var transactions = await GetTransactionsAsync(month, year);

            // Check which transactions have images
            var checkNums = transactions
                .Where(t => !string.IsNullOrWhiteSpace(t.CheckNumber))
                .Select(t => t.CheckNumber!)
                .Distinct()
                .ToList();
            var hasImageSet = await GetCheckNumbersWithImagesAsync(checkNums, month, year);
            foreach (var t in transactions)
            {
                if (!string.IsNullOrWhiteSpace(t.CheckNumber))
                    t.HasCheckImage = hasImageSet.Contains(t.CheckNumber!);
            }

            gridTransactions.ItemsSource = transactions;

            var checks = transactions.Where(t => !string.IsNullOrWhiteSpace(t.CheckNumber)).ToList();
            foreach (var c in checks)
                c.ReconcileStatus = await IsCheckMatchedAsync(c.CheckNumber!) ? "✅ Matched" : "❌ Unmatched";
            gridCheckRecon.ItemsSource = checks;

            var summary = await GetMonthlySummaryAsync();
            gridSummary.ItemsSource = summary;

            var totalCredits = transactions.Sum(t => t.Credit);
            var totalDebits = transactions.Sum(t => t.Debit);
            lblCredits.Text = totalCredits.ToString("C2");
            lblDebits.Text = totalDebits.ToString("C2");
            lblNet.Text = (totalCredits - totalDebits).ToString("C2");
            lblCount.Text = transactions.Count.ToString();
        }
        catch (Exception ex)
        {
            lblStatus.Text = $"Error loading: {ex.Message}";
        }
        finally { _loading = false; }
    }

    // ══════════════════════════════════════════════════════════
    //  DB READS
    // ══════════════════════════════════════════════════════════

    private async Task<List<BankTransaction>> GetTransactionsAsync(int month, int year)
    {
        var list = new List<BankTransaction>();
        var cs = GetConnectionString();

        if (IsSqlServer())
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, [Date], [Description], Credit, Debit, CheckNumber, Category, StatementMonth, StatementYear FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y ORDER BY [Date]", conn);
            cmd.Parameters.AddWithValue("@sid", _storeId);
            cmd.Parameters.AddWithValue("@m", month);
            cmd.Parameters.AddWithValue("@y", year);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(ReadTransaction(r));
        }
        else
        {
            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqliteCommand("SELECT Id, Date, Description, Credit, Debit, CheckNumber, Category, StatementMonth, StatementYear FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y ORDER BY Date", conn);
            cmd.Parameters.AddWithValue("@sid", _storeId);
            cmd.Parameters.AddWithValue("@m", month);
            cmd.Parameters.AddWithValue("@y", year);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(ReadTransaction(r));
        }
        return list;
    }

    private static BankTransaction ReadTransaction(System.Data.Common.DbDataReader r)
    {
        return new BankTransaction
        {
            Id = r.GetInt32(0),
            Date = DateTime.TryParse(r[1]?.ToString(), out var d) ? d : DateTime.MinValue,
            Description = r.GetString(2),
            Credit = Convert.ToDecimal(r[3]),
            Debit = Convert.ToDecimal(r[4]),
            CheckNumber = r.IsDBNull(5) ? null : r.GetString(5),
            Category = r.GetString(6),
            StatementMonth = r.GetInt32(7),
            StatementYear = r.GetInt32(8)
        };
    }

    private async Task<bool> IsCheckMatchedAsync(string checkNumber)
    {
        try
        {
            using var db = _dbFactory!.CreateDbContext();
            return await db.CheckPayouts.AsNoTracking()
                .AnyAsync(c => c.StoreId == _storeId && c.CheckNumber == checkNumber);
        }
        catch { return false; }
    }

    private async Task<List<MonthlySummary>> GetMonthlySummaryAsync()
    {
        var list = new List<MonthlySummary>();
        var cs = GetConnectionString();
        string sql = "SELECT StatementMonth, StatementYear, SUM(Credit), SUM(Debit), COUNT(*) FROM BankStatementTransactions WHERE StoreId=@sid GROUP BY StatementYear, StatementMonth ORDER BY StatementYear DESC, StatementMonth DESC";

        if (IsSqlServer())
        {
            using var conn = new SqlConnection(cs); await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", _storeId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MonthlySummary { MonthLabel = $"{new DateTime(r.GetInt32(1), r.GetInt32(0), 1):MMMM yyyy}", TotalCredits = Convert.ToDecimal(r[2]), TotalDebits = Convert.ToDecimal(r[3]), TransactionCount = r.GetInt32(4) });
        }
        else
        {
            using var conn = new SqliteConnection(cs); await conn.OpenAsync();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", _storeId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MonthlySummary { MonthLabel = $"{new DateTime(r.GetInt32(1), r.GetInt32(0), 1):MMMM yyyy}", TotalCredits = Convert.ToDecimal(r[2]), TotalDebits = Convert.ToDecimal(r[3]), TransactionCount = Convert.ToInt32(r[4]) });
        }
        return list;
    }

    // ══════════════════════════════════════════════════════════
    //  CHECK IMAGE DB OPERATIONS
    // ══════════════════════════════════════════════════════════

    /// <summary>Returns the set of check numbers that have images stored for the given month/year.</summary>
    private async Task<HashSet<string>> GetCheckNumbersWithImagesAsync(List<string> checkNumbers, int month, int year)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (checkNumbers.Count == 0) return result;

        try
        {
            var cs = GetConnectionString();
            if (IsSqlServer())
            {
                using var conn = new SqlConnection(cs); await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DISTINCT CheckNumber FROM BankStatementCheckImages WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) result.Add(r.GetString(0));
            }
            else
            {
                using var conn = new SqliteConnection(cs); await conn.OpenAsync();
                using var cmd = new SqliteCommand("SELECT DISTINCT CheckNumber FROM BankStatementCheckImages WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) result.Add(r.GetString(0));
            }
        }
        catch { /* table may not exist yet */ }
        return result;
    }

    /// <summary>Retrieves the check image bytes from the database.</summary>
    private async Task<byte[]?> GetCheckImageAsync(string checkNumber, int month, int year)
    {
        try
        {
            var cs = GetConnectionString();
            if (IsSqlServer())
            {
                using var conn = new SqlConnection(cs); await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 ImageData FROM BankStatementCheckImages WHERE StoreId=@sid AND CheckNumber=@chk AND StatementMonth=@m AND StatementYear=@y", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@chk", checkNumber);
                cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                var result = await cmd.ExecuteScalarAsync();
                return result as byte[];
            }
            else
            {
                using var conn = new SqliteConnection(cs); await conn.OpenAsync();
                using var cmd = new SqliteCommand("SELECT ImageData FROM BankStatementCheckImages WHERE StoreId=@sid AND CheckNumber=@chk AND StatementMonth=@m AND StatementYear=@y LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@chk", checkNumber);
                cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                var result = await cmd.ExecuteScalarAsync();
                return result as byte[];
            }
        }
        catch { return null; }
    }

    /// <summary>Saves a check image to the database.</summary>
    private async Task SaveCheckImageAsync(string checkNumber, byte[] imageData, string format, int month, int year)
    {
        var cs = GetConnectionString();
        if (IsSqlServer())
        {
            using var conn = new SqlConnection(cs); await conn.OpenAsync();
            // Upsert: delete existing then insert
            using var del = new SqlCommand("DELETE FROM BankStatementCheckImages WHERE StoreId=@sid AND CheckNumber=@chk AND StatementMonth=@m AND StatementYear=@y", conn);
            del.Parameters.AddWithValue("@sid", _storeId); del.Parameters.AddWithValue("@chk", checkNumber);
            del.Parameters.AddWithValue("@m", month); del.Parameters.AddWithValue("@y", year);
            await del.ExecuteNonQueryAsync();

            using var cmd = new SqlCommand("INSERT INTO BankStatementCheckImages (StoreId, CheckNumber, ImageData, ImageFormat, StatementMonth, StatementYear) VALUES (@sid, @chk, @img, @fmt, @m, @y)", conn);
            cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@chk", checkNumber);
            cmd.Parameters.Add("@img", SqlDbType.VarBinary, imageData.Length).Value = imageData;
            cmd.Parameters.AddWithValue("@fmt", format);
            cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            using var conn = new SqliteConnection(cs); await conn.OpenAsync();
            using var del = new SqliteCommand("DELETE FROM BankStatementCheckImages WHERE StoreId=@sid AND CheckNumber=@chk AND StatementMonth=@m AND StatementYear=@y", conn);
            del.Parameters.AddWithValue("@sid", _storeId); del.Parameters.AddWithValue("@chk", checkNumber);
            del.Parameters.AddWithValue("@m", month); del.Parameters.AddWithValue("@y", year);
            await del.ExecuteNonQueryAsync();

            using var cmd = new SqliteCommand("INSERT INTO BankStatementCheckImages (StoreId, CheckNumber, ImageData, ImageFormat, StatementMonth, StatementYear) VALUES (@sid, @chk, @img, @fmt, @m, @y)", conn);
            cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@chk", checkNumber);
            cmd.Parameters.AddWithValue("@img", imageData);
            cmd.Parameters.AddWithValue("@fmt", format);
            cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  SAVE / RE-CATEGORIZE
    // ══════════════════════════════════════════════════════════

    /// <summary>Saves current category values for all displayed transactions back to the database.</summary>
    private async Task SaveAllCategoriesAsync()
    {
        var transactions = gridTransactions.ItemsSource as List<BankTransaction>;
        if (transactions == null || transactions.Count == 0) return;

        var cs = GetConnectionString();
        if (IsSqlServer())
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            foreach (var tx in transactions)
            {
                using var cmd = new SqlCommand("UPDATE BankStatementTransactions SET Category=@cat WHERE Id=@id AND StoreId=@sid", conn);
                cmd.Parameters.AddWithValue("@cat", tx.Category);
                cmd.Parameters.AddWithValue("@id", tx.Id);
                cmd.Parameters.AddWithValue("@sid", _storeId);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        else
        {
            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            foreach (var tx in transactions)
            {
                using var cmd = new SqliteCommand("UPDATE BankStatementTransactions SET Category=@cat WHERE Id=@id AND StoreId=@sid", conn);
                cmd.Parameters.AddWithValue("@cat", tx.Category);
                cmd.Parameters.AddWithValue("@id", tx.Id);
                cmd.Parameters.AddWithValue("@sid", _storeId);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  EXCEL PARSER — St. Charles Bank Format
    //  Layout varies per page:
    //    Page 1 Debits: Date=D(4), Desc=G(7), Amount=V(22)
    //    Page 2 Debits: Date=B(2), Desc=G(7), Amount=Y(25)
    //    Page 1 Credits: Date=D(4), Desc=G(7), Amount=V(22)
    //    Page 2 Credits: Date=D(4), Desc=G(7), Amount=Y(25)
    //  Multi-line cells: "Jan 09\nJan 14" with "-$212.47\n-$10,928.51"
    //  Continuation rows: next row has desc in G but no date
    //  Section breaks: "BALANCEYOURACCOUNT" interrupts debits
    // ══════════════════════════════════════════════════════════

    private List<BankTransaction> ParseExcel(string filePath, int month, int year)
    {
        var transactions = new List<BankTransaction>();
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.First();

        string currentSection = ""; // "Debits" or "Credits"
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

        // Track which rows are continuation rows (already consumed) so we don't double-process
        var skipRows = new HashSet<int>();

        for (int row = 1; row <= lastRow; row++)
        {
            if (skipRows.Contains(row)) continue;

            try
            {
                var cellA = GetCellText(ws, row, 1);  // A
                var cellB = GetCellText(ws, row, 2);  // B
                var cellD = GetCellText(ws, row, 4);  // D
                var cellG = GetCellText(ws, row, 7);  // G

                // ── Detect section headers ──
                var allRowText = (cellA + " " + cellB + " " + cellD + " " + cellG).Trim().ToUpperInvariant();

                // End sections
                if (allRowText.Contains("DAILY BALANCE") || allRowText.Contains("CHECK IMAGES"))
                {
                    currentSection = "";
                    continue;
                }

                // "BALANCEYOURACCOUNT" is a mid-page interruption
                if (cellA.Replace(" ", "").ToUpperInvariant().StartsWith("BALANCEYOUR") ||
                    cellA.ToUpperInvariant().Contains("IMPORTANT INFO"))
                {
                    currentSection = "";
                    continue;
                }

                // Start/resume sections
                if (cellD.Trim().ToUpperInvariant().StartsWith("DEBIT") ||
                    cellA.Trim().ToUpperInvariant().StartsWith("DEBIT"))
                {
                    currentSection = "Debits";
                    continue;
                }
                if (cellD.Trim().ToUpperInvariant().StartsWith("CREDIT") ||
                    cellA.Trim().ToUpperInvariant().StartsWith("CREDIT"))
                {
                    currentSection = "Credits";
                    continue;
                }

                // Balance Summary is not a transaction section
                if (cellD.Trim().ToUpperInvariant().StartsWith("BALANCE SUMMARY") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("BEGINNING BALANCE") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ENDING BALANCE") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ENTREPRENEUR") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("NUMBER OF DAYS") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ANALYSIS OR"))
                    continue;

                // Skip header rows
                if (cellD == "Date" || cellB == "Date") continue;

                // Skip if not in a valid section
                if (currentSection != "Debits" && currentSection != "Credits") continue;

                // ── Get date from D or B ──
                var dateStr = !string.IsNullOrWhiteSpace(cellD) ? cellD : cellB;
                if (string.IsNullOrWhiteSpace(dateStr)) continue;

                // Skip if dateStr looks like garbage
                if (dateStr.Length > 50) continue;

                // ── Get amount from columns V(22) and Y(25) ──
                var amountV = GetCellText(ws, row, 22);
                var amountY = GetCellText(ws, row, 25);

                string amountStr;
                if (!string.IsNullOrWhiteSpace(amountY) && !amountY.Equals("Additions", StringComparison.OrdinalIgnoreCase)
                    && !amountY.Equals("Subtractions", StringComparison.OrdinalIgnoreCase))
                    amountStr = amountY;
                else if (!string.IsNullOrWhiteSpace(amountV) && !amountV.Equals("Additions", StringComparison.OrdinalIgnoreCase)
                    && !amountV.Equals("Subtractions", StringComparison.OrdinalIgnoreCase))
                    amountStr = amountV;
                else
                    continue;

                var description = cellG;

                // ── Collect continuation descriptions from subsequent rows ──
                var continuationDesc = new List<string>();
                for (int nextRow = row + 1; nextRow <= lastRow; nextRow++)
                {
                    var nA = GetCellText(ws, nextRow, 1);
                    var nB = GetCellText(ws, nextRow, 2);
                    var nD = GetCellText(ws, nextRow, 4);
                    var nG = GetCellText(ws, nextRow, 7);

                    var nAllText = (nA + " " + nD).Trim().ToUpperInvariant();
                    if (nAllText.Contains("DEBIT") || nAllText.Contains("CREDIT") ||
                        nAllText.Contains("DAILY") || nAllText.Contains("CHECK IMAGES") ||
                        nAllText.Contains("BALANCE"))
                        break;

                    var nDate = !string.IsNullOrWhiteSpace(nD) ? nD : nB;

                    bool hasDate = !string.IsNullOrWhiteSpace(nDate) && nDate.Length < 20 && TryParseStatementDate(nDate.Split('\n')[0].Trim(), year, out _);
                    bool hasNoAmount = string.IsNullOrWhiteSpace(GetCellText(ws, nextRow, 22)) && string.IsNullOrWhiteSpace(GetCellText(ws, nextRow, 25));

                    if (!hasDate && !string.IsNullOrWhiteSpace(nG) && hasNoAmount)
                    {
                        continuationDesc.Add(nG.Trim());
                        skipRows.Add(nextRow);
                    }
                    else
                    {
                        break;
                    }
                }

                // ── Handle multi-line cells ──
                var dateLines = SplitLines(dateStr);
                var descLines = SplitLines(description);
                var amountLines = SplitLines(amountStr);

                int lineCount = Math.Max(dateLines.Length, amountLines.Length);
                if (lineCount < 1) lineCount = 1;

                for (int li = 0; li < lineCount; li++)
                {
                    var lineDateStr = li < dateLines.Length ? dateLines[li].Trim() : "";
                    var lineAmountStr = li < amountLines.Length ? amountLines[li].Trim() : "";
                    var lineDescStr = li < descLines.Length ? descLines[li].Trim() : (descLines.Length > 0 ? descLines[0].Trim() : "");

                    if (!TryParseStatementDate(lineDateStr, year, out var txDate)) continue;

                    var cleanAmount = lineAmountStr.Replace("$", "").Replace(",", "").Replace("(", "-").Replace(")", "").Trim();
                    if (!decimal.TryParse(cleanAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) continue;
                    amount = Math.Abs(amount);
                    if (amount == 0) continue;

                    var fullDesc = lineDescStr;
                    if (lineCount == 1 && continuationDesc.Count > 0)
                        fullDesc = (fullDesc + " " + string.Join(" ", continuationDesc)).Trim();
                    else if (li < continuationDesc.Count)
                        fullDesc = (fullDesc + " " + continuationDesc[li]).Trim();

                    string? checkNum = null;
                    var checkMatch = Regex.Match(fullDesc, @"(?:CHECK|CHK)\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
                    if (checkMatch.Success) checkNum = checkMatch.Groups[1].Value;

                    var tx = new BankTransaction
                    {
                        Date = txDate,
                        Description = fullDesc.Trim(),
                        Credit = currentSection == "Credits" ? amount : 0,
                        Debit = currentSection == "Debits" ? amount : 0,
                        CheckNumber = checkNum,
                        Category = CategorizeTransaction(fullDesc, checkNum),
                        StatementMonth = month,
                        StatementYear = year
                    };
                    transactions.Add(tx);
                }
            }
            catch { /* skip unparseable rows */ }
        }

        return transactions;
    }

    /// <summary>
    /// Extract embedded images from the Excel worksheet and attempt to match them to check numbers.
    /// Returns a dictionary mapping check numbers to image byte arrays.
    /// </summary>
    private Dictionary<string, (byte[] Data, string Format)> ExtractCheckImages(IXLWorksheet ws, List<BankTransaction> transactions)
    {
        var result = new Dictionary<string, (byte[] Data, string Format)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var pictures = ws.Pictures.ToList();
            if (pictures.Count == 0) return result;

            var allCheckNums = transactions
                .Where(t => !string.IsNullOrWhiteSpace(t.CheckNumber))
                .Select(t => t.CheckNumber!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allCheckNums.Count == 0) return result;

            foreach (var pic in pictures)
            {
                try
                {
                    var topRow = pic.TopLeftCell.Address.RowNumber;

                    // Search nearby rows (±10) for a check number reference
                    string? matchedCheck = null;
                    for (int searchRow = Math.Max(1, topRow - 5); searchRow <= topRow + 10; searchRow++)
                    {
                        for (int col = 1; col <= 10; col++)
                        {
                            var cellText = GetCellText(ws, searchRow, col);
                            if (string.IsNullOrWhiteSpace(cellText)) continue;

                            // Look for check number patterns
                            var m = Regex.Match(cellText, @"(?:CHECK|CHK|#)\s*#?\s*(\d{3,})", RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                var num = m.Groups[1].Value;
                                if (allCheckNums.Contains(num))
                                {
                                    matchedCheck = num;
                                    break;
                                }
                            }

                            // Also check if the cell itself is just a number matching a check
                            var trimmed = cellText.Trim();
                            if (allCheckNums.Contains(trimmed))
                            {
                                matchedCheck = trimmed;
                                break;
                            }
                        }
                        if (matchedCheck != null) break;
                    }

                    if (matchedCheck != null && !result.ContainsKey(matchedCheck))
                    {
                        // ClosedXML exposes image data via ImageStream (MemoryStream)
                        byte[]? bytes = null;
                        try
                        {
                            // Try the ImageStream property (ClosedXML 0.97+)
                            var stream = pic.ImageStream;
                            if (stream != null && stream.CanRead)
                            {
                                if (stream.CanSeek) stream.Position = 0;
                                using var ms = new MemoryStream();
                                stream.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                        }
                        catch
                        {
                            // Fallback: try to get raw bytes from the underlying picture data
                            try
                            {
                                // Some ClosedXML versions use a different approach
                                using var ms = new MemoryStream();
                                pic.ImageStream.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                            catch { /* image extraction not supported in this ClosedXML version */ }
                        }

                        if (bytes != null && bytes.Length > 100) // sanity check — real images are > 100 bytes
                        {
                            var format = "png";
                            // Detect format from magic bytes
                            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                                format = "jpg";
                            else if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50)
                                format = "png";
                            else if (bytes.Length > 4 && bytes[0] == 0x47 && bytes[1] == 0x49)
                                format = "gif";

                            result[matchedCheck] = (bytes, format);
                        }
                    }
                }
                catch { /* skip individual image errors */ }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image extraction error: {ex.Message}");
        }

        return result;
    }

    private static string GetCellText(IXLWorksheet ws, int row, int col)
    {
        try
        {
            var cell = ws.Cell(row, col);
            if (cell == null) return "";

            if (cell.IsMerged())
            {
                var merged = cell.MergedRange();
                if (merged != null)
                {
                    var first = merged.FirstCell();
                    return first?.GetFormattedString()?.Trim() ?? "";
                }
            }
            return cell.GetFormattedString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    private static bool TryParseStatementDate(string dateStr, int statementYear, out DateTime result)
    {
        result = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(dateStr)) return false;

        var clean = dateStr.Trim();

        if (DateTime.TryParse($"{clean}, {statementYear}", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        if (DateTime.TryParse($"{clean} {statementYear}", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;
        if (DateTime.TryParse(clean, out result))
            return true;

        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            var monthNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                {"Jan",1},{"Feb",2},{"Mar",3},{"Apr",4},{"May",5},{"Jun",6},
                {"Jul",7},{"Aug",8},{"Sep",9},{"Oct",10},{"Nov",11},{"Dec",12}
            };
            if (monthNames.TryGetValue(parts[0], out var mo) && int.TryParse(parts[1], out var day))
            {
                try { result = new DateTime(statementYear, mo, day); return true; }
                catch { }
            }
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════
    //  IMPORT
    // ══════════════════════════════════════════════════════════

    private async Task ImportTransactionsAsync(List<BankTransaction> transactions, bool replace)
    {
        var cs = GetConnectionString();
        var month = transactions.FirstOrDefault()?.StatementMonth ?? 1;
        var year = transactions.FirstOrDefault()?.StatementYear ?? DateTime.Today.Year;

        if (IsSqlServer())
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            if (replace)
            {
                using var del = new SqlCommand("DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                del.Parameters.AddWithValue("@sid", _storeId); del.Parameters.AddWithValue("@m", month); del.Parameters.AddWithValue("@y", year);
                await del.ExecuteNonQueryAsync();
            }
            foreach (var tx in transactions)
            {
                using var cmd = new SqlCommand("INSERT INTO BankStatementTransactions (StoreId, [Date], [Description], Credit, Debit, CheckNumber, Category, StatementMonth, StatementYear) VALUES (@sid, @d, @desc, @cr, @dr, @chk, @cat, @m, @y)", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@d", tx.Date);
                cmd.Parameters.AddWithValue("@desc", tx.Description); cmd.Parameters.AddWithValue("@cr", tx.Credit);
                cmd.Parameters.AddWithValue("@dr", tx.Debit); cmd.Parameters.AddWithValue("@chk", (object?)tx.CheckNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", tx.Category); cmd.Parameters.AddWithValue("@m", tx.StatementMonth); cmd.Parameters.AddWithValue("@y", tx.StatementYear);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        else
        {
            using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            if (replace)
            {
                using var del = new SqliteCommand("DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                del.Parameters.AddWithValue("@sid", _storeId); del.Parameters.AddWithValue("@m", month); del.Parameters.AddWithValue("@y", year);
                await del.ExecuteNonQueryAsync();
            }
            foreach (var tx in transactions)
            {
                using var cmd = new SqliteCommand("INSERT INTO BankStatementTransactions (StoreId, Date, Description, Credit, Debit, CheckNumber, Category, StatementMonth, StatementYear) VALUES (@sid, @d, @desc, @cr, @dr, @chk, @cat, @m, @y)", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@d", tx.Date.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@desc", tx.Description); cmd.Parameters.AddWithValue("@cr", tx.Credit);
                cmd.Parameters.AddWithValue("@dr", tx.Debit); cmd.Parameters.AddWithValue("@chk", (object?)tx.CheckNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", tx.Category); cmd.Parameters.AddWithValue("@m", tx.StatementMonth); cmd.Parameters.AddWithValue("@y", tx.StatementYear);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  EVENT HANDLERS
    // ══════════════════════════════════════════════════════════

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Bank Statement (Excel)",
                Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            var month = cmbMonth?.SelectedValue is int m ? m : DateTime.Today.Month;
            var year = cmbYear?.SelectedItem is int y ? y : DateTime.Today.Year;

            lblStatus.Text = "Parsing statement...";

            List<BankTransaction> transactions = null!;
            Dictionary<string, (byte[] Data, string Format)> checkImages = null!;

            await Task.Run(() =>
            {
                using var wb = new XLWorkbook(dlg.FileName);
                var ws = wb.Worksheets.First();
                transactions = ParseExcelFromWorksheet(ws, month, year);
                checkImages = ExtractCheckImages(ws, transactions);
            });

            if (transactions.Count == 0)
            {
                lblStatus.Text = "No transactions found in the file. Check that the file is a St. Charles Bank statement.";
                return;
            }

            var credits = transactions.Sum(t => t.Credit);
            var debits = transactions.Sum(t => t.Debit);
            var imgCount = checkImages.Count;

            var msg = $"Found {transactions.Count} transactions:\n\n" +
                      $"Credits: {credits:C2} ({transactions.Count(t => t.Credit > 0)} items)\n" +
                      $"Debits: {debits:C2} ({transactions.Count(t => t.Debit > 0)} items)\n" +
                      (imgCount > 0 ? $"Check Images: {imgCount} found\n" : "") +
                      "\nImport these transactions? (Replaces existing data for this month)";

            if (WpfMessageBox.Show(msg, "Import Bank Statement", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                lblStatus.Text = "Import cancelled.";
                return;
            }

            lblStatus.Text = "Importing...";
            await ImportTransactionsAsync(transactions, replace: true);

            // Save check images
            if (checkImages.Count > 0)
            {
                lblStatus.Text = $"Saving {checkImages.Count} check images...";
                foreach (var (checkNum, (data, format)) in checkImages)
                {
                    await SaveCheckImageAsync(checkNum, data, format, month, year);
                }
            }

            await LoadDataAsync();
            lblStatus.Text = $"✅ Imported {transactions.Count} transactions" +
                             (imgCount > 0 ? $" + {imgCount} check images" : "") +
                             " successfully.";
        }
        catch (Exception ex) { lblStatus.Text = $"Error: {ex.Message}"; }
    }

    /// <summary>Parse from an already-opened worksheet (so we can also extract images from the same workbook).</summary>
    private List<BankTransaction> ParseExcelFromWorksheet(IXLWorksheet ws, int month, int year)
    {
        // This delegates to the same logic as ParseExcel but takes a worksheet directly
        var transactions = new List<BankTransaction>();
        string currentSection = "";
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var skipRows = new HashSet<int>();

        for (int row = 1; row <= lastRow; row++)
        {
            if (skipRows.Contains(row)) continue;

            try
            {
                var cellA = GetCellText(ws, row, 1);
                var cellB = GetCellText(ws, row, 2);
                var cellD = GetCellText(ws, row, 4);
                var cellG = GetCellText(ws, row, 7);

                var allRowText = (cellA + " " + cellB + " " + cellD + " " + cellG).Trim().ToUpperInvariant();

                if (allRowText.Contains("DAILY BALANCE") || allRowText.Contains("CHECK IMAGES"))
                { currentSection = ""; continue; }

                if (cellA.Replace(" ", "").ToUpperInvariant().StartsWith("BALANCEYOUR") ||
                    cellA.ToUpperInvariant().Contains("IMPORTANT INFO"))
                { currentSection = ""; continue; }

                if (cellD.Trim().ToUpperInvariant().StartsWith("DEBIT") ||
                    cellA.Trim().ToUpperInvariant().StartsWith("DEBIT"))
                { currentSection = "Debits"; continue; }

                if (cellD.Trim().ToUpperInvariant().StartsWith("CREDIT") ||
                    cellA.Trim().ToUpperInvariant().StartsWith("CREDIT"))
                { currentSection = "Credits"; continue; }

                if (cellD.Trim().ToUpperInvariant().StartsWith("BALANCE SUMMARY") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("BEGINNING BALANCE") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ENDING BALANCE") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ENTREPRENEUR") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("NUMBER OF DAYS") ||
                    cellD.Trim().ToUpperInvariant().StartsWith("ANALYSIS OR"))
                    continue;

                if (cellD == "Date" || cellB == "Date") continue;
                if (currentSection != "Debits" && currentSection != "Credits") continue;

                var dateStr = !string.IsNullOrWhiteSpace(cellD) ? cellD : cellB;
                if (string.IsNullOrWhiteSpace(dateStr)) continue;
                if (dateStr.Length > 50) continue;

                var amountV = GetCellText(ws, row, 22);
                var amountY = GetCellText(ws, row, 25);

                string amountStr;
                if (!string.IsNullOrWhiteSpace(amountY) && !amountY.Equals("Additions", StringComparison.OrdinalIgnoreCase)
                    && !amountY.Equals("Subtractions", StringComparison.OrdinalIgnoreCase))
                    amountStr = amountY;
                else if (!string.IsNullOrWhiteSpace(amountV) && !amountV.Equals("Additions", StringComparison.OrdinalIgnoreCase)
                    && !amountV.Equals("Subtractions", StringComparison.OrdinalIgnoreCase))
                    amountStr = amountV;
                else
                    continue;

                var description = cellG;

                var continuationDesc = new List<string>();
                for (int nextRow = row + 1; nextRow <= lastRow; nextRow++)
                {
                    var nA = GetCellText(ws, nextRow, 1);
                    var nD = GetCellText(ws, nextRow, 4);
                    var nB = GetCellText(ws, nextRow, 2);
                    var nG = GetCellText(ws, nextRow, 7);

                    var nAllText = (nA + " " + nD).Trim().ToUpperInvariant();
                    if (nAllText.Contains("DEBIT") || nAllText.Contains("CREDIT") ||
                        nAllText.Contains("DAILY") || nAllText.Contains("CHECK IMAGES") ||
                        nAllText.Contains("BALANCE"))
                        break;

                    var nDate = !string.IsNullOrWhiteSpace(nD) ? nD : nB;
                    bool hasDate = !string.IsNullOrWhiteSpace(nDate) && nDate.Length < 20 && TryParseStatementDate(nDate.Split('\n')[0].Trim(), year, out _);
                    bool hasNoAmount = string.IsNullOrWhiteSpace(GetCellText(ws, nextRow, 22)) && string.IsNullOrWhiteSpace(GetCellText(ws, nextRow, 25));

                    if (!hasDate && !string.IsNullOrWhiteSpace(nG) && hasNoAmount)
                    { continuationDesc.Add(nG.Trim()); skipRows.Add(nextRow); }
                    else break;
                }

                var dateLines = SplitLines(dateStr);
                var descLines = SplitLines(description);
                var amountLines = SplitLines(amountStr);

                int lineCount = Math.Max(dateLines.Length, amountLines.Length);
                if (lineCount < 1) lineCount = 1;

                for (int li = 0; li < lineCount; li++)
                {
                    var lineDateStr = li < dateLines.Length ? dateLines[li].Trim() : "";
                    var lineAmountStr = li < amountLines.Length ? amountLines[li].Trim() : "";
                    var lineDescStr = li < descLines.Length ? descLines[li].Trim() : (descLines.Length > 0 ? descLines[0].Trim() : "");

                    if (!TryParseStatementDate(lineDateStr, year, out var txDate)) continue;

                    var cleanAmount = lineAmountStr.Replace("$", "").Replace(",", "").Replace("(", "-").Replace(")", "").Trim();
                    if (!decimal.TryParse(cleanAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) continue;
                    amount = Math.Abs(amount);
                    if (amount == 0) continue;

                    var fullDesc = lineDescStr;
                    if (lineCount == 1 && continuationDesc.Count > 0)
                        fullDesc = (fullDesc + " " + string.Join(" ", continuationDesc)).Trim();
                    else if (li < continuationDesc.Count)
                        fullDesc = (fullDesc + " " + continuationDesc[li]).Trim();

                    string? checkNum = null;
                    var checkMatch = Regex.Match(fullDesc, @"(?:CHECK|CHK)\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
                    if (checkMatch.Success) checkNum = checkMatch.Groups[1].Value;

                    transactions.Add(new BankTransaction
                    {
                        Date = txDate,
                        Description = fullDesc.Trim(),
                        Credit = currentSection == "Credits" ? amount : 0,
                        Debit = currentSection == "Debits" ? amount : 0,
                        CheckNumber = checkNum,
                        Category = CategorizeTransaction(fullDesc, checkNum),
                        StatementMonth = month,
                        StatementYear = year
                    });
                }
            }
            catch { }
        }

        return transactions;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDataAsync();

    public void StartStatementImportFromHub() => Upload_Click(this, new RoutedEventArgs());

    public void RefreshStatementFromHub() => Refresh_Click(this, new RoutedEventArgs());

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var mainWin = Window.GetWindow(this);
        if (mainWin != null)
        {
            var tabControl = mainWin.FindName("tabsMain") as System.Windows.Controls.TabControl;
            if (tabControl != null) tabControl.SelectedIndex = 0;
        }
    }

    private async void Period_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_loading) await LoadDataAsync();
    }

    private async void DeleteMonth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var month = cmbMonth?.SelectedValue is int m ? m : DateTime.Today.Month;
            var year = cmbYear?.SelectedItem is int y ? y : DateTime.Today.Year;
            var monthName = new DateTime(year, month, 1).ToString("MMMM yyyy");

            if (WpfMessageBox.Show($"Delete all bank statement data for {monthName}?", "Delete Month",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var cs = GetConnectionString();
            if (IsSqlServer())
            {
                using var conn = new SqlConnection(cs); await conn.OpenAsync();
                using var cmd = new SqlCommand("DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                await cmd.ExecuteNonQueryAsync();

                // Also delete check images for this month
                try
                {
                    using var cmdImg = new SqlCommand("DELETE FROM BankStatementCheckImages WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                    cmdImg.Parameters.AddWithValue("@sid", _storeId); cmdImg.Parameters.AddWithValue("@m", month); cmdImg.Parameters.AddWithValue("@y", year);
                    await cmdImg.ExecuteNonQueryAsync();
                }
                catch { }
            }
            else
            {
                using var conn = new SqliteConnection(cs); await conn.OpenAsync();
                using var cmd = new SqliteCommand("DELETE FROM BankStatementTransactions WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                cmd.Parameters.AddWithValue("@sid", _storeId); cmd.Parameters.AddWithValue("@m", month); cmd.Parameters.AddWithValue("@y", year);
                await cmd.ExecuteNonQueryAsync();

                try
                {
                    using var cmdImg = new SqliteCommand("DELETE FROM BankStatementCheckImages WHERE StoreId=@sid AND StatementMonth=@m AND StatementYear=@y", conn);
                    cmdImg.Parameters.AddWithValue("@sid", _storeId); cmdImg.Parameters.AddWithValue("@m", month); cmdImg.Parameters.AddWithValue("@y", year);
                    await cmdImg.ExecuteNonQueryAsync();
                }
                catch { }
            }
            await LoadDataAsync();
            lblStatus.Text = $"Deleted data for {monthName}.";
        }
        catch (Exception ex) { lblStatus.Text = $"Error: {ex.Message}"; }
    }

    /// <summary>Re-run auto-categorization on all transactions for the current month.</summary>
    private async void Recategorize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var transactions = gridTransactions.ItemsSource as List<BankTransaction>;
            if (transactions == null || transactions.Count == 0)
            {
                lblStatus.Text = "No transactions to re-categorize.";
                return;
            }

            if (WpfMessageBox.Show($"Re-categorize all {transactions.Count} transactions?\nThis will overwrite any manual category changes.",
                "Re-Categorize", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            int changed = 0;
            foreach (var tx in transactions)
            {
                var newCat = CategorizeTransaction(tx.Description, tx.CheckNumber);
                if (tx.Category != newCat)
                {
                    tx.Category = newCat;
                    changed++;
                }
            }

            gridTransactions.Items.Refresh();

            // Save to DB
            await SaveAllCategoriesAsync();

            lblStatus.Text = $"✅ Re-categorized {changed} of {transactions.Count} transactions.";
        }
        catch (Exception ex) { lblStatus.Text = $"Error: {ex.Message}"; }
    }

    /// <summary>Save any category edits to the database.</summary>
    private async void SaveCategories_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            lblStatus.Text = "Saving categories...";
            await SaveAllCategoriesAsync();
            lblStatus.Text = "✅ Categories saved.";
        }
        catch (Exception ex) { lblStatus.Text = $"Error saving: {ex.Message}"; }
    }

    /// <summary>View check image popup when the camera/search button is clicked.</summary>
    private async void ViewCheckImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.DataContext is not BankTransaction tx) return;
            if (string.IsNullOrWhiteSpace(tx.CheckNumber)) return;

            var month = cmbMonth?.SelectedValue is int m ? m : DateTime.Today.Month;
            var year = cmbYear?.SelectedItem is int y ? y : DateTime.Today.Year;

            var imageData = await GetCheckImageAsync(tx.CheckNumber, month, year);
            if (imageData == null || imageData.Length == 0)
            {
                WpfMessageBox.Show(
                    $"No check image found for Check #{tx.CheckNumber}.\n\n" +
                    "Check images are extracted from the Excel statement during import.\n" +
                    "If the bank statement has a 'Check Images' section with embedded pictures,\n" +
                    "try re-uploading the statement to extract them.",
                    "Check Image Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Create popup window
            var imgWindow = new Window
            {
                Title = $"Check #{tx.CheckNumber} — {tx.Date:MM/dd/yyyy} — {tx.Debit:C2}",
                Width = 850,
                Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF0B0B0F")),
                ResizeMode = ResizeMode.CanResize
            };

            var bi = new BitmapImage();
            using (var ms = new MemoryStream(imageData))
            {
                bi.BeginInit();
                bi.StreamSource = ms;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
            }

            var img = new System.Windows.Controls.Image
            {
                Source = bi,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(10)
            };

            // Add header info and image in a stack
            var header = new TextBlock
            {
                Text = $"Check #{tx.CheckNumber}  •  {tx.Date:MMMM dd, yyyy}  •  {tx.Debit:C2}  •  {tx.Description}",
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD4AF37")),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 10, 10, 5)
            };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(img);

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack
            };

            imgWindow.Content = scroll;
            imgWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PLAID BANK CONNECTION
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Get the API base URL.</summary>
    private string GetApiBaseUrl()
    {
        if (!string.IsNullOrEmpty(_plaidApiBaseUrl)) return _plaidApiBaseUrl;
        _plaidApiBaseUrl = "https://hbstoreledger-api-dwfdg2hygggqhma3.canadacentral-01.azurewebsites.net";
        return _plaidApiBaseUrl;
    }

    /// <summary>Get the JWT auth token for API calls.</summary>
    private string GetAuthToken()
    {
        return _authToken ?? "";
    }

    /// <summary>Make an authenticated API call.</summary>
    private async Task<HttpResponseMessage> ApiCallAsync(HttpMethod method, string endpoint, object? body = null)
    {
        using var client = new HttpClient();
        var url = GetApiBaseUrl().TrimEnd('/') + endpoint;
        var request = new HttpRequestMessage(method, url);
        var token = GetAuthToken();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("Authorization", $"Bearer {token}");

        if (body != null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    /// <summary>Load the current Plaid connection status and update UI.</summary>
    private async Task LoadPlaidConnectionStatusAsync()
    {
        try
        {
            var response = await ApiCallAsync(HttpMethod.Get, "/api/plaid/connections");
            if (!response.IsSuccessStatusCode)
            {
                ShowDisconnectedState();
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var connections = doc.RootElement;

            JsonElement? activeConn = null;
            foreach (var conn in connections.EnumerateArray())
            {
                if (conn.TryGetProperty("isActive", out var isActive) && isActive.GetBoolean())
                {
                    activeConn = conn;
                    break;
                }
            }

            if (activeConn.HasValue)
            {
                var conn = activeConn.Value;
                var instName = conn.GetProperty("institutionName").GetString() ?? "Bank";
                var mask = conn.TryGetProperty("accountMask", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : null;
                var lastSync = conn.TryGetProperty("lastSync", out var ls) && ls.ValueKind != JsonValueKind.Null
                    ? DateTime.Parse(ls.GetString()!).ToLocalTime().ToString("MMM dd, yyyy h:mm tt")
                    : "Never";
                ShowConnectedState(instName, mask, lastSync);
            }
            else
            {
                ShowDisconnectedState();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plaid status check error: {ex.Message}");
            ShowDisconnectedState();
        }
    }

    private void ShowConnectedState(string bankName, string? mask, string lastSync)
    {
        Dispatcher.Invoke(() =>
        {
            txtBankName.Text = string.IsNullOrEmpty(mask) ? bankName : $"{bankName} ****{mask}";
            txtLastSync.Text = lastSync;
            connectedInfo.Visibility = Visibility.Visible;
            disconnectedInfo.Visibility = Visibility.Collapsed;
            connectedButtons.Visibility = Visibility.Visible;
            btnConnectBank.Visibility = Visibility.Collapsed;
        });
    }

    private void ShowDisconnectedState()
    {
        Dispatcher.Invoke(() =>
        {
            connectedInfo.Visibility = Visibility.Collapsed;
            disconnectedInfo.Visibility = Visibility.Visible;
            connectedButtons.Visibility = Visibility.Collapsed;
            btnConnectBank.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Connect Bank Account — opens Plaid Link via WebView2.</summary>
    private async void ConnectBank_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureApiTokenAsync()) return;

            btnConnectBank.IsEnabled = false;
            btnConnectBank.Content = "⏳ Connecting...";

            // Step 1: Get a link token from our API
            var response = await ApiCallAsync(HttpMethod.Post,
                "/api/plaid/create-link-token", new { storeId = 1 });

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                WpfMessageBox.Show($"Failed to initialize bank connection:\n{err}",
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var linkToken = doc.RootElement.GetProperty("linkToken").GetString()!;

            // Step 2: Open Plaid Link in a WebView2 window
            var plaidWindow = new PlaidLinkWindow(linkToken);
            plaidWindow.Owner = Window.GetWindow(this);
            var result = plaidWindow.ShowDialog();

            if (result == true && plaidWindow.Success)
            {
                // Step 3: Exchange the public token via our API
                var exchangeResponse = await ApiCallAsync(HttpMethod.Post,
                    "/api/plaid/exchange-token", new
                    {
                        publicToken = plaidWindow.PublicToken,
                        institutionId = plaidWindow.InstitutionId,
                        institutionName = plaidWindow.InstitutionName,
                        accountId = plaidWindow.AccountId,
                        accountName = plaidWindow.AccountName,
                        accountMask = plaidWindow.AccountMask
                    });

                if (exchangeResponse.IsSuccessStatusCode)
                {
                    WpfMessageBox.Show(
                        $"✅ Bank account connected!\n\n" +
                        $"Bank: {plaidWindow.InstitutionName}\n" +
                        $"Account: {plaidWindow.AccountName} ****{plaidWindow.AccountMask}\n\n" +
                        "Click 'Sync Now' to pull your transactions.",
                        "Bank Connected", MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadPlaidConnectionStatusAsync();
                }
                else
                {
                    var err = await exchangeResponse.Content.ReadAsStringAsync();
                    WpfMessageBox.Show($"Bank login succeeded but token exchange failed:\n{err}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else if (!string.IsNullOrEmpty(plaidWindow.ErrorMessage))
            {
                WpfMessageBox.Show($"Bank connection cancelled:\n{plaidWindow.ErrorMessage}",
                    "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Error connecting bank: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnConnectBank.IsEnabled = true;
            btnConnectBank.Content = "🔗 Connect Bank Account";
        }
    }

    /// <summary>Sync Now — pulls new transactions from Plaid.</summary>
    private async void SyncTransactions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureApiTokenAsync()) return;

            btnSyncNow.IsEnabled = false;
            btnSyncNow.Content = "⏳ Syncing...";

            var response = await ApiCallAsync(HttpMethod.Post,
                "/api/plaid/sync-transactions", new { storeId = 1 });
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var added = root.GetProperty("added").GetInt32();
                var modified = root.GetProperty("modified").GetInt32();
                var removed = root.GetProperty("removed").GetInt32();

                WpfMessageBox.Show(
                    $"✅ Sync complete!\n\n" +
                    $"New transactions: {added}\n" +
                    $"Updated: {modified}\n" +
                    $"Removed: {removed}",
                    "Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                await LoadPlaidConnectionStatusAsync();
                await LoadDataAsync();
            }
            else
            {
                WpfMessageBox.Show($"Sync failed:\n{json}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Sync error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnSyncNow.IsEnabled = true;
            btnSyncNow.Content = "🔄 Sync Now";
        }
    }

    /// <summary>View Balance — shows real-time account balance.</summary>
    private async void ViewBalance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await EnsureApiTokenAsync()) return;

            btnViewBalance.IsEnabled = false;
            btnViewBalance.Content = "⏳ Loading...";

            var response = await ApiCallAsync(HttpMethod.Get, "/api/plaid/balance");
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var current = root.GetProperty("current").GetDecimal();
                var available = root.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.Number
                    ? av.GetDecimal() : (decimal?)null;
                var acctName = root.GetProperty("accountName").GetString();

                var msg = $"💰 Account Balance\n\n" +
                          $"Account: {acctName}\n" +
                          $"Current Balance: {current:C2}\n";
                if (available.HasValue)
                    msg += $"Available Balance: {available:C2}\n";

                WpfMessageBox.Show(msg, "Account Balance", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                WpfMessageBox.Show($"Failed to get balance:\n{json}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Balance error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnViewBalance.IsEnabled = true;
            btnViewBalance.Content = "💰 Balance";
        }
    }

    /// <summary>Disconnect Bank — removes the Plaid connection.</summary>
    private async void DisconnectBank_Click(object sender, RoutedEventArgs e)
    {
        var confirm = WpfMessageBox.Show(
            "Are you sure you want to disconnect your bank account?\n\n" +
            "Transaction history will be preserved, but automatic syncing will stop.",
            "Disconnect Bank", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        if (!await EnsureApiTokenAsync()) return;

        try
        {
            var listResponse = await ApiCallAsync(HttpMethod.Get, "/api/plaid/connections");
            if (!listResponse.IsSuccessStatusCode) return;

            var listJson = await listResponse.Content.ReadAsStringAsync();
            using var listDoc = JsonDocument.Parse(listJson);

            int? connectionId = null;
            foreach (var conn in listDoc.RootElement.EnumerateArray())
            {
                if (conn.TryGetProperty("isActive", out var ia) && ia.GetBoolean())
                {
                    connectionId = conn.GetProperty("connectionId").GetInt32();
                    break;
                }
            }

            if (connectionId == null)
            {
                WpfMessageBox.Show("No active connection found.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var response = await ApiCallAsync(HttpMethod.Post,
                "/api/plaid/disconnect", new { connectionId = connectionId.Value });

            if (response.IsSuccessStatusCode)
            {
                WpfMessageBox.Show("Bank account disconnected.", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowDisconnectedState();
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                WpfMessageBox.Show($"Failed to disconnect:\n{err}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
