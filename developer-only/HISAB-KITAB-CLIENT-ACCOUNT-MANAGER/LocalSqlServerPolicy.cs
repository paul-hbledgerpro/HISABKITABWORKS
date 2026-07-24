using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal static class LocalSqlServerPolicy
{
    public const string DefaultInstance = @".\SQLEXPRESS";
    public const string ClientStoreInstance = @".\SQLEXPRESS";
    public const string MasterDatabase = "master";

    public static bool IsLocal(string? server)
    {
        var value = (server ?? string.Empty).Trim();
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

    public static string BuildConnectionString(string server, string database, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("Enter the shared licensing SQL Server.");
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("The database name is required.");

        var local = IsLocal(server);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            InitialCatalog = database.Trim(),
            Encrypt = !local,
            TrustServerCertificate = local,
            ConnectTimeout = 30
        };

        // Local SQL Express always authenticates with the Windows account
        // running this developer tool. Ignore stale SQL credentials locally.
        if (local || string.IsNullOrWhiteSpace(username))
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = username.Trim();
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    public static void EnsureDatabaseExists(string server, string database, string username, string password)
    {
        if (!IsLocal(server))
        {
            try
            {
                using var remote = new SqlConnection(BuildConnectionString(server, database, username, password));
                remote.Open();
                return;
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"Could not open the shared licensing database '{database}'. " +
                    "Import or create it on the selected SQL service first, then try again.",
                    ex);
            }
        }

        using var connection = new SqlConnection(BuildConnectionString(server, MasterDatabase, username, password));
        connection.Open();
        using var command = new SqlCommand(
            "IF DB_ID(@database) IS NULL EXEC(N'CREATE DATABASE ' + QUOTENAME(@database))",
            connection);
        command.Parameters.AddWithValue("@database", database);
        command.ExecuteNonQuery();
    }
}
