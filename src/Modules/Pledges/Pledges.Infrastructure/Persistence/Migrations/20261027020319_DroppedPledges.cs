using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DroppedPledges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "pledge_ledger_kind",
                schema: "pledges",
                table: "pledge_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "pledge_ledger_sign",
                schema: "pledges",
                table: "pledge_ledger");

            // Round 1's failed pledge is Round 2's dropped one. No DROP entries are written for them:
            // Round 1 never took a failed pledge out of the total.
            migrationBuilder.Sql("UPDATE pledges.pledges SET status = 'DROPPED' WHERE status = 'FAILED';");

            migrationBuilder.AddCheckConstraint(
                name: "pledge_ledger_kind",
                schema: "pledges",
                table: "pledge_ledger",
                sql: "kind IN ('ACTIVATE', 'CHANGE', 'CANCEL', 'DROP')");

            migrationBuilder.AddCheckConstraint(
                name: "pledge_ledger_sign",
                schema: "pledges",
                table: "pledge_ledger",
                sql: "(kind = 'ACTIVATE' AND delta_minor > 0) OR (kind IN ('CANCEL', 'DROP') AND delta_minor < 0) OR (kind = 'CHANGE' AND delta_minor <> 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "pledge_ledger_kind",
                schema: "pledges",
                table: "pledge_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "pledge_ledger_sign",
                schema: "pledges",
                table: "pledge_ledger");

            migrationBuilder.AddCheckConstraint(
                name: "pledge_ledger_kind",
                schema: "pledges",
                table: "pledge_ledger",
                sql: "kind IN ('ACTIVATE', 'CHANGE', 'CANCEL')");

            migrationBuilder.AddCheckConstraint(
                name: "pledge_ledger_sign",
                schema: "pledges",
                table: "pledge_ledger",
                sql: "(kind = 'ACTIVATE' AND delta_minor > 0) OR (kind = 'CANCEL' AND delta_minor < 0) OR (kind = 'CHANGE' AND delta_minor <> 0)");
        }
    }
}
