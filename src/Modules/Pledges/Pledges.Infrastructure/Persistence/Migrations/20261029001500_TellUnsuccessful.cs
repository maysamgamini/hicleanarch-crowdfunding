using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TellUnsuccessful : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "told_finished_at",
                schema: "pledges",
                table: "funding_windows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "told_through",
                schema: "pledges",
                table: "funding_windows",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "funding_windows_telling",
                schema: "pledges",
                table: "funding_windows",
                column: "deadline",
                filter: "status = 'UNSUCCESSFUL' AND told_finished_at IS NULL");

            // Campaigns already decided unsuccessful had their backers told by CampaignFundingDecided.
            migrationBuilder.Sql("UPDATE pledges.funding_windows SET told_finished_at = now() WHERE status = 'UNSUCCESSFUL'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "funding_windows_telling",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "told_finished_at",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "told_through",
                schema: "pledges",
                table: "funding_windows");
        }
    }
}
