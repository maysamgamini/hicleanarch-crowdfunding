using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

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

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "notifications",
                columns: table => new
                {
                    handler = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.handler, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => new { x.user_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sent_notifications",
                schema: "notifications",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sent_notifications", x => new { x.event_id, x.recipient_id });
                });

            migrationBuilder.CreateIndex(
                name: "outbox_undelivered",
                schema: "notifications",
                table: "outbox",
                column: "occurred_at",
                filter: "delivered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_backers",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notification_preferences",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "sent_notifications",
                schema: "notifications");
        }
    }
}
