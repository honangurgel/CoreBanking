using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    BalanceMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.CheckConstraint("CK_LedgerAccounts_CustomerBalance", "\"CustomerAccountId\" IS NULL OR \"BalanceMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_LedgerAccounts_Accounts_CustomerAccountId",
                        column: x => x.CustomerAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_CustomerAccountId",
                table: "LedgerAccounts",
                column: "CustomerAccountId",
                unique: true);

            migrationBuilder.Sql("""
                CREATE TABLE "LedgerEntries" (
                    "Id" uuid PRIMARY KEY,
                    "Description" varchar(200) NOT NULL CHECK (length(trim("Description")) > 0),
                    "CreatedAt" timestamptz NOT NULL,
                    "Posted" boolean NOT NULL DEFAULT false,
                    "ReversesEntryId" uuid UNIQUE REFERENCES "LedgerEntries" ("Id") ON DELETE RESTRICT,
                    CHECK ("ReversesEntryId" IS NULL OR "ReversesEntryId" <> "Id")
                );

                CREATE TABLE "LedgerPostings" (
                    "EntryId" uuid NOT NULL REFERENCES "LedgerEntries" ("Id") ON DELETE RESTRICT,
                    "LedgerAccountId" uuid NOT NULL REFERENCES "LedgerAccounts" ("Id") ON DELETE RESTRICT,
                    "AmountMinor" bigint NOT NULL CHECK ("AmountMinor" <> 0 AND "AmountMinor" <> '-9223372036854775808'::bigint),
                    PRIMARY KEY ("EntryId", "LedgerAccountId")
                );

                CREATE FUNCTION protect_ledger_account() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."BalanceMinor" <> 0 THEN
                            RAISE EXCEPTION 'Ledger account must start with zero balance';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Ledger accounts cannot be deleted';
                    END IF;
                    IF pg_trigger_depth() < 2 OR NEW."Id" <> OLD."Id"
                       OR NEW."CustomerAccountId" IS DISTINCT FROM OLD."CustomerAccountId" THEN
                        RAISE EXCEPTION 'Ledger accounts can only be updated by posting an entry';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_ledger_account_trigger
                    BEFORE INSERT OR UPDATE OR DELETE ON "LedgerAccounts"
                    FOR EACH ROW EXECUTE FUNCTION protect_ledger_account();

                CREATE FUNCTION protect_ledger_posting() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE is_posted boolean;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'Ledger postings cannot be modified or deleted';
                    END IF;
                    SELECT "Posted" INTO is_posted FROM "LedgerEntries" WHERE "Id" = NEW."EntryId" FOR UPDATE;
                    IF NOT FOUND OR is_posted THEN
                        RAISE EXCEPTION 'Ledger postings require an open entry';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER protect_ledger_posting_trigger
                    BEFORE INSERT OR UPDATE OR DELETE ON "LedgerPostings"
                    FOR EACH ROW EXECUTE FUNCTION protect_ledger_posting();

                CREATE FUNCTION post_ledger_entry() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE posting_count bigint; total numeric;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Ledger entries cannot be deleted';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Posted" THEN
                            RAISE EXCEPTION 'A ledger entry must start unposted';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Posted" OR NOT NEW."Posted" OR NEW."Id" <> OLD."Id"
                       OR NEW."Description" <> OLD."Description" OR NEW."CreatedAt" <> OLD."CreatedAt"
                       OR NEW."ReversesEntryId" IS DISTINCT FROM OLD."ReversesEntryId" THEN
                        RAISE EXCEPTION 'Ledger entries cannot be modified';
                    END IF;
                    SELECT count(*), coalesce(sum("AmountMinor"), 0)
                        INTO posting_count, total FROM "LedgerPostings" WHERE "EntryId" = NEW."Id";
                    IF posting_count < 2 OR total <> 0 THEN
                        RAISE EXCEPTION 'Ledger entry must contain at least two balanced postings';
                    END IF;
                    IF NEW."ReversesEntryId" IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM "LedgerEntries" WHERE "Id" = NEW."ReversesEntryId" AND "Posted")
                           OR EXISTS (
                               SELECT 1
                               FROM (SELECT * FROM "LedgerPostings" WHERE "EntryId" = NEW."Id") p
                               FULL JOIN (SELECT * FROM "LedgerPostings" WHERE "EntryId" = NEW."ReversesEntryId") original
                                 ON p."LedgerAccountId" = original."LedgerAccountId"
                               WHERE p."AmountMinor" IS DISTINCT FROM -original."AmountMinor"
                           ) THEN
                            RAISE EXCEPTION 'Reversal must exactly offset a posted entry';
                        END IF;
                    END IF;
                    PERFORM a."Id" FROM "LedgerAccounts" a
                        JOIN "LedgerPostings" p ON p."LedgerAccountId" = a."Id"
                        WHERE p."EntryId" = NEW."Id" ORDER BY a."Id" FOR UPDATE OF a;
                    UPDATE "LedgerAccounts" a SET "BalanceMinor" = a."BalanceMinor" + p."AmountMinor"
                        FROM "LedgerPostings" p
                        WHERE p."EntryId" = NEW."Id" AND p."LedgerAccountId" = a."Id";
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER post_ledger_entry_trigger
                    BEFORE INSERT OR UPDATE OR DELETE ON "LedgerEntries"
                    FOR EACH ROW EXECUTE FUNCTION post_ledger_entry();

                CREATE FUNCTION require_posted_entry() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM "LedgerEntries" WHERE "Id" = NEW."Id" AND "Posted") THEN
                        RAISE EXCEPTION 'An unposted ledger entry cannot be committed';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER require_posted_entry_trigger
                    AFTER INSERT ON "LedgerEntries" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION require_posted_entry();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE "LedgerPostings";
                DROP TABLE "LedgerEntries";
                DROP TRIGGER protect_ledger_account_trigger ON "LedgerAccounts";
                DROP FUNCTION protect_ledger_account();
                DROP FUNCTION protect_ledger_posting();
                DROP FUNCTION post_ledger_entry();
                DROP FUNCTION require_posted_entry();
                """);
            migrationBuilder.DropTable(
                name: "LedgerAccounts");
        }
    }
}
