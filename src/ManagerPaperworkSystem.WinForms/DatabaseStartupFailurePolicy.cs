using Microsoft.Data.SqlClient;

namespace ManagerPaperworkSystem.WinForms;

internal static class DatabaseStartupFailurePolicy
{
    public static bool IsClosedPeriod(Exception exception)
    {
        if (exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(x => x.Number == 51120)) return true;
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsClosedPeriod)) return true;
        return exception.InnerException is not null && IsClosedPeriod(exception.InnerException);
    }
}
