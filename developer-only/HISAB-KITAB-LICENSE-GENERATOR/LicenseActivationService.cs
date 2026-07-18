using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class LicenseActivationService
{
    private const string LicensingDatabase = "HBLedgerPro_Licensing";
    private const int DefaultMaxUsers = 999;
    private readonly string _server;
    private readonly string _username;
    private readonly string _password;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public LicenseActivationService(string server, string username, string password)
    {
        _server = server.Trim();
        _username = username.Trim();
        _password = password;
    }

    public string LicensingConnectionString => ConnectionString(LicensingDatabase);

    public IReadOnlyList<string> ListBusinessDatabases()
    {
        using var connection = new SqlConnection(ConnectionString("master"));
        connection.Open();
        using var command = new SqlCommand(@"
SELECT name
FROM sys.databases
WHERE database_id > 4 AND state_desc='ONLINE' AND name<>@licensingDatabase
ORDER BY name", connection);
        command.Parameters.AddWithValue("@licensingDatabase", LicensingDatabase);
        using var reader = command.ExecuteReader();
        var databases = new List<string>();
        while (reader.Read())
            databases.Add(reader.GetString(0));
        return databases;
    }

    public void TestAndPrepareDatabase()
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(SchemaSql, connection) { CommandTimeout = 120 };
        command.ExecuteNonQuery();
    }

    public ActivationPreparation Prepare(
        string storeGuid,
        string businessName,
        string storeZip,
        string databaseName,
        string pcId,
        DeviceLicenseRequestV2? protectedRequest,
        int initialPcSeats,
        int initialBusinessLimit,
        DateTime expiresUtc,
        bool allowBusinessTransfer = false)
    {
        storeGuid = storeGuid.Trim().ToUpperInvariant();
        businessName = businessName.Trim();
        storeZip = storeZip.Trim();
        databaseName = databaseName.Trim();
        if (string.IsNullOrWhiteSpace(databaseName))
            databaseName = storeGuid;
        pcId = pcId.Trim().ToUpperInvariant();
        ValidateVisibleFields(storeGuid, businessName, storeZip, pcId);

        if (protectedRequest is not null)
        {
            DeviceRequestValidator.Validate(protectedRequest);
            RequireMatch(storeGuid, protectedRequest.StoreGuid, "Store GUID");
            RequireMatch(businessName, protectedRequest.BusinessName, "Store Name");
            RequireMatch(storeZip, protectedRequest.StoreZip, "Store ZIP");
            RequireMatch(pcId, protectedRequest.DeviceId, "PC ID");
        }

        var parentSubscriptionKey = protectedRequest?.SubscriptionKey?.Trim() ?? "";
        var addingBusiness = !string.IsNullOrWhiteSpace(parentSubscriptionKey);
        var subscription = addingBusiness
            ? FindSubscriptionByKey(parentSubscriptionKey)
            : FindSubscription(storeGuid);
        var createdStore = false;
        var addedBusiness = false;
        if (addingBusiness && subscription is null)
            throw new InvalidOperationException(
                "The existing client subscription in this protected request was not found or is inactive. Generate the request again from the licensed PC.");

        if (subscription is null)
        {
            if (protectedRequest is null)
                throw new InvalidOperationException(
                    "This Store GUID is not registered yet. Paste the PC ID using the COPY button on the customer's activation screen so the protected first-PC proof is included.");
            subscription = CreateSubscription(
                storeGuid, businessName, storeZip, databaseName,
                initialPcSeats, initialBusinessLimit, expiresUtc);
            createdStore = true;
        }
        else if (!addingBusiness &&
                 !string.Equals(subscription.BusinessName, businessName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Store GUID {storeGuid} is already registered to '{subscription.BusinessName}', not '{businessName}'.");
        }

        EnsurePrimaryBusiness(subscription);
        if (addingBusiness &&
            !string.Equals(storeGuid, subscription.DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            var activeBusinesses = LoadApprovedBusinesses(subscription.CustomerId);
            var alreadyApproved = activeBusinesses.Any(x =>
                string.Equals(x.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase));
            var requiredBusinessCount = activeBusinesses.Count + (alreadyApproved ? 0 : 1);
            var authorizedBusinessLimit = Math.Max(subscription.MaxStores, initialBusinessLimit);
            if (requiredBusinessCount > authorizedBusinessLimit)
                throw new BusinessLimitRequiredException(requiredBusinessCount);

            EnsureBusinessCanBelongToCustomer(
                subscription.CustomerId, storeGuid, databaseName, allowBusinessTransfer);
            EnsurePhysicalDatabase(databaseName);
            UpsertAdditionalBusiness(subscription.CustomerId, storeGuid, databaseName, businessName);
            addedBusiness = !alreadyApproved;
        }
        else
        {
            SaveCustomerActivationMetadata(subscription.CustomerId, storeGuid, storeZip);
            EnsurePhysicalDatabase(databaseName);
        }

        var devices = LoadDevices(subscription.LicenseId);
        var request = protectedRequest ?? RequestFromRegisteredPc(subscription, devices, pcId, storeZip);
        var otherAssignments = LoadOtherStoreAssignments(subscription.LicenseId, pcId);
        return new ActivationPreparation(subscription, request, devices, otherAssignments, createdStore, addedBusiness);
    }

    public IssuedActivation Issue(
        ActivationPreparation preparation,
        PcSeatChoice seatChoice,
        int requestedBusinessLimit,
        DateTime expiresUtc)
    {
        var subscription = preparation.Subscription;
        var request = preparation.Request;
        var activeCount = preparation.Devices.Count(IsActiveSeat);
        var maxDevices = seatChoice.Action == PcSeatAction.AddPaidPc
            ? Math.Max(subscription.MaxDevices, activeCount + 1)
            : Math.Max(1, subscription.MaxDevices);
        var maxBusinesses = Math.Max(1, requestedBusinessLimit);
        var payload = BuildPayload(subscription, request, maxDevices, maxBusinesses, expiresUtc);
        var licenseJson = BuildSignedLicense(payload);
        var formattedLicense = ActivationCodeCodec.FormatLicense(payload, licenseJson);

        RegisterDeviceAndUpdateSubscription(
            subscription, request, maxDevices, maxBusinesses, expiresUtc,
            payload.ActivationId, formattedLicense, seatChoice);

        var message = preparation.AddedBusiness
            ? $"Licensed business '{request.BusinessName}' added; existing PC license updated"
            : seatChoice.ReleaseOtherStoreAssignments
            ? "PC moved to this Store GUID; the previous store assignment was released"
            : preparation.OtherStoreAssignments.Count > 0 &&
              !preparation.Devices.Any(x => string.Equals(x.DeviceId, request.DeviceId, StringComparison.Ordinal))
                ? "PC added to this Store GUID and its existing store assignment was kept"
                : seatChoice.Action switch
        {
            PcSeatAction.FirstPc => "Store and first PC registered",
            PcSeatAction.RenewSamePc => "Existing PC matched and renewed",
            PcSeatAction.AddPaidPc => $"Additional paid PC added; subscription now has {maxDevices} PC seat(s)",
            PcSeatAction.ReplacePc => "Old PC replaced; paid PC seat count was not increased",
            _ => "PC license generated"
        };
        return new IssuedActivation(
            formattedLicense,
            licenseJson,
            ActivationCodeCodec.DisplayLicenseKey(payload),
            payload,
            maxDevices,
            maxBusinesses,
            message,
            LoadDevices(subscription.LicenseId));
    }

    public IReadOnlyList<RegisteredLicensePc> LoadDevices(int licenseId)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT Id, DeviceId, InstallationId, DeviceName, DevicePublicKey, FingerprintHash,
       Status, ActivatedDate, ExpiresDate, LastLicenseIssuedDate
FROM dbo.LicenseDevices
WHERE LicenseId=@licenseId
ORDER BY CASE WHEN Status='Active' THEN 0 ELSE 1 END, DeviceName", connection);
        command.Parameters.AddWithValue("@licenseId", licenseId);
        using var reader = command.ExecuteReader();
        var devices = new List<RegisteredLicensePc>();
        while (reader.Read())
        {
            devices.Add(new RegisteredLicensePc(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDateTime(7),
                reader.GetDateTime(8), reader.GetDateTime(9)));
        }
        return devices;
    }

    private IReadOnlyList<OtherStorePcAssignment> LoadOtherStoreAssignments(int targetLicenseId, string pcId)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT d.LicenseId, l.AssignedDatabases, c.BusinessName, d.DeviceName, d.Status, d.ExpiresDate
FROM dbo.LicenseDevices d
INNER JOIN dbo.Licenses l ON l.Id=d.LicenseId
INNER JOIN dbo.Customers c ON c.Id=l.CustomerId
WHERE d.DeviceId=@pcId AND d.LicenseId<>@targetLicenseId AND d.Status='Active'
ORDER BY c.BusinessName", connection);
        command.Parameters.AddWithValue("@pcId", pcId);
        command.Parameters.AddWithValue("@targetLicenseId", targetLicenseId);
        using var reader = command.ExecuteReader();
        var assignments = new List<OtherStorePcAssignment>();
        while (reader.Read())
        {
            assignments.Add(new OtherStorePcAssignment(
                reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetDateTime(5)));
        }
        return assignments;
    }

    private ClientSubscription? FindSubscription(string storeGuid)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT TOP 1 c.Id, l.Id, c.BusinessName, l.LicenseKey, l.AssignedDatabases,
       l.MaxStores, l.MaxUsers, l.MaxDevices, l.ExpiresDate, l.EnabledServices, l.PayrollState
FROM dbo.Customers c
INNER JOIN dbo.Licenses l ON l.CustomerId=c.Id
WHERE l.AssignedDatabases=@storeGuid AND l.IsActive=1
ORDER BY l.Id DESC", connection);
        command.Parameters.AddWithValue("@storeGuid", storeGuid);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadSubscription(reader)
            : null;
    }

    private ClientSubscription? FindSubscriptionByKey(string subscriptionKey)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT TOP 1 c.Id, l.Id, c.BusinessName, l.LicenseKey, l.AssignedDatabases,
       l.MaxStores, l.MaxUsers, l.MaxDevices, l.ExpiresDate, l.EnabledServices, l.PayrollState
