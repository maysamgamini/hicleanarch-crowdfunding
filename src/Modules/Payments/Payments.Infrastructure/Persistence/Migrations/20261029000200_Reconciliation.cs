using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reconciled_at",
                schema: "payments",
                table: "payouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reconciliation_differences",
                schema: "payments",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ours_minor = table.Column<long>(type: "bigint", nullable: true),
                    theirs_minor = table.Column<long>(type: "bigint", nullable: true),
                    found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_differences", x => new { x.campaign_id, x.source_ref, x.kind });
                });

            migrationBuilder.CreateIndex(
                name: "payouts_open",
                schema: "payments",
                table: "payouts",
                column: "settled_at",
                filter: "status <> 'PAID_OUT'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_differences",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "payouts_open",
                schema: "payments",
                table: "payouts");

            migrationBuilder.DropColumn(
                name: "reconciled_at",
                schema: "payments",
                table: "payouts");
        }
    }
}
