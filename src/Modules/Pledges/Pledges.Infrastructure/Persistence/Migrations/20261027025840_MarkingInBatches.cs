using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarkingInBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "marked_through",
                schema: "pledges",
                table: "funding_windows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "marking_finished_at",
                schema: "pledges",
                table: "funding_windows",
                type: "timestamp with time zone",
                nullable: true);

            // Windows decided before this migration had every pledge marked in their decision.
            migrationBuilder.Sql("""
                UPDATE pledges.funding_windows SET marking_finished_at = coalesce(ended_at, now())
                WHERE status IN ('FUNDED', 'UNSUCCESSFUL');
                """);

            migrationBuilder.CreateIndex(
                name: "pledges_active_by_campaign",
                schema: "pledges",
                table: "pledges",
                columns: new[] { "campaign_id", "id" },
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "funding_windows_marking",
                schema: "pledges",
                table: "funding_windows",
                column: "deadline",
                filter: "status = 'FUNDED' AND marking_finished_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "pledges_active_by_campaign",
                schema: "pledges",
                table: "pledges");

            migrationBuilder.DropIndex(
                name: "funding_windows_marking",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "marked_through",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "marking_finished_at",
                schema: "pledges",
                table: "funding_windows");
        }
    }
}
