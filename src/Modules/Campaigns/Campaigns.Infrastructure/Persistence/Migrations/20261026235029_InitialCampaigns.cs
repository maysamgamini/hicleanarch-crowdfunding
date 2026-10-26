using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Campaigns.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "campaigns");

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    goal_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tier_definitions",
                schema: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    min_pledge_minor = table.Column<long>(type: "bigint", nullable: false),
                    limit = table.Column<int>(type: "integer", nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tier_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_tier_definitions_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalSchema: "campaigns",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tier_definitions_campaign_id",
                schema: "campaigns",
                table: "tier_definitions",
                column: "campaign_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tier_definitions",
                schema: "campaigns");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "campaigns");
        }
    }
}
