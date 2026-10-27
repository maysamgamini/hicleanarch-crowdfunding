using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivery_claimed_until",
                schema: "pledges",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publish_claimed_until",
                schema: "pledges",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "outbox_unpublished",
                schema: "pledges",
                table: "outbox",
                column: "occurred_at",
                filter: "published_at IS NULL AND 'broker' = ANY (sinks)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "outbox_unpublished",
                schema: "pledges",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "delivery_claimed_until",
                schema: "pledges",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "publish_claimed_until",
                schema: "pledges",
                table: "outbox");
        }
    }
}
