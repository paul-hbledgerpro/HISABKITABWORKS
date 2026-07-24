using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal sealed record ClientAccount(
    int CustomerId, int LicenseId, string BusinessName, string OwnerName, string Email, string Phone,
    string StoreGuid, string StoreZip, string StoreAddress, string DatabaseName, string SubscriptionKey,
    int MaxDevices, int MaxBusinesses, decimal MonthlyFee, DateTime ExpiresDate, string EnabledServices,
    bool IsActive, string PayrollState, string MonthlyReportEmail, int MonthlyReportDay);

internal sealed record ServicePrice(string ServiceName, bool Enabled, decimal MonthlyRate);

internal static class StandardServicePricing
{
    public const decimal OneTimeLicenseFee = 200m;
    public const decimal Accounting = 14.99m;
    public const decimal Payroll = 19.99m;
    public const decimal Scheduling = 12.99m;
    public const decimal MonthlyReports = 9.99m;

    public static decimal For(string serviceName) => serviceName switch
    {
        "Accounting" => Accounting,
        "Payroll" => Payroll,
        "Scheduling" => Scheduling,
        "MonthlyReports" => MonthlyReports,
        _ => 0m
    };
}

internal sealed record AccountInvoice(
    int Id, int CustomerId, int LicenseId, string InvoiceNumber, DateTime InvoiceDate, DateTime DueDate,
    DateTime PeriodStart, DateTime PeriodEnd, decimal Subtotal, decimal AmountPaid, string Status, string Notes,
    DateTime CreatedUtc)
{
    public decimal BalanceDue => Math.Max(0, Subtotal - AmountPaid);
}

internal sealed record AccountInvoiceItem(int Id, int InvoiceId, string ServiceName, string Description, decimal Amount);
internal sealed record AccountPayment(int Id, int InvoiceId, DateTime PaymentDate, decimal Amount, string Method, string ReferenceNumber, string Notes);
internal sealed record InvoiceDocumentData(ClientAccount Account, AccountInvoice Invoice, IReadOnlyList<AccountInvoiceItem> Items, IReadOnlyList<AccountPayment> Payments);
internal sealed record InvoiceInboxProvisioning(
    int CustomerId,
    int LicenseId,
    string StoreGuid,
    string WorkerStoreId,
    string InvoiceAddress,
    string EncryptedStoreApiToken,
    string ApiBaseUrl,
    DateTime UpdatedUtc);

internal sealed class ClientAccountService
{
    private const string LicensingDatabase = "HBLedgerPro_Licensing";
    private readonly string _server;
    private readonly string _username;
    private readonly string _password;

    public ClientAccountService(string server, string username, string password)
    {
        LocalSqlServerPolicy.RequireLocal(server);
        _server = LocalSqlServerPolicy.DefaultInstance;
        _username = string.Empty;
        _password = string.Empty;
    }

