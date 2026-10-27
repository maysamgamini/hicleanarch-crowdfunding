using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldsDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "pledges_hold_due",
                schema: "pledges",
                table: "pledges",
                column: "hold_expires_at",
                filter: "status = 'PENDING_SETUP'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "pledges_hold_due",
                schema: "pledges",
                table: "pledges");
        }
    }
}
