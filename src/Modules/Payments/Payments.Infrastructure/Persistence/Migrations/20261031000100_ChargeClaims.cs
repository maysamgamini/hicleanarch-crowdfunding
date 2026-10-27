using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChargeClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_until",
                schema: "payments",
                table: "settlement_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claimed_by",
                schema: "payments",
                table: "charges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_until",
                schema: "payments",
                table: "charges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges",
                columns: new[] { "campaign_id", "created_at" },
                filter: "status IN ('TO_CHARGE', 'CHARGING', 'PAYMENT_FAILED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "paused_until",
                schema: "payments",
                table: "settlement_jobs");

            migrationBuilder.DropColumn(
                name: "claimed_by",
                schema: "payments",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "claimed_until",
                schema: "payments",
                table: "charges");

            migrationBuilder.CreateIndex(
                name: "charges_open",
                schema: "payments",
                table: "charges",
                column: "campaign_id",
                filter: "status IN ('TO_CHARGE', 'CHARGING', 'PAYMENT_FAILED')");
        }
    }
}
