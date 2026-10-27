using Npgsql;

namespace Payments.Infrastructure.Cutover;

internal static class PaymentsDatabase
{
    /// <summary>
    /// Whether a host's owner connection is the one that migrates Payments' schema: the same server
    /// and database as Payments' own connection. No owner connection (a running task) migrates nothing.
    /// </summary>
    public static bool IsMigratedBy(string? ownerConnectionString, string paymentsConnectionString) =>
        string.IsNullOrEmpty(ownerConnectionString) || SameDatabase(ownerConnectionString, paymentsConnectionString);

    public static bool SameDatabase(string a, string b)
    {
        var x = new NpgsqlConnectionStringBuilder(a);
        var y = new NpgsqlConnectionStringBuilder(b);
        return string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port && x.Database == y.Database;
    }
}
