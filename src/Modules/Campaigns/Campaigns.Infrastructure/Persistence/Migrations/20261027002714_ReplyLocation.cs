using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Campaigns.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplyLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reply_location",
                schema: "campaigns",
                table: "idempotency_keys",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reply_location",
                schema: "campaigns",
                table: "idempotency_keys");
        }
    }
}
