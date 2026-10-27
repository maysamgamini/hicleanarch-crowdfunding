using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimeFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "decided_at",
                schema: "pledges",
                table: "funding_windows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "recheck_minor",
                schema: "pledges",
                table: "funding_windows",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rechecked_at",
                schema: "pledges",
                table: "funding_windows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "funding_windows_to_recheck",
                schema: "pledges",
                table: "funding_windows",
                column: "decided_at",
                filter: "rechecked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "funding_windows_to_recheck",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "recheck_minor",
                schema: "pledges",
                table: "funding_windows");

            migrationBuilder.DropColumn(
                name: "rechecked_at",
                schema: "pledges",
                table: "funding_windows");
        }
    }
}
