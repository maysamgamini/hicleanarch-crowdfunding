using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Campaigns.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublishAndIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Statuses are stored as the API shows them: DRAFT, LIVE.
            migrationBuilder.Sql("UPDATE campaigns.campaigns SET status = upper(status);");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "campaigns",
                table: "campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "campaigns",
                columns: table => new
                {
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    reply_status = table.Column<int>(type: "integer", nullable: true),
                    reply_body = table.Column<string>(type: "json", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.caller_id, x.key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "campaigns");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "campaigns",
                table: "campaigns");
        }
    }
}
