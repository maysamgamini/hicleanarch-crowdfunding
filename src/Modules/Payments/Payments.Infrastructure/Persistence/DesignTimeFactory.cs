using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Payments.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(PaymentsDbOptions.Configure(new DbContextOptionsBuilder<PaymentsDbContext>(), "Host=localhost").Options);
}

internal static class PaymentsDbOptions
{
    public static DbContextOptionsBuilder<PaymentsDbContext> Configure(DbContextOptionsBuilder<PaymentsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", PaymentsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}
