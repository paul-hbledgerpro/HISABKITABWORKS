using Microsoft.Data.SqlClient;

namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal static class LocalSqlServerPolicy
{
    public const string DefaultInstance = @".\SQLEXPRESS";
    public const string MasterDatabase = "master";

    public static void RequireLocal(string? server)
    {
        if (IsLocal(server))
            return;

        throw new InvalidOperationException(
            "Remote and cloud SQL databases are disabled. " +
            $@"Connect to the free local SQL Server instance '{DefaultInstance}'.");
    }

    private static bool IsLocal(string? server)
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
        RequireLocal(server);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            InitialCatalog = database,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        };

        if (string.IsNullOrWhiteSpace(username))
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
        RequireLocal(server);
        using var connection = new SqlConnection(BuildConnectionString(server, MasterDatabase, username, password));
        connection.Open();
        using var command = new SqlCommand(
            "IF DB_ID(@database) IS NULL EXEC(N'CREATE DATABASE ' + QUOTENAME(@database))",
            connection);
        command.Parameters.AddWithValue("@database", database);
        command.ExecuteNonQuery();
    }
}
