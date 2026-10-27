using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class PledgeDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "campaign_pledges_by_activation",
                schema: "notifications",
                table: "campaign_pledges",
                column: "activated_at",
                filter: "activated_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "campaign_pledges_by_cancel",
                schema: "notifications",
                table: "campaign_pledges",
                column: "cancelled_at",
                filter: "cancelled_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "campaign_pledges_by_activation",
                schema: "notifications",
                table: "campaign_pledges");

            migrationBuilder.DropIndex(
                name: "campaign_pledges_by_cancel",
                schema: "notifications",
                table: "campaign_pledges");
        }
    }
}
