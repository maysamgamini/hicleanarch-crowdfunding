using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "payments",
                table: "outbox",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "payments",
                table: "outbox");
        }
    }
}
