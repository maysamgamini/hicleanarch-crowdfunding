using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Each card setup and charge keeps the W3C context of the work that created it, so the PSP's
    /// webhook about it can link its own trace back (lesson s4-l08). Rows from before stay null and
    /// their webhooks start a trace with no link.
    /// </summary>
    public partial class TraceContextOfSetupsAndCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "payments",
                table: "charges",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "payments",
                table: "card_setups",
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
                table: "charges");

            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "payments",
                table: "card_setups");
        }
    }
}
