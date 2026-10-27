using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseCancelledPledges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "funding_windows_to_release",
                schema: "pledges",
                table: "funding_windows",
                column: "deadline",
                filter: "status = 'CANCELLED' AND marking_finished_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "funding_windows_to_release",
                schema: "pledges",
                table: "funding_windows");
        }
    }
}
