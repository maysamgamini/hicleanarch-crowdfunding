using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoneyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "psp_refund_id",
                schema: "payments",
                table: "charges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "campaign_payees",
                schema: "payments",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_payees", x => x.campaign_id);
                });

            migrationBuilder.CreateTable(
                name: "money_journals",
                schema: "payments",
                columns: table => new
                {
                    journal_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_money_journals", x => x.journal_id);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                schema: "payments",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    reserve_minor = table.Column<long>(type: "bigint", nullable: true),
                    psp_transfer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    paid_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payouts", x => x.campaign_id);
                });

            migrationBuilder.CreateTable(
                name: "money_entries",
                schema: "payments",
                columns: table => new
                {
                    entry_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    journal_id = table.Column<long>(type: "bigint", nullable: false),
                    account = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_money_entries", x => x.entry_id);
                    table.CheckConstraint("money_entries_not_zero", "amount_minor <> 0");
                    table.ForeignKey(
                        name: "fk_money_entries_money_journals_journal_id",
                        column: x => x.journal_id,
                        principalSchema: "payments",
                        principalTable: "money_journals",
                        principalColumn: "journal_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_money_entries_journal_id",
                schema: "payments",
                table: "money_entries",
                column: "journal_id");

            migrationBuilder.CreateIndex(
                name: "ix_money_journals_campaign_id",
                schema: "payments",
                table: "money_journals",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "one_journal_per_source",
                schema: "payments",
                table: "money_journals",
                columns: new[] { "source_ref", "kind" },
                unique: true);

            // Every journal must sum to zero and have at least two lines. The check runs at commit
            // (a deferred constraint trigger), after all of a journal's lines are in, on both tables,
            // so neither a lopsided journal nor a journal with no lines can commit.
            migrationBuilder.Sql("""
                CREATE FUNCTION payments.money_journal_balances() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    total bigint;
                    lines int;
                BEGIN
                    SELECT coalesce(sum(amount_minor), 0), count(*) INTO total, lines
                    FROM payments.money_entries WHERE journal_id = NEW.journal_id;
                    IF total <> 0 OR lines < 2 THEN
                        RAISE EXCEPTION 'money journal % does not balance: % over % lines', NEW.journal_id, total, lines
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE CONSTRAINT TRIGGER money_entries_balance
                    AFTER INSERT ON payments.money_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION payments.money_journal_balances();

                CREATE CONSTRAINT TRIGGER money_journals_balance
                    AFTER INSERT ON payments.money_journals
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION payments.money_journal_balances();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER money_journals_balance ON payments.money_journals;
                DROP TRIGGER money_entries_balance ON payments.money_entries;
                DROP FUNCTION payments.money_journal_balances();
                """);

            migrationBuilder.DropTable(
                name: "campaign_payees",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "money_entries",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "payouts",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "money_journals",
                schema: "payments");

            migrationBuilder.DropColumn(
                name: "psp_refund_id",
                schema: "payments",
                table: "charges");
        }
    }
}
