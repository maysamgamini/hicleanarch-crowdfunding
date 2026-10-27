using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneCardSetupPerPledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before setups were idempotent, a retried request could leave several rows for a pledge.
            // One is kept: the completed one if there is one, else the newest.
            migrationBuilder.Sql("""
                DELETE FROM payments.card_setups s
                USING (
                    SELECT id, row_number() OVER (
                        PARTITION BY pledge_id ORDER BY (status = 'SUCCEEDED') DESC, created_at DESC, id DESC) AS n
                    FROM payments.card_setups) ranked
                WHERE s.id = ranked.id AND ranked.n > 1;
                """);

            migrationBuilder.DropIndex(
                name: "ix_card_setups_pledge_id",
                schema: "payments",
                table: "card_setups");

            migrationBuilder.CreateIndex(
                name: "ix_card_setups_pledge_id",
                schema: "payments",
                table: "card_setups",
                column: "pledge_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_card_setups_pledge_id",
                schema: "payments",
                table: "card_setups");

            migrationBuilder.CreateIndex(
                name: "ix_card_setups_pledge_id",
                schema: "payments",
                table: "card_setups",
                column: "pledge_id");
        }
    }
}
