using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class OutgoingEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outgoing_emails",
                schema: "notifications",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "PENDING"),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outgoing_emails", x => new { x.event_id, x.recipient_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "recipient_addresses",
                schema: "notifications",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    looked_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipient_addresses", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "outgoing_emails_due",
                schema: "notifications",
                table: "outgoing_emails",
                column: "next_attempt_at",
                filter: "status = 'PENDING'");

            // Emails sent before the queue keep their rows, so a redelivered event still records nothing.
            migrationBuilder.Sql("""
                INSERT INTO notifications.outgoing_emails (event_id, recipient_id, kind, subject, body, status, attempts, created_at, sent_at)
                SELECT event_id, recipient_id, kind, '', '', 'SENT', 1, sent_at, sent_at FROM notifications.sent_notifications
                """);

            migrationBuilder.DropTable(
                name: "sent_notifications",
                schema: "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outgoing_emails",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "recipient_addresses",
                schema: "notifications");

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
        }
    }
}
