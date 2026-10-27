using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Campaigns.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseUnsuccessful : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pledges released every unsuccessful campaign's pledges in its decision's transaction, so
            // every campaign the catalog shows UNSUCCESSFUL is already released.
            migrationBuilder.Sql("UPDATE campaigns.campaigns SET status = 'RELEASED' WHERE status = 'UNSUCCESSFUL'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE campaigns.campaigns SET status = 'UNSUCCESSFUL' WHERE status = 'RELEASED'");
        }
    }
}
