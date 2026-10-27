using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundingWindowCreator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "creator_id",
                schema: "pledges",
                table: "funding_windows",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "creator_id",
                schema: "pledges",
                table: "funding_windows");
        }
    }
}
