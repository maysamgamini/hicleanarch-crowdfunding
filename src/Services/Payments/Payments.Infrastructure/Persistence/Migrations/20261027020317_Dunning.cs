using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropCheckConstraint(
                name: "charge_attempts_no",
                schema: "payments",
                table: "charge_attempts");

            migrationBuilder.AddColumn<string>(
                name: "last_decline_code",
                schema: "payments",
                table: "charges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payment_failed_at",
                schema: "payments",
                table: "charges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "on_session",
                schema: "payments",
                table: "charge_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "payments",
                columns: table => new
                {
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    reply_status = table.Column<int>(type: "integer", nullable: true),
                    reply_body = table.Column<string>(type: "json", nullable: true),
                    reply_location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.caller_id, x.key });
                });

            // Round 1's final failure is Round 2's dropped charge.
            migrationBuilder.Sql("UPDATE payments.charges SET status = 'DROPPED' WHERE status = 'FAILED';");

            migrationBuilder.CreateIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges",
                column: "campaign_id",
                filter: "status IN ('TO_CHARGE', 'CHARGING', 'PAYMENT_FAILED')");

            migrationBuilder.CreateIndex(
                name: "charge_attempts_by_payment",
                schema: "payments",
                table: "charge_attempts",
                column: "psp_payment_id",
                filter: "psp_payment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "charge_attempts_no",
                schema: "payments",
                table: "charge_attempts",
                sql: "attempt_no BETWEEN 1 AND 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropIndex(
                name: "charge_attempts_by_payment",
                schema: "payments",
                table: "charge_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "charge_attempts_no",
                schema: "payments",
                table: "charge_attempts");

            migrationBuilder.DropColumn(
                name: "last_decline_code",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "payment_failed_at",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "on_session",
                schema: "payments",
                table: "charge_attempts");

            migrationBuilder.CreateIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges",
                column: "campaign_id",
                filter: "status IN ('TO_CHARGE', 'CHARGING')");

            migrationBuilder.AddCheckConstraint(
                name: "charge_attempts_no",
                schema: "payments",
                table: "charge_attempts",
                sql: "attempt_no BETWEEN 1 AND 3");
        }
    }
}
