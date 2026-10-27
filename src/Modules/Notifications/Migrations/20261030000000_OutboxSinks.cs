using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifications.Migrations
{
    /// <inheritdoc />
    public partial class OutboxSinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "outbox_undelivered",
                schema: "notifications",
                table: "outbox");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "notifications",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "sinks",
                schema: "notifications",
                table: "outbox",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "outbox_undelivered",
                schema: "notifications",
                table: "outbox",
                column: "occurred_at",
                filter: "delivered_at IS NULL AND (sinks IS NULL OR cardinality(array_remove(sinks, 'broker')) > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "outbox_undelivered",
                schema: "notifications",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "notifications",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "sinks",
                schema: "notifications",
                table: "outbox");

            migrationBuilder.CreateIndex(
                name: "outbox_undelivered",
                schema: "notifications",
                table: "outbox",
                column: "occurred_at",
                filter: "delivered_at IS NULL");
        }
    }
}
