using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class CampaignPledges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaign_pledges",
                schema: "notifications",
                columns: table => new
                {
                    pledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_pledges", x => x.pledge_id);
                });

            migrationBuilder.CreateIndex(
                name: "campaign_pledges_live",
                schema: "notifications",
                table: "campaign_pledges",
                column: "campaign_id",
                filter: "activated_at IS NOT NULL AND cancelled_at IS NULL AND released_at IS NULL");

            // The old copy knew backers, not pledges: each live row becomes a pledge with an id of its
            // own. A later cancel of such a pledge cannot find it, so its backer stays told until the
            // campaign ends; every pledge made from now on is exact.
            migrationBuilder.Sql("""
                INSERT INTO notifications.campaign_pledges (pledge_id, campaign_id, backer_id, activated_at)
                SELECT gen_random_uuid(), campaign_id, backer_id, now() FROM notifications.campaign_backers
                """);

            migrationBuilder.DropTable(
                name: "campaign_backers",
                schema: "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_pledges",
                schema: "notifications");

            migrationBuilder.CreateTable(
                name: "campaign_backers",
                schema: "notifications",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_backers", x => new { x.campaign_id, x.backer_id });
                });
        }
    }
}
