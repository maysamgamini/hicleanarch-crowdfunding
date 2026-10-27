using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Settlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "charge_attempts",
                schema: "payments",
                columns: table => new
                {
                    pledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    psp_payment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    decline_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_attempts", x => new { x.pledge_id, x.attempt_no });
                    table.CheckConstraint("charge_attempts_no", "attempt_no BETWEEN 1 AND 3");
                    table.CheckConstraint("charge_attempts_status", "status IN ('IN_FLIGHT', 'SUCCEEDED', 'FAILED')");
                });

            migrationBuilder.CreateTable(
                name: "charges",
                schema: "payments",
                columns: table => new
                {
                    pledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    payment_method_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    psp_payment_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charges", x => x.pledge_id);
                    table.CheckConstraint("charges_amount_positive", "amount_minor > 0");
                });

            migrationBuilder.CreateTable(
                name: "settlement_jobs",
                schema: "payments",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expected_charges = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_jobs", x => x.campaign_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_charge_attempts_idempotency_key",
                schema: "payments",
                table: "charge_attempts",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "one_success_per_pledge",
                schema: "payments",
                table: "charge_attempts",
                column: "pledge_id",
                unique: true,
                filter: "status = 'SUCCEEDED'");

            migrationBuilder.CreateIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges",
                column: "campaign_id",
                filter: "status IN ('TO_CHARGE', 'CHARGING')");

            migrationBuilder.CreateIndex(
                name: "settlement_jobs_open",
                schema: "payments",
                table: "settlement_jobs",
                column: "created_at",
                filter: "status <> 'DONE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charge_attempts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "charges",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "settlement_jobs",
                schema: "payments");
        }
    }
}
