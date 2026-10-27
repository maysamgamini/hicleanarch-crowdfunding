using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PledgesAndLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pledge_ledger",
                schema: "pledges",
                columns: table => new
                {
                    entry_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pledge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cause_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    delta_minor = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pledge_ledger", x => x.entry_id);
                    table.CheckConstraint("pledge_ledger_kind", "kind IN ('ACTIVATE', 'CHANGE', 'CANCEL')");
                    table.CheckConstraint("pledge_ledger_sign", "(kind = 'ACTIVATE' AND delta_minor > 0) OR (kind = 'CANCEL' AND delta_minor < 0) OR (kind = 'CHANGE' AND delta_minor <> 0)");
                });

            migrationBuilder.CreateTable(
                name: "pledges",
                schema: "pledges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    backer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_method_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pledges", x => x.id);
                    table.CheckConstraint("pledges_amount_positive", "amount_minor > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_pledge_ledger_campaign_id",
                schema: "pledges",
                table: "pledge_ledger",
                column: "campaign_id")
                .Annotation("Npgsql:IndexInclude", new[] { "delta_minor" });

            migrationBuilder.CreateIndex(
                name: "ix_pledge_ledger_cause_id_kind",
                schema: "pledges",
                table: "pledge_ledger",
                columns: new[] { "cause_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "one_activation_per_pledge",
                schema: "pledges",
                table: "pledge_ledger",
                column: "pledge_id",
                unique: true,
                filter: "kind = 'ACTIVATE'");

            migrationBuilder.CreateIndex(
                name: "ix_pledges_campaign_id",
                schema: "pledges",
                table: "pledges",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "one_live_pledge_per_backer",
                schema: "pledges",
                table: "pledges",
                columns: new[] { "campaign_id", "backer_id" },
                unique: true,
                filter: "status IN ('PENDING_SETUP', 'ACTIVE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pledge_ledger",
                schema: "pledges");

            migrationBuilder.DropTable(
                name: "pledges",
                schema: "pledges");
        }
    }
}
