using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "card_setups",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    psp_setup_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_method_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_setups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "payments",
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
                name: "outbox",
                schema: "payments",
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
                name: "psp_customers",
                schema: "payments",
                columns: table => new
                {
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    psp_customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_psp_customers", x => x.backer_id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_inbox",
                schema: "payments",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_inbox", x => x.message_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_setups_pledge_id",
                schema: "payments",
                table: "card_setups",
                column: "pledge_id");

            migrationBuilder.CreateIndex(
                name: "ix_card_setups_psp_setup_id",
                schema: "payments",
                table: "card_setups",
                column: "psp_setup_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "outbox_undelivered",
                schema: "payments",
                table: "outbox",
                column: "occurred_at",
                filter: "delivered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_psp_customers_psp_customer_id",
                schema: "payments",
                table: "psp_customers",
                column: "psp_customer_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_setups",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "psp_customers",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "webhook_inbox",
                schema: "payments");
        }
    }
}