    public void ConnectAndUpgrade()
    {
        LocalSqlServerPolicy.EnsureDatabaseExists(_server, LicensingDatabase, _username, _password);
        using var connection = Open();
        using var command = new SqlCommand(@"
IF OBJECT_ID('dbo.Customers', 'U') IS NULL OR OBJECT_ID('dbo.Licenses', 'U') IS NULL
    THROW 51000, 'The HISAB KITAB licensing database has not been initialized. Open the License Generator and connect once first.', 1;
IF COL_LENGTH('dbo.Licenses', 'MaxDevices') IS NULL
    ALTER TABLE dbo.Licenses ADD MaxDevices INT NOT NULL CONSTRAINT DF_Licenses_MaxDevices DEFAULT(1);
IF COL_LENGTH('dbo.Licenses', 'EnabledServices') IS NULL
    ALTER TABLE dbo.Licenses ADD EnabledServices NVARCHAR(200) NOT NULL CONSTRAINT DF_Licenses_EnabledServices DEFAULT('Accounting');
IF COL_LENGTH('dbo.Licenses', 'PayrollState') IS NULL
    ALTER TABLE dbo.Licenses ADD PayrollState NVARCHAR(2) NOT NULL CONSTRAINT DF_Licenses_PayrollState DEFAULT('');
IF COL_LENGTH('dbo.Licenses', 'MonthlyReportEmail') IS NULL
    ALTER TABLE dbo.Licenses ADD MonthlyReportEmail NVARCHAR(254) NOT NULL CONSTRAINT DF_Licenses_MonthlyReportEmail DEFAULT('');
IF COL_LENGTH('dbo.Licenses', 'MonthlyReportDay') IS NULL
    ALTER TABLE dbo.Licenses ADD MonthlyReportDay TINYINT NOT NULL CONSTRAINT DF_Licenses_MonthlyReportDay DEFAULT(3);
IF COL_LENGTH('dbo.Customers', 'StoreGuid') IS NULL
    ALTER TABLE dbo.Customers ADD StoreGuid NVARCHAR(128) NULL;
IF COL_LENGTH('dbo.Customers', 'StoreZip') IS NULL
    ALTER TABLE dbo.Customers ADD StoreZip NVARCHAR(20) NULL;
EXEC(N'
UPDATE dbo.Licenses
SET PayrollState=UPPER(LEFT(AssignedDatabases,2))
WHERE (PayrollState IS NULL OR LEN(LTRIM(RTRIM(PayrollState)))=0)
  AND AssignedDatabases LIKE ''[A-Za-z][A-Za-z][_]%'';');

IF OBJECT_ID('dbo.AccountServicePrices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountServicePrices
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountServicePrices PRIMARY KEY,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        ServiceName NVARCHAR(80) NOT NULL,
        MonthlyRate DECIMAL(18,2) NOT NULL CONSTRAINT DF_AccountServicePrices_Rate DEFAULT(0),
        UpdatedUtc DATETIME2 NOT NULL CONSTRAINT DF_AccountServicePrices_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_AccountServicePrices UNIQUE(LicenseId, ServiceName)
    );
END;

IF OBJECT_ID('dbo.AccountInvoices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountInvoices
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountInvoices PRIMARY KEY,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        InvoiceNumber NVARCHAR(50) NOT NULL CONSTRAINT UQ_AccountInvoices_Number UNIQUE,
        InvoiceDate DATE NOT NULL,
        DueDate DATE NOT NULL,
        PeriodStart DATE NOT NULL,
        PeriodEnd DATE NOT NULL,
        Subtotal DECIMAL(18,2) NOT NULL,
        AmountPaid DECIMAL(18,2) NOT NULL CONSTRAINT DF_AccountInvoices_Paid DEFAULT(0),
        Status NVARCHAR(30) NOT NULL CONSTRAINT DF_AccountInvoices_Status DEFAULT('Open'),
        Notes NVARCHAR(1000) NOT NULL CONSTRAINT DF_AccountInvoices_Notes DEFAULT(''),
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_AccountInvoices_Created DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID('dbo.AccountInvoiceItems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountInvoiceItems
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountInvoiceItems PRIMARY KEY,
        InvoiceId INT NOT NULL,
        ServiceName NVARCHAR(80) NOT NULL,
        Description NVARCHAR(300) NOT NULL,
        Amount DECIMAL(18,2) NOT NULL
    );
    CREATE INDEX IX_AccountInvoiceItems_InvoiceId ON dbo.AccountInvoiceItems(InvoiceId);
END;

IF OBJECT_ID('dbo.AccountPayments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountPayments
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountPayments PRIMARY KEY,
        InvoiceId INT NOT NULL,
        PaymentDate DATE NOT NULL,
        Amount DECIMAL(18,2) NOT NULL,
        Method NVARCHAR(60) NOT NULL,
        ReferenceNumber NVARCHAR(120) NOT NULL CONSTRAINT DF_AccountPayments_Reference DEFAULT(''),
        Notes NVARCHAR(500) NOT NULL CONSTRAINT DF_AccountPayments_Notes DEFAULT(''),
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_AccountPayments_Created DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_AccountPayments_InvoiceId ON dbo.AccountPayments(InvoiceId);
END;

IF OBJECT_ID('dbo.InvoiceInboxProvisioning', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceInboxProvisioning
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceInboxProvisioning PRIMARY KEY,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        StoreGuid NVARCHAR(200) NOT NULL,
        WorkerStoreId NVARCHAR(80) NOT NULL,
        InvoiceAddress NVARCHAR(320) NOT NULL,
        EncryptedStoreApiToken NVARCHAR(2000) NOT NULL,
        ApiBaseUrl NVARCHAR(500) NOT NULL,
        UpdatedUtc DATETIME2 NOT NULL CONSTRAINT DF_InvoiceInboxProvisioning_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_InvoiceInboxProvisioning_StoreGuid UNIQUE(StoreGuid),
        CONSTRAINT UQ_InvoiceInboxProvisioning_Address UNIQUE(InvoiceAddress)
    );
    CREATE INDEX IX_InvoiceInboxProvisioning_CustomerId
        ON dbo.InvoiceInboxProvisioning(CustomerId);
END;", connection);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> Databases()
    {
        using var connection = new SqlConnection(ConnectionString("master")); connection.Open();
        using var command = new SqlCommand("SELECT name FROM sys.databases WHERE database_id>4 AND state_desc='ONLINE' AND name<>@license ORDER BY name", connection);
        command.Parameters.AddWithValue("@license", LicensingDatabase);
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<ClientAccount> LoadAccounts()
    {
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT c.Id, l.Id, c.BusinessName, c.OwnerName, c.Email, c.Phone,
       ISNULL(c.StoreGuid,''), ISNULL(c.StoreZip,''), ISNULL(cb.StoreAddress,''),
       ISNULL(cb.DatabaseName, l.AssignedDatabases), l.LicenseKey,
       l.MaxDevices, l.MaxStores, l.MonthlyFee, l.ExpiresDate, l.EnabledServices, l.IsActive,
       ISNULL(l.PayrollState,''), ISNULL(l.MonthlyReportEmail,''), ISNULL(l.MonthlyReportDay,3)
FROM dbo.Customers c
CROSS APPLY (SELECT TOP 1 * FROM dbo.Licenses x WHERE x.CustomerId=c.Id ORDER BY x.Id DESC) l
OUTER APPLY (SELECT TOP 1 * FROM dbo.CustomerBusinesses b WHERE b.CustomerId=c.Id ORDER BY b.IsPrimary DESC, b.Id) cb
ORDER BY c.BusinessName", connection);
        using var reader = command.ExecuteReader(); var result = new List<ClientAccount>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public ClientAccount Save(ClientAccount input)
    {
        Validate(input);
        using var connection = Open(); using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureOwnershipAvailable(connection, tx, input);
        var customerId = input.CustomerId;
        var licenseId = input.LicenseId;
        var key = input.SubscriptionKey;
        if (customerId == 0)
        {
            using var customer = new SqlCommand(@"
INSERT dbo.Customers (BusinessName, OwnerName, Email, Phone, Notes, StoreGuid, StoreZip)
OUTPUT INSERTED.Id VALUES (@business,@owner,@email,@phone,'Created in Client Account Manager',@guid,@zip)", connection, tx);
            AddCustomerParameters(customer, input); customerId = Convert.ToInt32(customer.ExecuteScalar());
            key = UniqueKey(connection, tx);
            using var license = new SqlCommand(@"
INSERT dbo.Licenses (CustomerId,LicenseKey,MaxStores,MaxUsers,MaxDevices,MonthlyFee,IsActive,ActivatedDate,ExpiresDate,AssignedDatabases,EnabledServices,PayrollState,MonthlyReportEmail,MonthlyReportDay)
OUTPUT INSERTED.Id VALUES (@customer,@key,@stores,999,@devices,@fee,1,SYSUTCDATETIME(),@expires,@guid,@services,@payrollState,@reportEmail,@reportDay)", connection, tx);
            AddLicenseParameters(license, input, customerId, key); licenseId = Convert.ToInt32(license.ExecuteScalar());
            using var business = new SqlCommand(@"
INSERT dbo.CustomerBusinesses (CustomerId,BusinessName,StoreAddress,DatabaseName,StoreGuid,IsPrimary,IsActive,CreatedUtc)
VALUES (@customer,@business,@address,@database,@guid,1,1,SYSUTCDATETIME())", connection, tx);
            AddBusinessParameters(business, input, customerId); business.ExecuteNonQuery();
        }
        else
        {
            using var customer = new SqlCommand(@"
UPDATE dbo.Customers SET BusinessName=@business,OwnerName=@owner,Email=@email,Phone=@phone,StoreGuid=@guid,StoreZip=@zip WHERE Id=@customer", connection, tx);
            AddCustomerParameters(customer, input); customer.Parameters.AddWithValue("@customer", customerId); customer.ExecuteNonQuery();
            using var license = new SqlCommand(@"
UPDATE dbo.Licenses SET MaxStores=@stores,MaxDevices=@devices,MonthlyFee=@fee,ExpiresDate=@expires,AssignedDatabases=@guid,EnabledServices=@services,PayrollState=@payrollState,MonthlyReportEmail=@reportEmail,MonthlyReportDay=@reportDay,IsActive=@active WHERE Id=@license AND CustomerId=@customer", connection, tx);
            AddLicenseParameters(license, input, customerId, key); license.Parameters.AddWithValue("@license", licenseId); license.Parameters.AddWithValue("@active", input.IsActive); license.ExecuteNonQuery();
            using var business = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM dbo.CustomerBusinesses WHERE CustomerId=@customer AND IsPrimary=1)
 UPDATE dbo.CustomerBusinesses SET BusinessName=@business,StoreAddress=@address,DatabaseName=@database,StoreGuid=@guid,IsActive=@active WHERE CustomerId=@customer AND IsPrimary=1
ELSE
 INSERT dbo.CustomerBusinesses (CustomerId,BusinessName,StoreAddress,DatabaseName,StoreGuid,IsPrimary,IsActive,CreatedUtc) VALUES (@customer,@business,@address,@database,@guid,1,@active,SYSUTCDATETIME())", connection, tx);
            AddBusinessParameters(business, input, customerId); business.Parameters.AddWithValue("@active", input.IsActive); business.ExecuteNonQuery();
        }
        tx.Commit();
        return input with { CustomerId = customerId, LicenseId = licenseId, SubscriptionKey = key };
    }

    public void UpdateServices(
        int customerId,
        int licenseId,
        string enabledServices,
        string payrollState,
        string monthlyReportEmail,
        int monthlyReportDay)
    {
        if (customerId <= 0 || licenseId <= 0)
            throw new InvalidOperationException("Select an existing client account first.");
        if (!enabledServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("Accounting", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Core Accounting must remain enabled.");

        using var connection = Open();
        using var command = new SqlCommand(@"
UPDATE dbo.Licenses
SET EnabledServices=@services, PayrollState=@payrollState,
    MonthlyReportEmail=@reportEmail, MonthlyReportDay=@reportDay
WHERE Id=@license AND CustomerId=@customer;", connection);
        command.Parameters.AddWithValue("@services", enabledServices);
        command.Parameters.AddWithValue("@payrollState", ValidatePayrollState(payrollState));
        command.Parameters.AddWithValue("@reportEmail", ValidateMonthlyReportEmail(enabledServices, monthlyReportEmail));
        command.Parameters.AddWithValue("@reportDay", ValidateMonthlyReportDay(monthlyReportDay));
        command.Parameters.AddWithValue("@license", licenseId);
        command.Parameters.AddWithValue("@customer", customerId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The selected client license could not be found. Refresh and select the account again.");
    }

    public InvoiceInboxProvisioning? LoadInvoiceInboxProvisioning(int customerId, string storeGuid)
    {
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT CustomerId,LicenseId,StoreGuid,WorkerStoreId,InvoiceAddress,
       EncryptedStoreApiToken,ApiBaseUrl,UpdatedUtc
FROM dbo.InvoiceInboxProvisioning
WHERE CustomerId=@customer AND StoreGuid=@guid;", connection);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@guid", storeGuid.Trim().ToUpperInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new InvoiceInboxProvisioning(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDateTime(7))
            : null;
    }

    public void SaveInvoiceInboxProvisioning(InvoiceInboxProvisioning value)
    {
        if (value.CustomerId <= 0 || value.LicenseId <= 0 ||
            string.IsNullOrWhiteSpace(value.StoreGuid) ||
            string.IsNullOrWhiteSpace(value.WorkerStoreId) ||
            string.IsNullOrWhiteSpace(value.InvoiceAddress) ||
            string.IsNullOrWhiteSpace(value.EncryptedStoreApiToken) ||
            string.IsNullOrWhiteSpace(value.ApiBaseUrl))
            throw new InvalidOperationException("The Invoice Inbox provisioning record is incomplete.");

        using var connection = Open();
        using var command = new SqlCommand(@"
MERGE dbo.InvoiceInboxProvisioning AS target
USING (SELECT @guid AS StoreGuid) AS source
ON target.StoreGuid=source.StoreGuid
WHEN MATCHED THEN UPDATE SET
    LicenseId=@license,StoreGuid=@guid,WorkerStoreId=@workerStore,
    InvoiceAddress=@address,EncryptedStoreApiToken=@token,
    ApiBaseUrl=@baseUrl,UpdatedUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT(CustomerId,LicenseId,StoreGuid,WorkerStoreId,InvoiceAddress,
           EncryptedStoreApiToken,ApiBaseUrl,UpdatedUtc)
    VALUES(@customer,@license,@guid,@workerStore,@address,@token,@baseUrl,SYSUTCDATETIME());",
            connection);
        command.Parameters.AddWithValue("@customer", value.CustomerId);
        command.Parameters.AddWithValue("@license", value.LicenseId);
        command.Parameters.AddWithValue("@guid", value.StoreGuid.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@workerStore", value.WorkerStoreId.Trim());
        command.Parameters.AddWithValue("@address", value.InvoiceAddress.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("@token", value.EncryptedStoreApiToken);
        command.Parameters.AddWithValue("@baseUrl", value.ApiBaseUrl.Trim().TrimEnd('/'));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ServicePrice> LoadServicePrices(ClientAccount account)
    {
        var enabled = account.EnabledServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT ServiceName, MonthlyRate
FROM dbo.AccountServicePrices
WHERE LicenseId=@license;", connection);
        command.Parameters.AddWithValue("@license", account.LicenseId);
        using var reader = command.ExecuteReader();
        var saved = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) saved[reader.GetString(0)] = reader.GetDecimal(1);

        var result = new List<ServicePrice>();
        foreach (var service in new[] { "Accounting", "Payroll", "Scheduling", "MonthlyReports" })
        {
            var isEnabled = enabled.Contains(service);
            var fallback = saved.Count == 0 && isEnabled
                ? StandardServicePricing.For(service)
                : 0m;
            result.Add(new ServicePrice(service, isEnabled, saved.GetValueOrDefault(service, fallback)));
        }
        return result;
    }

    public void SaveServicePrices(ClientAccount account, IReadOnlyCollection<ServicePrice> prices)
    {
        var enabled = account.EnabledServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid = prices.Where(x => enabled.Contains(x.ServiceName)).ToList();
        if (valid.Any(x => x.MonthlyRate < 0)) throw new InvalidOperationException("Monthly service prices cannot be negative.");

        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var price in prices)
        {
            using var command = new SqlCommand(@"
MERGE dbo.AccountServicePrices AS target
USING (SELECT @license AS LicenseId, @service AS ServiceName) AS source
ON target.LicenseId=source.LicenseId AND target.ServiceName=source.ServiceName
WHEN MATCHED THEN UPDATE SET CustomerId=@customer,MonthlyRate=@rate,UpdatedUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(CustomerId,LicenseId,ServiceName,MonthlyRate)
VALUES(@customer,@license,@service,@rate);", connection, tx);
            command.Parameters.AddWithValue("@customer", account.CustomerId);
            command.Parameters.AddWithValue("@license", account.LicenseId);
            command.Parameters.AddWithValue("@service", price.ServiceName);
            command.Parameters.AddWithValue("@rate", price.Enabled && enabled.Contains(price.ServiceName) ? price.MonthlyRate : 0m);
            command.ExecuteNonQuery();
        }
        var total = valid.Sum(x => x.MonthlyRate);
        using (var update = new SqlCommand("UPDATE dbo.Licenses SET MonthlyFee=@total WHERE Id=@license AND CustomerId=@customer", connection, tx))
        {
            update.Parameters.AddWithValue("@total", total);
            update.Parameters.AddWithValue("@license", account.LicenseId);
            update.Parameters.AddWithValue("@customer", account.CustomerId);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<AccountInvoice> LoadInvoices(int customerId)
    {
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT Id,CustomerId,LicenseId,InvoiceNumber,InvoiceDate,DueDate,PeriodStart,PeriodEnd,
       Subtotal,AmountPaid,Status,Notes,CreatedUtc
FROM dbo.AccountInvoices
WHERE CustomerId=@customer
ORDER BY InvoiceDate DESC,Id DESC;", connection);
        command.Parameters.AddWithValue("@customer", customerId);
        using var reader = command.ExecuteReader();
        var result = new List<AccountInvoice>();
        while (reader.Read()) result.Add(ReadInvoice(reader));
        return result;
    }

    public AccountInvoice CreateInvoice(ClientAccount account, DateTime invoiceDate, DateTime dueDate, DateTime periodStart, DateTime periodEnd)
    {
        if (dueDate.Date < invoiceDate.Date) throw new InvalidOperationException("Due date cannot be before the invoice date.");
        if (periodEnd.Date < periodStart.Date) throw new InvalidOperationException("Billing period end cannot be before its start.");
        var prices = LoadServicePrices(account).Where(x => x.Enabled).ToList();
        if (prices.Count == 0) throw new InvalidOperationException("This account has no enabled services.");
        if (prices.Sum(x => x.MonthlyRate) <= 0) throw new InvalidOperationException("Save a monthly price for at least one enabled service before creating an invoice.");
        if (LoadInvoices(account.CustomerId).Any(x => x.PeriodStart.Date == periodStart.Date && x.PeriodEnd.Date == periodEnd.Date))
            throw new InvalidOperationException("An invoice already exists for this account and billing period.");

        using var connection = Open();
        using var tx = connection.BeginTransaction(IsolationLevel.Serializable);
        var invoiceNumber = UniqueInvoiceNumber(connection, tx, invoiceDate);
        var total = prices.Sum(x => x.MonthlyRate);
        int invoiceId;
        using (var command = new SqlCommand(@"
INSERT dbo.AccountInvoices(CustomerId,LicenseId,InvoiceNumber,InvoiceDate,DueDate,PeriodStart,PeriodEnd,Subtotal,AmountPaid,Status,Notes)
OUTPUT INSERTED.Id
VALUES(@customer,@license,@number,@invoice,@due,@from,@to,@total,0,'Open','Monthly software subscription services');", connection, tx))
        {
            command.Parameters.AddWithValue("@customer", account.CustomerId);
            command.Parameters.AddWithValue("@license", account.LicenseId);
            command.Parameters.AddWithValue("@number", invoiceNumber);
            command.Parameters.AddWithValue("@invoice", invoiceDate.Date);
            command.Parameters.AddWithValue("@due", dueDate.Date);
            command.Parameters.AddWithValue("@from", periodStart.Date);
            command.Parameters.AddWithValue("@to", periodEnd.Date);
            command.Parameters.AddWithValue("@total", total);
            invoiceId = Convert.ToInt32(command.ExecuteScalar());
        }
        foreach (var price in prices)
        {
            using var item = new SqlCommand(@"
INSERT dbo.AccountInvoiceItems(InvoiceId,ServiceName,Description,Amount)
VALUES(@invoice,@service,@description,@amount);", connection, tx);
            item.Parameters.AddWithValue("@invoice", invoiceId);
            item.Parameters.AddWithValue("@service", price.ServiceName);
            item.Parameters.AddWithValue("@description", $"{price.ServiceName} monthly subscription - {periodStart:MM/dd/yyyy} to {periodEnd:MM/dd/yyyy}");
            item.Parameters.AddWithValue("@amount", price.MonthlyRate);
            item.ExecuteNonQuery();
        }
        tx.Commit();
        return LoadInvoices(account.CustomerId).First(x => x.Id == invoiceId);
    }

    public IReadOnlyList<AccountPayment> LoadPayments(int invoiceId)
    {
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT Id,InvoiceId,PaymentDate,Amount,Method,ReferenceNumber,Notes
FROM dbo.AccountPayments
WHERE InvoiceId=@invoice
ORDER BY PaymentDate DESC,Id DESC;", connection);
        command.Parameters.AddWithValue("@invoice", invoiceId);
        using var reader = command.ExecuteReader();
        var result = new List<AccountPayment>();
        while (reader.Read())
            result.Add(new AccountPayment(reader.GetInt32(0), reader.GetInt32(1), reader.GetDateTime(2), reader.GetDecimal(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return result;
    }

    public void RecordPayment(AccountInvoice invoice, DateTime paymentDate, decimal amount, string method, string referenceNumber, string notes)
    {
        if (amount <= 0) throw new InvalidOperationException("Payment amount must be greater than zero.");
        if (amount > invoice.BalanceDue) throw new InvalidOperationException($"Payment cannot exceed the remaining balance of {invoice.BalanceDue:C2}.");
        if (string.IsNullOrWhiteSpace(method)) throw new InvalidOperationException("Select a payment method.");
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var insert = new SqlCommand(@"
INSERT dbo.AccountPayments(InvoiceId,PaymentDate,Amount,Method,ReferenceNumber,Notes)
VALUES(@invoice,@date,@amount,@method,@reference,@notes);", connection, tx))
        {
            insert.Parameters.AddWithValue("@invoice", invoice.Id);
            insert.Parameters.AddWithValue("@date", paymentDate.Date);
            insert.Parameters.AddWithValue("@amount", amount);
            insert.Parameters.AddWithValue("@method", method.Trim());
            insert.Parameters.AddWithValue("@reference", referenceNumber.Trim());
            insert.Parameters.AddWithValue("@notes", notes.Trim());
            insert.ExecuteNonQuery();
        }
        var newPaid = invoice.AmountPaid + amount;
        var status = newPaid >= invoice.Subtotal ? "Paid" : "Partially Paid";
        using (var update = new SqlCommand("UPDATE dbo.AccountInvoices SET AmountPaid=@paid,Status=@status WHERE Id=@invoice", connection, tx))
        {
            update.Parameters.AddWithValue("@paid", newPaid);
            update.Parameters.AddWithValue("@status", status);
            update.Parameters.AddWithValue("@invoice", invoice.Id);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public InvoiceDocumentData LoadInvoiceDocument(ClientAccount account, int invoiceId)
    {
        var invoice = LoadInvoices(account.CustomerId).FirstOrDefault(x => x.Id == invoiceId)
                      ?? throw new InvalidOperationException("The selected invoice could not be found.");
        using var connection = Open();
        using var command = new SqlCommand(@"
SELECT Id,InvoiceId,ServiceName,Description,Amount
FROM dbo.AccountInvoiceItems
WHERE InvoiceId=@invoice
ORDER BY Id;", connection);
        command.Parameters.AddWithValue("@invoice", invoiceId);
        using var reader = command.ExecuteReader();
        var items = new List<AccountInvoiceItem>();
        while (reader.Read())
            items.Add(new AccountInvoiceItem(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4)));
        return new InvoiceDocumentData(account, invoice, items, LoadPayments(invoiceId));
    }

    private static void Validate(ClientAccount value)
    {
        if (string.IsNullOrWhiteSpace(value.BusinessName) || string.IsNullOrWhiteSpace(value.OwnerName) ||
            string.IsNullOrWhiteSpace(value.StoreGuid) || string.IsNullOrWhiteSpace(value.StoreZip) || string.IsNullOrWhiteSpace(value.DatabaseName))
            throw new InvalidOperationException("Business, owner, Store GUID, ZIP and database are required.");
        if (value.StoreZip.Length != 5 || !value.StoreZip.All(char.IsDigit)) throw new InvalidOperationException("Store ZIP must be five digits.");
        if (!value.EnabledServices.Split(',').Contains("Accounting", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Core Accounting must remain enabled.");
        _ = ValidatePayrollState(value.PayrollState);
        _ = ValidateMonthlyReportEmail(value.EnabledServices, value.MonthlyReportEmail);
        _ = ValidateMonthlyReportDay(value.MonthlyReportDay);
        _ = StateFromStoreGuid(value.StoreGuid);
    }

    private static void EnsureOwnershipAvailable(SqlConnection connection, SqlTransaction tx, ClientAccount input)
    {
        using var command = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.Customers c
LEFT JOIN dbo.CustomerBusinesses b ON b.CustomerId=c.Id AND b.IsActive=1
WHERE c.Id<>@customer AND (c.StoreGuid=@guid OR b.StoreGuid=@guid OR b.DatabaseName=@database)", connection, tx);
        command.Parameters.AddWithValue("@customer", input.CustomerId); command.Parameters.AddWithValue("@guid", input.StoreGuid); command.Parameters.AddWithValue("@database", input.DatabaseName);
        if (Convert.ToInt32(command.ExecuteScalar()) > 0) throw new InvalidOperationException("This Store GUID or SQL database already belongs to another client account.");
    }

    private static void AddCustomerParameters(SqlCommand command, ClientAccount value)
    {
        command.Parameters.AddWithValue("@business", value.BusinessName.Trim()); command.Parameters.AddWithValue("@owner", value.OwnerName.Trim());
        command.Parameters.AddWithValue("@email", value.Email.Trim()); command.Parameters.AddWithValue("@phone", value.Phone.Trim());
        command.Parameters.AddWithValue("@guid", value.StoreGuid.Trim().ToUpperInvariant()); command.Parameters.AddWithValue("@zip", value.StoreZip.Trim());
    }

    private static void AddLicenseParameters(SqlCommand command, ClientAccount value, int customerId, string key)
    {
        command.Parameters.AddWithValue("@customer", customerId); command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@stores", value.MaxBusinesses); command.Parameters.AddWithValue("@devices", value.MaxDevices);
        command.Parameters.AddWithValue("@fee", value.MonthlyFee); command.Parameters.AddWithValue("@expires", value.ExpiresDate);
        command.Parameters.AddWithValue("@guid", value.StoreGuid.Trim().ToUpperInvariant()); command.Parameters.AddWithValue("@services", value.EnabledServices);
        command.Parameters.AddWithValue("@payrollState", ValidatePayrollState(value.PayrollState));
        command.Parameters.AddWithValue("@reportEmail", ValidateMonthlyReportEmail(value.EnabledServices, value.MonthlyReportEmail));
        command.Parameters.AddWithValue("@reportDay", ValidateMonthlyReportDay(value.MonthlyReportDay));
    }

    private static void AddBusinessParameters(SqlCommand command, ClientAccount value, int customerId)
    {
        command.Parameters.AddWithValue("@customer", customerId); command.Parameters.AddWithValue("@business", value.BusinessName.Trim());
        command.Parameters.AddWithValue("@address", value.StoreAddress.Trim()); command.Parameters.AddWithValue("@database", value.DatabaseName.Trim());
        command.Parameters.AddWithValue("@guid", value.StoreGuid.Trim().ToUpperInvariant());
    }

    private static ClientAccount Read(SqlDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetInt32(11),
        r.GetInt32(12), r.GetDecimal(13), r.GetDateTime(14), r.GetString(15), r.GetBoolean(16),
        r.IsDBNull(17) ? "" : r.GetString(17), r.IsDBNull(18) ? "" : r.GetString(18),
        r.IsDBNull(19) ? 3 : Convert.ToInt32(r.GetByte(19)));

    private static string ValidateMonthlyReportEmail(string enabledServices, string value)
    {
        var enabled = enabledServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("MonthlyReports", StringComparer.OrdinalIgnoreCase);
        var email = (value ?? "").Trim();
        if (!enabled)
            return "";
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw new InvalidOperationException("Enter a valid recipient email for automatic monthly reports.");
        return email;
    }

    private static int ValidateMonthlyReportDay(int value)
    {
        if (value is < 1 or > 28)
            throw new InvalidOperationException("Monthly report delivery day must be between 1 and 28.");
        return value;
    }

    private static string ValidatePayrollState(string value)
    {
        var state = (value ?? "").Trim().ToUpperInvariant();
        if (!UsStateCodes.Contains(state))
            throw new InvalidOperationException("Select a valid U.S. payroll state in the Developer Account Manager.");
        return state;
    }

    private static string StateFromStoreGuid(string storeGuid)
    {
        var parts = (storeGuid ?? "").Trim().ToUpperInvariant().Split('_');
        if (parts.Length != 4 || !UsStateCodes.Contains(parts[0]))
            throw new InvalidOperationException("Store GUID must begin with a valid two-letter U.S. state code.");
        return parts[0];
    }

    private static readonly HashSet<string> UsStateCodes = new(
    [
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY",
        "LA","ME","MD","MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND",
        "OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
    ], StringComparer.Ordinal);
    private static AccountInvoice ReadInvoice(SqlDataReader r) => new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetDateTime(4), r.GetDateTime(5), r.GetDateTime(6), r.GetDateTime(7), r.GetDecimal(8), r.GetDecimal(9), r.GetString(10), r.GetString(11), r.GetDateTime(12));
    private static string UniqueKey(SqlConnection connection, SqlTransaction tx)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true) { var bytes = RandomNumberGenerator.GetBytes(12); var text = new string(bytes.Select(x => chars[x % chars.Length]).ToArray()); var key = $"HBL-{text[..4]}-{text[4..8]}-{text[8..]}"; using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.Licenses WHERE LicenseKey=@key", connection, tx); check.Parameters.AddWithValue("@key", key); if (Convert.ToInt32(check.ExecuteScalar()) == 0) return key; }
    }
    private static string UniqueInvoiceNumber(SqlConnection connection, SqlTransaction tx, DateTime invoiceDate)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var bytes = RandomNumberGenerator.GetBytes(4);
            var suffix = new string(bytes.Select(x => chars[x % chars.Length]).ToArray());
            var number = $"INV-HKW-{invoiceDate:yyyyMMdd}-{suffix}";
            using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.AccountInvoices WHERE InvoiceNumber=@number", connection, tx);
            check.Parameters.AddWithValue("@number", number);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0) return number;
        }
    }
    private SqlConnection Open() { var connection = new SqlConnection(ConnectionString(LicensingDatabase)); connection.Open(); return connection; }
    private string ConnectionString(string database)
        => LocalSqlServerPolicy.BuildConnectionString(_server, database, _username, _password);
}
