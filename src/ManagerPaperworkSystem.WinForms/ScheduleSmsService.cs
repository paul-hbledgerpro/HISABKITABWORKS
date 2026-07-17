using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace ManagerPaperworkSystem.WinForms;

internal static class ScheduleSmsGatewayService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> SendAsync(string endpoint, string username, string password, string phone, string message, CancellationToken ct = default)
    {
        var uri = ValidateEndpoint(endpoint);
        var normalizedPhone = NormalizeUsPhone(phone);
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("The schedule message is empty.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            textMessage = new { text = message },
            phoneNumbers = new[] { normalizedPhone }
        }), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SMS gateway returned {(int)response.StatusCode}: {Short(body)}");
        return Short(body);
    }

    public static string NormalizeUsPhone(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return "+1" + digits;
        if (digits.Length == 11 && digits[0] == '1') return "+" + digits;
        throw new InvalidOperationException($"Phone number '{phone}' must contain a valid 10-digit US number.");
    }

    private static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS Android SMS gateway URL.");
        if (uri.Scheme == "http" && !IsLocalHost(uri.Host))
            throw new InvalidOperationException("Unencrypted HTTP is allowed only for a local/private-network Android gateway. Use HTTPS for any Internet address.");
        return uri;
    }

    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168));
    }

    private static string Short(string value) => string.IsNullOrWhiteSpace(value) ? "Accepted" : value.Length <= 900 ? value : value[..900];
}

internal sealed class ScheduleSmsSettingsForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly TextBox _url = PayrollUi.TextBox();
    private readonly TextBox _username = PayrollUi.TextBox();
    private readonly TextBox _password = PayrollUi.TextBox(true);
    private readonly TextBox _testPhone = PayrollUi.TextBox();
    private readonly CheckBox _enabled = new() { Text = "Enable automatic schedule texting", AutoSize = true, Font = WinTheme.BoldFont(10) };

    public ScheduleSmsSettingsForm(Func<AppDbContext> createDb)
    {
        _createDb = createDb; PayrollUi.Prepare(this, "Schedule Text Message Setup - HISAB KITAB", new Size(860, 650));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(20), BackColor = WinTheme.Bg };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.Controls.Add(PayrollUi.Heading("SCHEDULE TEXT MESSAGE GATEWAY"), 0, 0);
        root.Controls.Add(new Label { Text = "Uses a store-owned Android phone running SMS Gateway for Android in Local Server mode. The phone and this PC must be on the same private network, and the phone's SIM plan sends the texts.", Dock = DockStyle.Fill, ForeColor = WinTheme.Muted, Font = WinTheme.BodyFont(10), Padding = new Padding(8) }, 0, 1);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = WinTheme.Panel, Padding = new Padding(12) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        fields.Controls.Add(PayrollUi.Field("GATEWAY URL (example: http://192.168.1.25:8080/message)", _url, 520), 0, 0); fields.SetColumnSpan(fields.GetControlFromPosition(0,0)!, 2);
        fields.Controls.Add(PayrollUi.Field("USERNAME", _username, 340), 0, 1); fields.Controls.Add(PayrollUi.Field("PASSWORD", _password, 250), 1, 1);
        fields.Controls.Add(PayrollUi.Field("TEST EMPLOYEE PHONE", _testPhone, 340), 0, 2); fields.Controls.Add(_enabled, 1, 2); root.Controls.Add(fields, 0, 2);
        var note = new Label { Text = "The gateway password is encrypted for this Windows PC. Schedule messages contain work times only; never include SSNs, tax information, or payroll amounts.", Dock = DockStyle.Fill, ForeColor = WinTheme.BlueDark, Font = WinTheme.BodyFont(9), Padding = new Padding(8) }; root.Controls.Add(note,0,3);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var save = PayrollUi.Button("SAVE SETTINGS", true, 220); save.Click += async (_,_) => await SaveAsync(); var test = PayrollUi.Button("SEND TEST TEXT", true, 200); test.Click += async (_,_) => await TestAsync(); var close = PayrollUi.Button("CLOSE"); close.Click += (_,_) => Close(); buttons.Controls.Add(save,0,0); buttons.Controls.Add(test,1,0); buttons.Controls.Add(close,2,0); root.Controls.Add(buttons,0,4); Controls.Add(root); Shown += async (_,_) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using var db=_createDb(); var settings=await db.Settings.FirstOrDefaultAsync() ?? new AppSettings();
        _enabled.Checked=settings.SmsGatewayEnabled; _url.Text=settings.SmsGatewayUrl; _username.Text=settings.SmsGatewayUsername;
        if(settings.SmsGatewayPasswordEncrypted.Length>0) try{_password.Text=PayrollSensitiveDataProtector.UnprotectText(settings.SmsGatewayPasswordEncrypted);}catch{_password.Clear();}
    }
    private async Task SaveAsync()
    {
        await using var db=_createDb(); var settings=await db.Settings.FirstOrDefaultAsync(); if(settings is null){settings=new AppSettings();db.Settings.Add(settings);}
        settings.SmsGatewayEnabled=_enabled.Checked; settings.SmsGatewayUrl=_url.Text.Trim(); settings.SmsGatewayUsername=_username.Text.Trim(); settings.SmsGatewayPasswordEncrypted=PayrollSensitiveDataProtector.ProtectText(_password.Text);
        await db.SaveChangesAsync(); MessageBox.Show(this,"SMS gateway settings saved.","Schedule Texting",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }
    private async Task TestAsync()
    {
        try{var response=await ScheduleSmsGatewayService.SendAsync(_url.Text.Trim(),_username.Text.Trim(),_password.Text,_testPhone.Text,"HISAB KITAB schedule texting test. Your schedule notifications are configured.");MessageBox.Show(this,$"Test text accepted by the Android gateway.\n\n{response}","Schedule Texting",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){MessageBox.Show(this,ex.Message,"Text Failed",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
}

internal sealed class ScheduleNotificationLogForm : Form
{
    private readonly Func<AppDbContext> _createDb;
    private readonly int _storeId;
    private readonly DataGridView _grid = WinTheme.Grid();

    public ScheduleNotificationLogForm(Func<AppDbContext> createDb, int storeId)
    {
        _createDb=createDb;_storeId=storeId;PayrollUi.Prepare(this,"Schedule Text Delivery Log - HISAB KITAB",new Size(1250,760));
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,Padding=new Padding(16),BackColor=WinTheme.Bg};root.RowStyles.Add(new RowStyle(SizeType.Absolute,54));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
        root.Controls.Add(PayrollUi.Heading("SCHEDULE TEXT DELIVERY LOG"),0,0);root.Controls.Add(_grid,0,1);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,BackColor=WinTheme.Panel};var close=PayrollUi.Button("CLOSE");close.Click+=(_,_)=>Close();var refresh=PayrollUi.Button("REFRESH",true);refresh.Click+=async(_,_)=>await RefreshAsync();buttons.Controls.AddRange(new Control[]{close,refresh});root.Controls.Add(buttons,0,2);Controls.Add(root);Shown+=async(_,_)=>await RefreshAsync();
    }
    private async Task RefreshAsync(){await using var db=_createDb();var employees=await db.Employees.AsNoTracking().Where(x=>x.StoreId==_storeId).ToDictionaryAsync(x=>x.Id,x=>x.FullName);var rows=await db.ScheduleNotifications.AsNoTracking().Where(x=>x.StoreId==_storeId).OrderByDescending(x=>x.CreatedUtc).Take(500).ToListAsync();_grid.DataSource=rows.Select(x=>new{x.Id,Employee=employees.GetValueOrDefault(x.EmployeeId,$"Employee #{x.EmployeeId}"),Period=$"{x.ScheduleFrom:MM/dd/yyyy} - {x.ScheduleTo:MM/dd/yyyy}",x.PhoneNumber,x.Status,Sent=x.SentUtc?.ToLocalTime().ToString("MM/dd/yyyy h:mm tt")??"",Response=x.GatewayResponse,CreatedBy=x.CreatedByName}).ToList();}
}
