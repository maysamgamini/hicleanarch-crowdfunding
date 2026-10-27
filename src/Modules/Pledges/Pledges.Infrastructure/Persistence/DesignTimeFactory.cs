using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pledges.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PledgesDbContext>
{
    public PledgesDbContext CreateDbContext(string[] args) =>
        new(PledgesDbOptions.Configure(new DbContextOptionsBuilder<PledgesDbContext>(), "Host=localhost").Options);
}

internal static class PledgesDbOptions
{
    public static DbContextOptionsBuilder<PledgesDbContext> Configure(DbContextOptionsBuilder<PledgesDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", PledgesDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}