FROM dbo.Customers c
INNER JOIN dbo.Licenses l ON l.CustomerId=c.Id
WHERE l.LicenseKey=@subscriptionKey AND l.IsActive=1
ORDER BY l.Id DESC", connection);
        command.Parameters.AddWithValue("@subscriptionKey", subscriptionKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadSubscription(reader)
            : null;
    }

    private ClientSubscription CreateSubscription(
        string storeGuid,
        string businessName,
        string storeZip,
        string databaseName,
        int maxDevices,
        int maxBusinesses,
        DateTime expiresUtc)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        using (var recheck = new SqlCommand(@"
SELECT TOP 1 c.Id, l.Id, c.BusinessName, l.LicenseKey, l.AssignedDatabases,
       l.MaxStores, l.MaxUsers, l.MaxDevices, l.ExpiresDate, l.EnabledServices, l.PayrollState
FROM dbo.Customers c
INNER JOIN dbo.Licenses l ON l.CustomerId=c.Id
WHERE l.AssignedDatabases=@storeGuid AND l.IsActive=1
ORDER BY l.Id DESC", connection, transaction))
        {
            recheck.Parameters.AddWithValue("@storeGuid", storeGuid);
            using var reader = recheck.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadSubscription(reader);
                reader.Close();
                transaction.Commit();
                return existing;
            }
        }

        int customerId;
        using (var customer = new SqlCommand(@"
INSERT dbo.Customers (BusinessName, OwnerName, Email, Phone, Notes, StoreGuid, StoreZip)
OUTPUT INSERTED.Id
VALUES (@business, @owner, '', '', @notes, @storeGuid, @storeZip)", connection, transaction))
        {
            customer.Parameters.AddWithValue("@business", businessName);
            customer.Parameters.AddWithValue("@owner", businessName);
            customer.Parameters.AddWithValue("@notes", $"Zip: {storeZip}; Created by developer activation workflow");
            customer.Parameters.AddWithValue("@storeGuid", storeGuid);
            customer.Parameters.AddWithValue("@storeZip", storeZip);
            customerId = Convert.ToInt32(customer.ExecuteScalar());
        }

        var subscriptionKey = GenerateUniqueSubscriptionKey(connection, transaction);
        int licenseId;
        using (var license = new SqlCommand(@"
INSERT dbo.Licenses
    (CustomerId, LicenseKey, MaxStores, MaxUsers, MaxDevices, MonthlyFee,
     IsActive, ActivatedDate, ExpiresDate, AssignedDatabases, PayrollState)
OUTPUT INSERTED.Id
VALUES
    (@customerId, @key, @maxStores, @maxUsers, @maxDevices, 0.00,
     1, SYSUTCDATETIME(), @expires, @database, @payrollState)", connection, transaction))
        {
            license.Parameters.AddWithValue("@customerId", customerId);
            license.Parameters.AddWithValue("@key", subscriptionKey);
            license.Parameters.AddWithValue("@maxStores", Math.Max(1, maxBusinesses));
            license.Parameters.AddWithValue("@maxUsers", DefaultMaxUsers);
            license.Parameters.AddWithValue("@maxDevices", Math.Max(1, maxDevices));
            license.Parameters.AddWithValue("@expires", expiresUtc);
            license.Parameters.AddWithValue("@database", storeGuid);
            license.Parameters.AddWithValue("@payrollState", StateFromStoreGuid(storeGuid));
            licenseId = Convert.ToInt32(license.ExecuteScalar());
        }

        using (var business = new SqlCommand(@"
INSERT dbo.CustomerBusinesses
    (CustomerId, BusinessName, StoreAddress, DatabaseName, StoreGuid, IsPrimary, IsActive, CreatedUtc)
VALUES
    (@customerId, @business, '', @database, @storeGuid, 1, 1, SYSUTCDATETIME())", connection, transaction))
        {
            business.Parameters.AddWithValue("@customerId", customerId);
            business.Parameters.AddWithValue("@business", businessName);
            business.Parameters.AddWithValue("@database", databaseName);
            business.Parameters.AddWithValue("@storeGuid", storeGuid);
            business.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ClientSubscription(
            customerId, licenseId, businessName, subscriptionKey, storeGuid,
            Math.Max(1, maxBusinesses), DefaultMaxUsers, Math.Max(1, maxDevices), expiresUtc,
            "Accounting", StateFromStoreGuid(storeGuid));
    }

    private void SaveCustomerActivationMetadata(int customerId, string storeGuid, string storeZip)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
UPDATE dbo.Customers
SET StoreGuid=@storeGuid, StoreZip=@storeZip,
    Notes=CASE WHEN Notes IS NULL OR Notes='' THEN 'Zip: ' + @storeZip ELSE Notes END
WHERE Id=@customerId", connection);
        command.Parameters.AddWithValue("@storeGuid", storeGuid);
        command.Parameters.AddWithValue("@storeZip", storeZip);
        command.Parameters.AddWithValue("@customerId", customerId);
        command.ExecuteNonQuery();
    }

    private void EnsurePrimaryBusiness(ClientSubscription subscription)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CustomerBusinesses WHERE CustomerId=@customerId AND StoreGuid=@storeGuid)
BEGIN
    INSERT dbo.CustomerBusinesses
        (CustomerId, BusinessName, StoreAddress, DatabaseName, StoreGuid, IsPrimary, IsActive, CreatedUtc)
    VALUES
        (@customerId, @business, '', @database, @storeGuid, 1, 1, SYSUTCDATETIME());
END
ELSE
BEGIN
    UPDATE dbo.CustomerBusinesses
    SET BusinessName=@business, StoreGuid=@storeGuid, IsPrimary=1, IsActive=1
    WHERE CustomerId=@customerId AND StoreGuid=@storeGuid;
END", connection);
        command.Parameters.AddWithValue("@customerId", subscription.CustomerId);
        command.Parameters.AddWithValue("@business", subscription.BusinessName);
        command.Parameters.AddWithValue("@database", subscription.DatabaseName);
        command.Parameters.AddWithValue("@storeGuid", subscription.DatabaseName);
        command.ExecuteNonQuery();
    }

    private void EnsureBusinessCanBelongToCustomer(
        int customerId,
        string storeGuid,
        string databaseName,
        bool allowBusinessTransfer)
    {
        var primaryOwner = FindSubscription(storeGuid);
        if (primaryOwner is not null && primaryOwner.CustomerId != customerId)
        {
            if (!allowBusinessTransfer)
                throw new BusinessOwnershipConflictException(
                    storeGuid, databaseName, primaryOwner.BusinessName);
            TransferBusinessOwnership(customerId, storeGuid, databaseName);
            return;
        }

        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT TOP 1 cb.CustomerId, c.BusinessName
FROM dbo.CustomerBusinesses cb
INNER JOIN dbo.Customers c ON c.Id=cb.CustomerId
WHERE (cb.StoreGuid=@storeGuid OR cb.DatabaseName=@database)
  AND cb.IsActive=1 AND cb.CustomerId<>@customerId", connection);
        command.Parameters.AddWithValue("@database", databaseName);
        command.Parameters.AddWithValue("@storeGuid", storeGuid);
        command.Parameters.AddWithValue("@customerId", customerId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return;

        var conflictingBusinessName = reader.GetString(1);
        reader.Close();
        if (!allowBusinessTransfer)
            throw new BusinessOwnershipConflictException(
                storeGuid, databaseName, conflictingBusinessName);
        TransferBusinessOwnership(customerId, storeGuid, databaseName);
    }

    private void TransferBusinessOwnership(int targetCustomerId, string storeGuid, string databaseName)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        using (var devices = new SqlCommand(@"
UPDATE d
SET Status='Moved', RevokedDate=SYSUTCDATETIME(),
    Notes='Store ownership moved by developer to customer ID ' + CONVERT(NVARCHAR(20), @targetCustomerId)
FROM dbo.LicenseDevices d
INNER JOIN dbo.Licenses l ON l.Id=d.LicenseId
WHERE l.CustomerId<>@targetCustomerId
  AND (l.AssignedDatabases=@storeGuid OR l.AssignedDatabases=@database)
  AND d.Status='Active'", connection, transaction))
        {
            devices.Parameters.AddWithValue("@targetCustomerId", targetCustomerId);
            devices.Parameters.AddWithValue("@storeGuid", storeGuid);
            devices.Parameters.AddWithValue("@database", databaseName);
            devices.ExecuteNonQuery();
        }

        using (var licenses = new SqlCommand(@"
UPDATE dbo.Licenses
SET IsActive=0
WHERE CustomerId<>@targetCustomerId
  AND (AssignedDatabases=@storeGuid OR AssignedDatabases=@database)", connection, transaction))
        {
            licenses.Parameters.AddWithValue("@targetCustomerId", targetCustomerId);
            licenses.Parameters.AddWithValue("@storeGuid", storeGuid);
            licenses.Parameters.AddWithValue("@database", databaseName);
            licenses.ExecuteNonQuery();
        }

        using (var businesses = new SqlCommand(@"
UPDATE dbo.CustomerBusinesses
SET IsActive=0
WHERE CustomerId<>@targetCustomerId
  AND (StoreGuid=@storeGuid OR DatabaseName=@database)", connection, transaction))
        {
            businesses.Parameters.AddWithValue("@targetCustomerId", targetCustomerId);
            businesses.Parameters.AddWithValue("@storeGuid", storeGuid);
            businesses.Parameters.AddWithValue("@database", databaseName);
            businesses.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void UpsertAdditionalBusiness(
        int customerId,
        string storeGuid,
        string databaseName,
        string businessName)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CustomerBusinesses WHERE CustomerId=@customerId AND StoreGuid=@storeGuid)
BEGIN
    INSERT dbo.CustomerBusinesses
        (CustomerId, BusinessName, StoreAddress, DatabaseName, StoreGuid, IsPrimary, IsActive, CreatedUtc)
    VALUES
        (@customerId, @business, '', @database, @storeGuid, 0, 1, SYSUTCDATETIME());
END
ELSE
BEGIN
    UPDATE dbo.CustomerBusinesses
    SET BusinessName=@business, DatabaseName=@database, StoreGuid=@storeGuid, IsActive=1
    WHERE CustomerId=@customerId AND StoreGuid=@storeGuid;
END", connection);
        command.Parameters.AddWithValue("@customerId", customerId);
        command.Parameters.AddWithValue("@database", databaseName);
        command.Parameters.AddWithValue("@storeGuid", storeGuid);
        command.Parameters.AddWithValue("@business", businessName);
        command.ExecuteNonQuery();
    }

    private void EnsurePhysicalDatabase(string storeGuid)
    {
        using var connection = new SqlConnection(ConnectionString("master"));
        connection.Open();
        using (var exists = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=@name", connection))
        {
            exists.Parameters.AddWithValue("@name", storeGuid);
            if (Convert.ToInt32(exists.ExecuteScalar()) > 0)
                return;
        }

        var quoted = storeGuid.Replace("]", "]]", StringComparison.Ordinal);
        using var create = new SqlCommand($"CREATE DATABASE [{quoted}]", connection) { CommandTimeout = 120 };
        create.ExecuteNonQuery();
    }

    private static DeviceLicenseRequestV2 RequestFromRegisteredPc(
        ClientSubscription subscription,
        IReadOnlyList<RegisteredLicensePc> devices,
        string pcId,
        string storeZip)
    {
        var pc = devices.FirstOrDefault(x => string.Equals(x.DeviceId, pcId, StringComparison.Ordinal));
        if (pc is null)
            throw new InvalidOperationException(
                "This is a new PC ID. Paste it using the COPY button on the customer's activation screen so its protected PC proof is included.");
        var parts = subscription.DatabaseName.Split('_');
        return new DeviceLicenseRequestV2
        {
            Version = 3,
            BusinessName = subscription.BusinessName,
            StoreGuid = subscription.DatabaseName,
            StoreZip = storeZip,
            StoreState = parts.Length == 4 ? parts[0] : "",
            BusinessType = parts.Length == 4 ? parts[2] : "",
            DeviceId = pc.DeviceId,
            InstallationId = pc.InstallationId,
            DeviceName = pc.DeviceName,
            DevicePublicKey = pc.DevicePublicKey,
            FingerprintHash = pc.FingerprintHash
        };
    }

    private DeviceLicensePayloadV2 BuildPayload(
        ClientSubscription subscription,
        DeviceLicenseRequestV2 request,
        int maxDevices,
        int maxBusinesses,
        DateTime expiresUtc)
    {
        var businesses = LoadApprovedBusinesses(subscription.CustomerId);
        if (businesses.Count == 0)
            throw new InvalidOperationException("The client account has no approved business database.");
        if (businesses.Count > maxBusinesses)
            throw new InvalidOperationException(
                $"This client has {businesses.Count} approved businesses. Increase the business limit before generating the key.");

        var licensedBusinesses = businesses.Select(business =>
        {
            var encrypted = EncryptConnection(business.DatabaseName, request.DevicePublicKey);
            return new LicensedBusinessPayloadV1
            {
                BusinessId = business.Id,
                BusinessName = business.BusinessName,
                Address = business.StoreAddress,
                StoreGuid = business.StoreGuid,
                DatabaseName = business.DatabaseName,
                PayrollState = StateFromStoreGuid(business.StoreGuid),
                IsPrimary = business.IsPrimary,
                EncryptedConnectionKey = encrypted.EncryptedKey,
                EncryptedConnection = encrypted.Cipher,
                ConnectionNonce = encrypted.Nonce,
                ConnectionTag = encrypted.Tag
            };
        }).ToList();
        var primary = licensedBusinesses.FirstOrDefault(x => x.IsPrimary) ?? licensedBusinesses[0];

        var primaryParts = subscription.DatabaseName.Split('_');
        var primaryState = primaryParts.Length == 4 ? primaryParts[0] : "";
        var primaryType = primaryParts.Length == 4 ? primaryParts[2] : "";
        var primaryZip = primaryParts.Length == 4 ? primaryParts[3] : "";
        var assignedPayrollState = string.IsNullOrWhiteSpace(subscription.PayrollState)
            ? primaryState
            : subscription.PayrollState.Trim().ToUpperInvariant();
        if (!string.Equals(assignedPayrollState, primaryState, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The developer-assigned Payroll State ({assignedPayrollState}) does not match the primary Store GUID state ({primaryState}). Correct the client account before issuing its license.");

        return new DeviceLicensePayloadV2
        {
            ActivationId = Guid.NewGuid().ToString("N"),
            LicenseKey = subscription.LicenseKey,
            CustomerId = subscription.CustomerId,
            LicenseId = subscription.LicenseId,
            BusinessName = subscription.BusinessName,
            StoreGuid = subscription.DatabaseName,
            StoreZip = primaryZip,
            StoreState = primaryState,
            BusinessType = primaryType,
            AppVersion = request.AppVersion,
            DeviceId = request.DeviceId,
            InstallationId = request.InstallationId,
            DeviceName = request.DeviceName,
            DevicePublicKey = request.DevicePublicKey,
            Status = "Active",
            MaxDevices = maxDevices,
            MaxStores = maxBusinesses,
            MaxUsers = subscription.MaxUsers,
            EnabledServices = subscription.EnabledServices,
            PayrollState = assignedPayrollState,
            IssuedUtc = DateTime.UtcNow.ToString("O"),
            ExpiresUtc = expiresUtc.ToString("O"),
            EncryptedConnectionKey = primary.EncryptedConnectionKey,
            EncryptedConnection = primary.EncryptedConnection,
            ConnectionNonce = primary.ConnectionNonce,
            ConnectionTag = primary.ConnectionTag,
            Businesses = licensedBusinesses
        };
    }

    private List<CustomerBusiness> LoadApprovedBusinesses(int customerId)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var command = new SqlCommand(@"
SELECT Id, BusinessName, StoreAddress, DatabaseName, StoreGuid, IsPrimary
FROM dbo.CustomerBusinesses
WHERE CustomerId=@customerId AND IsActive=1
ORDER BY IsPrimary DESC, BusinessName", connection);
        command.Parameters.AddWithValue("@customerId", customerId);
        using var reader = command.ExecuteReader();
        var businesses = new List<CustomerBusiness>();
        while (reader.Read())
            businesses.Add(new CustomerBusiness(
                reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? reader.GetString(3) : reader.GetString(4), reader.GetBoolean(5)));
        return businesses;
    }

    private EncryptedConnection EncryptConnection(string databaseName, string devicePublicKey)
    {
        var connectionPayload = new DeviceConnectionPayload
        {
            Server = _server,
            Database = databaseName,
            Username = _username,
            Password = _password
        };
        var clearConnection = JsonSerializer.SerializeToUtf8Bytes(connectionPayload, _json);
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clearConnection.Length];
        var tag = new byte[16];
        byte[] encryptedAesKey;
        try
        {
            using (var aes = new AesGcm(aesKey, tag.Length))
                aes.Encrypt(nonce, clearConnection, cipher, tag);
            using var deviceRsa = RSA.Create();
            deviceRsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(devicePublicKey), out _);
            encryptedAesKey = deviceRsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(clearConnection);
        }
        return new EncryptedConnection(
            Convert.ToBase64String(encryptedAesKey), Convert.ToBase64String(cipher),
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag));
    }

    private string BuildSignedLicense(DeviceLicensePayloadV2 payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(SigningKeyStore.Load()), out _);
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new DeviceLicenseEnvelopeV2
        {
            Version = 2,
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature)
        }, _json);
    }

    private void RegisterDeviceAndUpdateSubscription(
        ClientSubscription subscription,
        DeviceLicenseRequestV2 request,
        int maxDevices,
        int maxBusinesses,
        DateTime expiresUtc,
        string activationId,
        string activationKey,
        PcSeatChoice seatChoice)
    {
        using var connection = new SqlConnection(LicensingConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        if (seatChoice.ReleaseOtherStoreAssignments)
        {
            using var move = new SqlCommand(@"
UPDATE dbo.LicenseDevices
SET Status='Moved', RevokedDate=SYSUTCDATETIME(),
    Notes='PC registration moved to Store GUID ' + @targetStoreGuid
WHERE DeviceId=@deviceId AND LicenseId<>@targetLicenseId AND Status='Active'", connection, transaction);
            move.Parameters.AddWithValue("@targetStoreGuid", subscription.DatabaseName);
            move.Parameters.AddWithValue("@deviceId", request.DeviceId);
            move.Parameters.AddWithValue("@targetLicenseId", subscription.LicenseId);
            move.ExecuteNonQuery();
        }

        if (seatChoice.Action == PcSeatAction.ReplacePc)
        {
            using var replace = new SqlCommand(@"
UPDATE dbo.LicenseDevices
SET Status='Replaced', RevokedDate=SYSUTCDATETIME(), Notes='Replaced by PC ID ' + @newPcId
WHERE LicenseId=@licenseId AND DeviceId=@oldPcId", connection, transaction);
            replace.Parameters.AddWithValue("@newPcId", request.DeviceId);
            replace.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            replace.Parameters.AddWithValue("@oldPcId", seatChoice.ReplacedPcId ?? "");
            if (replace.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The selected existing PC could not be replaced.");
        }

        using (var count = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.LicenseDevices WITH (UPDLOCK, HOLDLOCK)
WHERE LicenseId=@licenseId AND Status='Active' AND ExpiresDate>SYSUTCDATETIME()
  AND DeviceId<>@deviceId", connection, transaction))
        {
            count.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            count.Parameters.AddWithValue("@deviceId", request.DeviceId);
            if (Convert.ToInt32(count.ExecuteScalar()) >= maxDevices)
                throw new InvalidOperationException("The paid PC seat limit would be exceeded.");
        }

        using (var update = new SqlCommand(@"
UPDATE dbo.Licenses
SET MaxDevices=@maxDevices, MaxStores=@maxStores, ExpiresDate=@expires
WHERE Id=@licenseId AND IsActive=1", connection, transaction))
        {
            update.Parameters.AddWithValue("@maxDevices", maxDevices);
            update.Parameters.AddWithValue("@maxStores", maxBusinesses);
            update.Parameters.AddWithValue("@expires", expiresUtc);
            update.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The client subscription is no longer active.");
        }

        using (var upsert = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM dbo.LicenseDevices WHERE LicenseId=@licenseId AND DeviceId=@deviceId)
BEGIN
    UPDATE dbo.LicenseDevices
    SET CustomerId=@customerId, LicenseId=@licenseId, InstallationId=@installationId,
        DeviceName=@deviceName, DevicePublicKey=@publicKey, FingerprintHash=@fingerprint,
        Status='Active', ExpiresDate=@expires, LastLicenseIssuedDate=SYSUTCDATETIME(), RevokedDate=NULL
    WHERE LicenseId=@licenseId AND DeviceId=@deviceId;
END
ELSE
BEGIN
    INSERT dbo.LicenseDevices
        (CustomerId, LicenseId, DeviceId, InstallationId, DeviceName, DevicePublicKey,
         FingerprintHash, Status, ActivatedDate, ExpiresDate, LastLicenseIssuedDate)
    VALUES
        (@customerId, @licenseId, @deviceId, @installationId, @deviceName, @publicKey,
         @fingerprint, 'Active', SYSUTCDATETIME(), @expires, SYSUTCDATETIME());
END", connection, transaction))
        {
            upsert.Parameters.AddWithValue("@customerId", subscription.CustomerId);
            upsert.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            upsert.Parameters.AddWithValue("@deviceId", request.DeviceId);
            upsert.Parameters.AddWithValue("@installationId", request.InstallationId);
            upsert.Parameters.AddWithValue("@deviceName", request.DeviceName);
            upsert.Parameters.AddWithValue("@publicKey", request.DevicePublicKey);
            upsert.Parameters.AddWithValue("@fingerprint", request.FingerprintHash);
            upsert.Parameters.AddWithValue("@expires", expiresUtc);
            upsert.ExecuteNonQuery();
        }

        using (var history = new SqlCommand(@"
INSERT dbo.DeviceLicenseIssueHistory
    (ActivationId, CustomerId, LicenseId, DeviceId, StoreGuid, BusinessName, StoreZip,
     ActivationKey, ExpiresDate, IssuedByComputer, IssueAction, ReplacedDeviceId)
VALUES
    (@activationId, @customerId, @licenseId, @deviceId, @storeGuid, @businessName, @storeZip,
     @activationKey, @expires, @issuedBy, @action, @replacedPcId)", connection, transaction))
        {
            history.Parameters.AddWithValue("@activationId", activationId);
            history.Parameters.AddWithValue("@customerId", subscription.CustomerId);
            history.Parameters.AddWithValue("@licenseId", subscription.LicenseId);
            history.Parameters.AddWithValue("@deviceId", request.DeviceId);
            history.Parameters.AddWithValue("@storeGuid", request.StoreGuid);
            history.Parameters.AddWithValue("@businessName", request.BusinessName);
            history.Parameters.AddWithValue("@storeZip", request.StoreZip);
            history.Parameters.AddWithValue("@activationKey", activationKey);
            history.Parameters.AddWithValue("@expires", expiresUtc);
            history.Parameters.AddWithValue("@issuedBy", Environment.MachineName);
            history.Parameters.AddWithValue("@action", seatChoice.Action.ToString());
            history.Parameters.AddWithValue("@replacedPcId", (object?)seatChoice.ReplacedPcId ?? DBNull.Value);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool IsActiveSeat(RegisteredLicensePc pc)
        => string.Equals(pc.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
           pc.ExpiresDate.ToUniversalTime() > DateTime.UtcNow;

    private static void ValidateVisibleFields(string storeGuid, string businessName, string zip, string pcId)
    {
        if (!StoreGuidFormat.IsValid(storeGuid))
            throw new InvalidOperationException("Store GUID must use STATE_STORENAME_BUSINESSTYPE_ZIP format.");
        if (string.IsNullOrWhiteSpace(businessName))
            throw new InvalidOperationException("Paste the Store Name.");
        if (zip.Length != 5 || !zip.All(char.IsDigit))
            throw new InvalidOperationException("Store ZIP must contain five digits.");
        var pcParts = pcId.Split('-');
        if (pcParts.Length != 5 || pcParts[0] != "HKD" ||
            pcParts.Skip(1).Any(part => part.Length != 4 || part.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidOperationException("Paste a valid HISAB KITAB PC ID.");
    }

    private static void RequireMatch(string entered, string protectedValue, string field)
    {
        if (!string.Equals(entered.Trim(), protectedValue.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The pasted {field} does not match the protected PC information.");
    }

    private string ConnectionString(string database)
        => new SqlConnectionStringBuilder
        {
            DataSource = _server,
            InitialCatalog = database,
            UserID = _username,
            Password = _password,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        }.ConnectionString;

    private static ClientSubscription ReadSubscription(SqlDataReader reader)
        => new(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? "" : reader.GetString(4),
            reader.IsDBNull(5) ? 1 : reader.GetInt32(5),
            reader.IsDBNull(6) ? DefaultMaxUsers : reader.GetInt32(6),
            reader.IsDBNull(7) ? 1 : reader.GetInt32(7),
            reader.GetDateTime(8),
            reader.IsDBNull(9) ? "Accounting" : reader.GetString(9),
            reader.IsDBNull(10) ? "" : reader.GetString(10));

    private static string GenerateUniqueSubscriptionKey(SqlConnection connection, SqlTransaction transaction)
    {
        while (true)
        {
            const string characters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(12);
            var chars = bytes.Select(value => characters[value % characters.Length]).ToArray();
            var key = $"HBL-{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
            using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.Licenses WHERE LicenseKey=@key", connection, transaction);
            check.Parameters.AddWithValue("@key", key);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                return key;
        }
    }

    private const string SchemaSql = @"
IF COL_LENGTH('dbo.Licenses', 'MaxDevices') IS NULL
    ALTER TABLE dbo.Licenses ADD MaxDevices INT NOT NULL CONSTRAINT DF_Licenses_MaxDevices DEFAULT(1);
IF COL_LENGTH('dbo.Licenses', 'EnabledServices') IS NULL
    ALTER TABLE dbo.Licenses ADD EnabledServices NVARCHAR(200) NOT NULL CONSTRAINT DF_Licenses_EnabledServices DEFAULT('Accounting');
IF COL_LENGTH('dbo.Licenses', 'PayrollState') IS NULL
    ALTER TABLE dbo.Licenses ADD PayrollState NVARCHAR(2) NOT NULL CONSTRAINT DF_Licenses_PayrollState DEFAULT('');
IF COL_LENGTH('dbo.Customers', 'StoreGuid') IS NULL
    ALTER TABLE dbo.Customers ADD StoreGuid NVARCHAR(128) NULL;
IF COL_LENGTH('dbo.Customers', 'StoreZip') IS NULL
    ALTER TABLE dbo.Customers ADD StoreZip NVARCHAR(20) NULL;
EXEC(N'
UPDATE dbo.Licenses
SET PayrollState=UPPER(LEFT(AssignedDatabases,2))
WHERE (PayrollState IS NULL OR LEN(LTRIM(RTRIM(PayrollState)))=0)
  AND AssignedDatabases LIKE ''[A-Za-z][A-Za-z][_]%'';');

IF OBJECT_ID('dbo.LicenseDevices', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LicenseDevices
    (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        DeviceId NVARCHAR(64) NOT NULL,
        InstallationId NVARCHAR(64) NOT NULL,
        DeviceName NVARCHAR(200) NOT NULL,
        DevicePublicKey NVARCHAR(MAX) NOT NULL,
        FingerprintHash NVARCHAR(128) NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        ActivatedDate DATETIME2 NOT NULL,
        ExpiresDate DATETIME2 NOT NULL,
        LastLicenseIssuedDate DATETIME2 NOT NULL,
        RevokedDate DATETIME2 NULL,
        Notes NVARCHAR(500) NULL
    );
    CREATE INDEX IX_LicenseDevices_LicenseId_Status ON dbo.LicenseDevices(LicenseId, Status);
END
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LicenseDevices') AND name='UX_LicenseDevices_DeviceId')
    DROP INDEX UX_LicenseDevices_DeviceId ON dbo.LicenseDevices;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LicenseDevices') AND name='UX_LicenseDevices_License_Device')
    CREATE UNIQUE INDEX UX_LicenseDevices_License_Device ON dbo.LicenseDevices(LicenseId, DeviceId);

IF OBJECT_ID('dbo.CustomerBusinesses', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerBusinesses
    (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerId INT NOT NULL,
        BusinessName NVARCHAR(200) NOT NULL,
        StoreAddress NVARCHAR(400) NULL,
        DatabaseName NVARCHAR(128) NOT NULL,
        StoreGuid NVARCHAR(128) NULL,
        IsPrimary BIT NOT NULL CONSTRAINT DF_CustomerBusinesses_IsPrimary DEFAULT(0),
        IsActive BIT NOT NULL CONSTRAINT DF_CustomerBusinesses_IsActive DEFAULT(1),
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_CustomerBusinesses_CreatedUtc DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX UX_CustomerBusinesses_Customer_Database ON dbo.CustomerBusinesses(CustomerId, DatabaseName);
END
IF COL_LENGTH('dbo.CustomerBusinesses', 'StoreGuid') IS NULL
    ALTER TABLE dbo.CustomerBusinesses ADD StoreGuid NVARCHAR(128) NULL;
EXEC(N'UPDATE dbo.CustomerBusinesses SET StoreGuid=DatabaseName WHERE StoreGuid IS NULL OR LEN(StoreGuid)=0');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CustomerBusinesses') AND name='UX_CustomerBusinesses_Customer_StoreGuid')
    EXEC(N'CREATE UNIQUE INDEX UX_CustomerBusinesses_Customer_StoreGuid ON dbo.CustomerBusinesses(CustomerId, StoreGuid)');

IF OBJECT_ID('dbo.DeviceLicenseIssueHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceLicenseIssueHistory
    (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ActivationId NVARCHAR(64) NOT NULL,
        CustomerId INT NOT NULL,
        LicenseId INT NOT NULL,
        DeviceId NVARCHAR(64) NOT NULL,
        StoreGuid NVARCHAR(128) NOT NULL,
        BusinessName NVARCHAR(200) NOT NULL,
        StoreZip NVARCHAR(20) NOT NULL,
        ActivationKey NVARCHAR(MAX) NOT NULL,
        IssuedUtc DATETIME2 NOT NULL CONSTRAINT DF_DeviceLicenseIssueHistory_IssuedUtc DEFAULT(SYSUTCDATETIME()),
        ExpiresDate DATETIME2 NOT NULL,
        IssuedByComputer NVARCHAR(200) NOT NULL,
        IssueAction NVARCHAR(30) NOT NULL CONSTRAINT DF_DeviceLicenseIssueHistory_IssueAction DEFAULT('Issued'),
        ReplacedDeviceId NVARCHAR(64) NULL
    );
    CREATE UNIQUE INDEX UX_DeviceLicenseIssueHistory_ActivationId ON dbo.DeviceLicenseIssueHistory(ActivationId);
END
IF COL_LENGTH('dbo.DeviceLicenseIssueHistory', 'IssueAction') IS NULL
    ALTER TABLE dbo.DeviceLicenseIssueHistory ADD IssueAction NVARCHAR(30) NOT NULL CONSTRAINT DF_DeviceLicenseIssueHistory_IssueAction_Legacy DEFAULT('Issued');
IF COL_LENGTH('dbo.DeviceLicenseIssueHistory', 'ReplacedDeviceId') IS NULL
    ALTER TABLE dbo.DeviceLicenseIssueHistory ADD ReplacedDeviceId NVARCHAR(64) NULL;";

    private sealed record CustomerBusiness(
        int Id,
        string BusinessName,
        string StoreAddress,
        string DatabaseName,
        string StoreGuid,
        bool IsPrimary);
    private sealed record EncryptedConnection(string EncryptedKey, string Cipher, string Nonce, string Tag);

    private static string StateFromStoreGuid(string storeGuid)
    {
        var parts = (storeGuid ?? "").Trim().ToUpperInvariant().Split('_');
        if (parts.Length != 4 || parts[0].Length != 2)
            throw new InvalidOperationException("Store GUID must begin with its two-letter payroll state.");
        return parts[0];
    }
}

internal sealed record ClientSubscription(
    int CustomerId,
    int LicenseId,
    string BusinessName,
    string LicenseKey,
    string DatabaseName,
    int MaxStores,
    int MaxUsers,
    int MaxDevices,
    DateTime ExpiresDate,
    string EnabledServices,
    string PayrollState);

internal sealed record RegisteredLicensePc(
    int Id,
    string DeviceId,
    string InstallationId,
    string DeviceName,
    string DevicePublicKey,
    string FingerprintHash,
    string Status,
    DateTime ActivatedDate,
    DateTime ExpiresDate,
    DateTime LastIssuedDate);

internal sealed record ActivationPreparation(
    ClientSubscription Subscription,
    DeviceLicenseRequestV2 Request,
    IReadOnlyList<RegisteredLicensePc> Devices,
    IReadOnlyList<OtherStorePcAssignment> OtherStoreAssignments,
    bool CreatedStore,
    bool AddedBusiness);

internal sealed record OtherStorePcAssignment(
    int LicenseId,
    string StoreGuid,
    string BusinessName,
    string DeviceName,
    string Status,
    DateTime ExpiresDate);

internal sealed record IssuedActivation(
    string FormattedLicense,
    string LicenseJson,
    string DisplayLicenseKey,
    DeviceLicensePayloadV2 Payload,
    int MaxDevices,
    int MaxBusinesses,
    string ResultMessage,
    IReadOnlyList<RegisteredLicensePc> Devices);

internal sealed class BusinessOwnershipConflictException : InvalidOperationException
{
    public BusinessOwnershipConflictException(
        string storeGuid,
        string databaseName,
        string currentClientName)
        : base($"Store GUID {storeGuid} or SQL database {databaseName} is already licensed to '{currentClientName}'.")
    {
        StoreGuid = storeGuid;
        DatabaseName = databaseName;
        CurrentClientName = currentClientName;
    }

    public string StoreGuid { get; }
    public string DatabaseName { get; }
    public string CurrentClientName { get; }
}

internal sealed class BusinessLimitRequiredException : InvalidOperationException
{
    public BusinessLimitRequiredException(int requiredBusinessCount)
        : base($"This updated license needs {requiredBusinessCount} business slots.")
        => RequiredBusinessCount = requiredBusinessCount;

    public int RequiredBusinessCount { get; }
}
