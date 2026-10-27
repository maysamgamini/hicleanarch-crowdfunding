using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Campaigns.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CampaignsDbContext>
{
    public CampaignsDbContext CreateDbContext(string[] args) =>
        new(CampaignsDbOptions.Configure(new DbContextOptionsBuilder<CampaignsDbContext>(), "Host=localhost").Options);
}

internal static class CampaignsDbOptions
{
    public static DbContextOptionsBuilder<CampaignsDbContext> Configure(DbContextOptionsBuilder<CampaignsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", CampaignsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}
