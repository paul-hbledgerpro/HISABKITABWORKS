using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace ManagerPaperworkSystem.WinForms;

/// <summary>
/// Prevents the customer application from opening a paid cloud/remote SQL database.
/// HISAB KITAB is intentionally limited to a SQL Server instance on this PC.
/// </summary>
internal static class LocalSqlServerPolicy
{
    public const string DefaultInstance = @".\SQLEXPRESS";
    private const string MasterDatabase = "master";

    public static void RequireLocalConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The local SQL Server connection is missing.");

        var builder = new SqlConnectionStringBuilder(connectionString);
        RequireLocalServer(builder.DataSource);
    }

    public static void RequireLocalServer(string? server)
    {
        if (IsLocalServer(server))
            return;

        throw new InvalidOperationException(
            "Remote and cloud SQL databases are disabled in HISAB KITAB. " +
            $@"Use the free local SQL Server instance '{DefaultInstance}' on this computer.");
    }

    public static bool IsLocalConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            return IsLocalServer(new SqlConnectionStringBuilder(connectionString).DataSource);
        }
        catch
        {
            return false;
        }
    }

    public static DatabaseConnectionSettings Normalize(DatabaseConnectionSettings settings)
    {
        if (!string.Equals(settings.DatabaseType, "SqlServer", StringComparison.OrdinalIgnoreCase))
            return settings;

        var database = settings.Database?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(database) && !string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            try
            {
                database = new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog.Trim();
            }
            catch
            {
                // BuildConnectionString will give the user a clear missing-database message.
            }
        }

        return new DatabaseConnectionSettings
        {
            DatabaseType = "SqlServer",
            Server = DefaultInstance,
            Database = database,
            Username = string.Empty,
            Password = string.Empty,
            ConnectionString = string.Empty
        };
    }

    public static void MarkMigrationPendingIfRemote(DatabaseConnectionSettings settings)
    {
        if (!string.Equals(settings.DatabaseType, "SqlServer", StringComparison.OrdinalIgnoreCase))
            return;

        var database = settings.Database?.Trim() ?? string.Empty;
        var server = settings.Server?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(settings.ConnectionString);
                if (string.IsNullOrWhiteSpace(database))
                    database = builder.InitialCatalog.Trim();
                if (string.IsNullOrWhiteSpace(server))
                    server = builder.DataSource.Trim();
            }
            catch
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(database) &&
            !string.IsNullOrWhiteSpace(server) &&
            !IsLocalServer(server))
        {
            WriteMigrationMarker(database);
        }
    }

    public static void MarkMigrationPendingIfRemote(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(builder.InitialCatalog) &&
                !IsLocalServer(builder.DataSource))
            {
                WriteMigrationMarker(builder.InitialCatalog);
            }
        }
        catch
        {
            // Invalid legacy entries are handled by the caller's normal validation.
        }
    }

    public static string BuildConnectionString(string database)
    {
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The licensed local database name is missing.");

        return new SqlConnectionStringBuilder
        {
            DataSource = DefaultInstance,
            InitialCatalog = database.Trim(),
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            ConnectRetryCount = 2,
            ConnectRetryInterval = 2
        }.ConnectionString;
    }

    public static string NormalizeConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The store database connection is missing.");

        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        return BuildConnectionString(database);
    }

    public static async Task EnsureDatabaseExistsAsync(string connectionString)
    {
        var requested = new SqlConnectionStringBuilder(connectionString);
        var database = requested.InitialCatalog?.Trim();
        if (string.IsNullOrWhiteSpace(database) ||
            database.Equals(MasterDatabase, StringComparison.OrdinalIgnoreCase))
            return;

        RequireLocalServer(requested.DataSource);
        var master = new SqlConnectionStringBuilder(requested.ConnectionString)
        {
            InitialCatalog = MasterDatabase
        };

        try
        {
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var exists = new SqlCommand("SELECT DB_ID(@database)", connection);
            exists.Parameters.AddWithValue("@database", database);
            if (await exists.ExecuteScalarAsync() is not DBNull and not null)
            {
                DeleteMigrationMarker(database);
                return;
            }

            if (File.Exists(GetMigrationMarkerPath(database)))
            {
                throw new InvalidOperationException(
                    $"The recovered data for '{database}' has not been imported to local SQL Server yet. " +
                    $@"Import that database into '{DefaultInstance}' using the same database name before opening this store. " +
                    "HISAB KITAB did not create an empty replacement database.");
            }

            var quoted = database.Replace("]", "]]", StringComparison.Ordinal);
            await using var create = new SqlCommand($"CREATE DATABASE [{quoted}]", connection)
            {
                CommandTimeout = 120
            };
            await create.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $@"HISAB KITAB could not open the free local SQL Server instance '{DefaultInstance}'. " +
                "Install/start SQL Server Express and then reopen the application.",
                ex);
        }
    }

    public static bool IsLocalServer(string? server)
    {
        var value = (server ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
            value = value[3..];

        var host = value.Split('\\', 2)[0].Split(',', 2)[0].Trim();
        return host.Equals(".", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("(local)", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("(localdb)", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMigrationMarker(string database)
    {
        var path = GetMigrationMarkerPath(database);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, database.Trim(), Encoding.UTF8);
    }

    private static void DeleteMigrationMarker(string database)
    {
        var path = GetMigrationMarkerPath(database);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string GetMigrationMarkerPath(string database)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.Trim().ToUpperInvariant())));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hisab Kitab",
            "LocalDatabaseMigration",
            $"{hash}.pending");
    }
}
