using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPledges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pledges");

            migrationBuilder.CreateTable(
                name: "funding_windows",
                schema: "pledges",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    goal_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    pledged_minor = table.Column<long>(type: "bigint", nullable: false),
                    backers = table.Column<int>(type: "integer", nullable: false),
                    final_minor = table.Column<long>(type: "bigint", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding_windows", x => x.campaign_id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "pledges",
                columns: table => new
                {
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    reply_status = table.Column<int>(type: "integer", nullable: true),
                    reply_body = table.Column<string>(type: "json", nullable: true),
                    reply_location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.caller_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "pledges",
                columns: table => new
                {
                    handler = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.handler, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "pledges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tier_counters",
                schema: "pledges",
                columns: table => new
                {
                    tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_pledge_minor = table.Column<long>(type: "bigint", nullable: false),
                    limit_qty = table.Column<int>(type: "integer", nullable: true),
                    claimed = table.Column<int>(type: "integer", nullable: false),
                    held = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tier_counters", x => x.tier_id);
                    table.CheckConstraint("never_oversell", "limit_qty IS NULL OR claimed + held <= limit_qty");
                    table.CheckConstraint("tier_counters_claimed_not_negative", "claimed >= 0");
                    table.CheckConstraint("tier_counters_held_not_negative", "held >= 0");
                    table.CheckConstraint("tier_counters_limit_positive", "limit_qty IS NULL OR limit_qty > 0");
                });

            migrationBuilder.CreateIndex(
                name: "funding_windows_due",
                schema: "pledges",
                table: "funding_windows",
                column: "deadline",
                filter: "status IN ('LIVE', 'ENDED')");

            migrationBuilder.CreateIndex(
                name: "outbox_undelivered",
                schema: "pledges",
                table: "outbox",
                column: "occurred_at",
                filter: "delivered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tier_counters_campaign_id",
                schema: "pledges",
                table: "tier_counters",
                column: "campaign_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "funding_windows",
                schema: "pledges");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "pledges");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "pledges");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "pledges");

            migrationBuilder.DropTable(
                name: "tier_counters",
                schema: "pledges");
        }
    }
}
