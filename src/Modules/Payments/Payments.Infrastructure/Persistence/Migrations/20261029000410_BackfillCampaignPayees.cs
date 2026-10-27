using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Campaigns published before s3-l15 have no payee row, because RecordPayee did not exist when
    /// their CampaignPublished was handled; without one, their creator could not see the settlement or
    /// be paid. The deploy's owner connection may read every schema, so it copies them from
    /// Campaigns once; the module's own role never can. A fresh database has no campaigns yet.
    /// </summary>
    public partial class BackfillCampaignPayees : Migration
    {
        internal const string Backfill = """
                DO $$
                BEGIN
                    IF to_regclass('campaigns.campaigns') IS NOT NULL THEN
                        INSERT INTO payments.campaign_payees (campaign_id, creator_id)
                        SELECT id, creator_id FROM campaigns.campaigns WHERE published_at IS NOT NULL
                        ON CONFLICT (campaign_id) DO NOTHING;
                    END IF;
                END $$;
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The copied rows cannot be told apart from the ones RecordPayee wrote, and both are true: nothing to undo.
        }
    }
}
