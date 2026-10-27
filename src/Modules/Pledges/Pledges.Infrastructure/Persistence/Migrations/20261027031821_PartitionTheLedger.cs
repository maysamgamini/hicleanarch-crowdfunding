using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pledges.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartitionTheLedger : Migration
    {
        /// <inheritdoc />
        // EF Core cannot partition a table, so this migration is SQL. The model above it says the same
        // thing in EF's terms (the composite key, the guard tables), so later migrations start from here.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- 0. No entry may be written while the ledger moves: a row committed between the steps
                --    below would be copied without its guards. Held until the migration commits.
                LOCK TABLE pledges.pledge_ledger IN EXCLUSIVE MODE;

                -- 1. The guards a partitioned table cannot hold. The trigger of step 5 fills them,
                --    for the entries copied in step 6 and for every entry after.
                CREATE TABLE pledges.ledger_causes (
                    cause_id    varchar(200) NOT NULL,
                    kind        varchar(10)  NOT NULL,
                    created_at  timestamptz  NOT NULL DEFAULT now(),
                    CONSTRAINT pk_ledger_causes PRIMARY KEY (cause_id, kind)
                );
                CREATE INDEX ix_ledger_causes_created_at ON pledges.ledger_causes (created_at);
                CREATE TABLE pledges.pledge_activations (
                    pledge_id uuid NOT NULL,
                    CONSTRAINT pk_pledge_activations PRIMARY KEY (pledge_id)
                );

                -- 2. When each funding window opened, never after its first entry.
                ALTER TABLE pledges.funding_windows ADD COLUMN opened_at timestamptz NOT NULL DEFAULT now();
                UPDATE pledges.funding_windows w SET opened_at = least(w.opened_at,
                    (SELECT min(l.created_at) FROM pledges.pledge_ledger l WHERE l.campaign_id = w.campaign_id));

                -- 3. The old ledger steps aside.
                ALTER TABLE pledges.pledge_ledger RENAME TO pledge_ledger_unpartitioned;
                ALTER TABLE pledges.pledge_ledger_unpartitioned RENAME CONSTRAINT pk_pledge_ledger TO pk_pledge_ledger_unpartitioned;
                DROP INDEX pledges.ix_pledge_ledger_campaign_id;
                DROP INDEX pledges.ix_pledge_ledger_cause_id_kind;
                DROP INDEX pledges.one_activation_per_pledge;

                -- 4. The ledger, partitioned by month. Every key includes the partition column.
                CREATE TABLE pledges.pledge_ledger (
                    entry_id     bigint       GENERATED ALWAYS AS IDENTITY,
                    campaign_id  uuid         NOT NULL,
                    pledge_id    uuid         NOT NULL,
                    cause_id     varchar(200) NOT NULL,
                    delta_minor  bigint       NOT NULL,
                    kind         varchar(10)  NOT NULL,
                    created_at   timestamptz  NOT NULL DEFAULT now(),
                    CONSTRAINT pk_pledge_ledger PRIMARY KEY (entry_id, created_at),
                    CONSTRAINT pledge_ledger_kind CHECK (kind IN ('ACTIVATE', 'CHANGE', 'CANCEL', 'DROP')),
                    CONSTRAINT pledge_ledger_sign CHECK ((kind = 'ACTIVATE' AND delta_minor > 0)
                        OR (kind IN ('CANCEL', 'DROP') AND delta_minor < 0) OR (kind = 'CHANGE' AND delta_minor <> 0))
                ) PARTITION BY RANGE (created_at);
                CREATE INDEX ix_pledge_ledger_campaign_id_created_at
                    ON pledges.pledge_ledger (campaign_id, created_at) INCLUDE (delta_minor);

                -- 5. Partitions are made by the owner's functions; the module's role may only run them.
                CREATE FUNCTION pledges.ensure_ledger_partitions(from_month date, months_ahead integer)
                RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                DECLARE
                    month   date := date_trunc('month', from_month)::date;
                    last    date := (date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => months_ahead))::date;
                    created integer := 0;
                    name    text;
                BEGIN
                    WHILE month <= last LOOP
                        name := 'pledge_ledger_' || to_char(month, 'YYYY_MM');
                        IF to_regclass('pledges.' || name) IS NULL THEN
                            EXECUTE format('CREATE TABLE pledges.%I PARTITION OF pledges.pledge_ledger FOR VALUES FROM (%L) TO (%L)',
                                name, month::timestamp AT TIME ZONE 'UTC', (month + interval '1 month')::timestamp AT TIME ZONE 'UTC');
                            created := created + 1;
                        END IF;
                        month := (month + interval '1 month')::date;
                    END LOOP;
                    RETURN created;
                END
                $fn$;

                CREATE FUNCTION pledges.ledger_partition_months_ahead()
                RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                    SELECT coalesce(max((extract(year FROM age(to_date(substring(c.relname FROM '\d{4}_\d{2}$'), 'YYYY_MM'),
                                                                date_trunc('month', now() AT TIME ZONE 'UTC'))) * 12
                                     + extract(month FROM age(to_date(substring(c.relname FROM '\d{4}_\d{2}$'), 'YYYY_MM'),
                                                                date_trunc('month', now() AT TIME ZONE 'UTC'))))::integer), -1)
                    FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                    WHERE i.inhparent = 'pledges.pledge_ledger'::regclass
                $fn$;

                -- Detaching is the owner's alone (the module's role may not run it), never of a month
                -- newer than 13 months back, and never of a month that holds an entry of a campaign
                -- still in play: a funding window not decided, or a pledge not in a final status.
                -- Whether the creator has been paid lives in Payments; 13 months is far beyond any
                -- settlement, retry window or payout.
                CREATE FUNCTION pledges.detach_ledger_partitions_before(before_month date)
                RETURNS integer LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
                DECLARE
                    newest    date := (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '13 months')::date;
                    partition record;
                    detached  integer := 0;
                    busy      uuid;
                BEGIN
                    IF date_trunc('month', before_month)::date > newest THEN
                        RAISE EXCEPTION 'ledger months newer than % are never detached (asked: before %)', newest, before_month
                            USING ERRCODE = 'check_violation';
                    END IF;
                    FOR partition IN
                        SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                        WHERE i.inhparent = 'pledges.pledge_ledger'::regclass
                          AND to_date(substring(c.relname FROM '\d{4}_\d{2}$'), 'YYYY_MM') < date_trunc('month', before_month)
                        ORDER BY c.relname
                    LOOP
                        EXECUTE format($q$
                            SELECT l.campaign_id FROM pledges.%I l
                            WHERE EXISTS (SELECT 1 FROM pledges.funding_windows w WHERE w.campaign_id = l.campaign_id
                                          AND w.status NOT IN ('FUNDED', 'UNSUCCESSFUL', 'CANCELLED'))
                               OR EXISTS (SELECT 1 FROM pledges.pledges p WHERE p.campaign_id = l.campaign_id
                                          AND p.status NOT IN ('EXPIRED', 'CANCELLED', 'RELEASED', 'CHARGED', 'FAILED', 'DROPPED'))
                            LIMIT 1 $q$, partition.relname) INTO busy;
                        IF busy IS NOT NULL THEN
                            RAISE EXCEPTION '% holds entries of campaign %, which is still in play', partition.relname, busy
                                USING ERRCODE = 'check_violation';
                        END IF;
                        EXECUTE format('ALTER TABLE pledges.pledge_ledger DETACH PARTITION pledges.%I', partition.relname);
                        detached := detached + 1;
                    END LOOP;
                    RETURN detached;
                END
                $fn$;

                -- The guards, filled by the database for every entry, whoever writes it: an older task
                -- during a rolling deploy cannot write an entry without them.
                CREATE FUNCTION pledges.guard_ledger_entry()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
                BEGIN
                    -- created_at is now, not the entry's time: a guard lives 30 days from when it was written.
                    INSERT INTO pledges.ledger_causes (cause_id, kind) VALUES (NEW.cause_id, NEW.kind);
                    IF NEW.kind = 'ACTIVATE' THEN
                        INSERT INTO pledges.pledge_activations (pledge_id) VALUES (NEW.pledge_id);
                    END IF;
                    RETURN NULL;
                END
                $fn$;
                CREATE TRIGGER guard_ledger_entry AFTER INSERT ON pledges.pledge_ledger
                    FOR EACH ROW EXECUTE FUNCTION pledges.guard_ledger_entry();

                REVOKE ALL ON FUNCTION pledges.ensure_ledger_partitions(date, integer) FROM PUBLIC;
                REVOKE ALL ON FUNCTION pledges.ledger_partition_months_ahead() FROM PUBLIC;
                REVOKE ALL ON FUNCTION pledges.detach_ledger_partitions_before(date) FROM PUBLIC;
                REVOKE ALL ON FUNCTION pledges.guard_ledger_entry() FROM PUBLIC;

                SELECT pledges.ensure_ledger_partitions(
                    coalesce((SELECT min(created_at) AT TIME ZONE 'UTC' FROM pledges.pledge_ledger_unpartitioned),
                             now() AT TIME ZONE 'UTC')::date, 3);

                -- 6. The entries move across, keeping their ids (the trigger writes their guards), and the old table goes.
                INSERT INTO pledges.pledge_ledger (entry_id, campaign_id, pledge_id, cause_id, delta_minor, kind, created_at)
                    OVERRIDING SYSTEM VALUE
                    SELECT entry_id, campaign_id, pledge_id, cause_id, delta_minor, kind, created_at
                    FROM pledges.pledge_ledger_unpartitioned;
                SELECT setval(pg_get_serial_sequence('pledges.pledge_ledger', 'entry_id'),
                              coalesce((SELECT max(entry_id) FROM pledges.pledge_ledger), 0) + 1, false);
                DROP TABLE pledges.pledge_ledger_unpartitioned;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("A partitioned ledger is not turned back into one table; restore from a snapshot instead.");
    }
}
