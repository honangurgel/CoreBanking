using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDebitCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DebitCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebitCards", x => x.Id);
                    table.UniqueConstraint("AK_DebitCards_Id_AccountId", x => new { x.Id, x.AccountId });
                    table.ForeignKey(
                        name: "FK_DebitCards_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DebitAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReservationEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalizationEntryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebitAuthorizations", x => x.Id);
                    table.CheckConstraint("CK_DebitAuthorizations_Amount", "\"AmountMinor\" > 0 AND \"AmountMinor\" <= 99999999999999999");
                    table.CheckConstraint("CK_DebitAuthorizations_State", "(\"Status\" = 'InsufficientFunds' AND \"ReservationEntryId\" IS NULL AND \"FinalizationEntryId\" IS NULL AND \"FinalizedAt\" IS NULL) OR (\"Status\" = 'Authorized' AND \"ReservationEntryId\" IS NOT NULL AND \"FinalizationEntryId\" IS NULL AND \"FinalizedAt\" IS NULL) OR (\"Status\" IN ('Settled', 'Cancelled') AND \"ReservationEntryId\" IS NOT NULL AND \"FinalizationEntryId\" IS NOT NULL AND \"FinalizedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DebitAuthorizations_DebitCards_CardId_AccountId",
                        columns: x => new { x.CardId, x.AccountId },
                        principalTable: "DebitCards",
                        principalColumns: new[] { "Id", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DebitAuthorizations_AccountId_IdempotencyKey",
                table: "DebitAuthorizations",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DebitAuthorizations_CardId_AccountId",
                table: "DebitAuthorizations",
                columns: new[] { "CardId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_DebitCards_AccountId_IdempotencyKey",
                table: "DebitCards",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "LedgerAccounts" ("Id", "CustomerAccountId", "BalanceMinor")
                VALUES ('9c48bd10-5d28-4c64-94da-aaf778039300', NULL, 0),
                       ('9c48bd10-5d28-4c64-94da-aaf778039301', NULL, 0)
                ON CONFLICT ("Id") DO NOTHING;
                DO $$
                BEGIN
                    IF (SELECT count(*) FROM "LedgerAccounts"
                        WHERE "Id" IN ('9c48bd10-5d28-4c64-94da-aaf778039300', '9c48bd10-5d28-4c64-94da-aaf778039301')
                        AND "CustomerAccountId" IS NULL AND "BalanceMinor" = 0) <> 2 THEN
                        RAISE EXCEPTION 'Invalid simulated debit card ledger accounts';
                    END IF;
                END;
                $$;
                ALTER TABLE "DebitAuthorizations"
                    ADD CONSTRAINT "FK_DebitAuthorizations_ReservationEntry"
                        FOREIGN KEY ("ReservationEntryId") REFERENCES "LedgerEntries" ("Id") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_DebitAuthorizations_FinalizationEntry"
                        FOREIGN KEY ("FinalizationEntryId") REFERENCES "LedgerEntries" ("Id") ON DELETE RESTRICT,
                    ADD CONSTRAINT "CK_DebitAuthorizations_DistinctEntries"
                        CHECK ("FinalizationEntryId" IS NULL OR "FinalizationEntryId" <> "ReservationEntryId");
                CREATE UNIQUE INDEX "IX_DebitAuthorizations_ReservationEntryId"
                    ON "DebitAuthorizations" ("ReservationEntryId") WHERE "ReservationEntryId" IS NOT NULL;
                CREATE UNIQUE INDEX "IX_DebitAuthorizations_FinalizationEntryId"
                    ON "DebitAuthorizations" ("FinalizationEntryId") WHERE "FinalizationEntryId" IS NOT NULL;
                ALTER TABLE "OperationAuditEvents"
                    DROP CONSTRAINT "OperationAuditEvents_Action_check",
                    DROP CONSTRAINT "OperationAuditEvents_check",
                    ADD CONSTRAINT "OperationAuditEvents_Action_check"
                        CHECK ("Action" IN ('Deposit', 'Transfer', 'PixCreate', 'PixRetry',
                            'CardIssue', 'CardAuthorize', 'CardSettle', 'CardCancel')),
                    ADD CONSTRAINT "OperationAuditEvents_check"
                        CHECK (("Action" = 'Deposit' AND "Outcome" = 'Completed')
                            OR ("Action" = 'Transfer' AND "Outcome" IN ('Completed', 'InsufficientFunds'))
                            OR ("Action" = 'PixCreate' AND "Outcome" IN ('Pending', 'InsufficientFunds'))
                            OR ("Action" = 'PixRetry' AND "Outcome" = 'Pending')
                            OR ("Action" = 'CardIssue' AND "Outcome" = 'Created')
                            OR ("Action" = 'CardAuthorize' AND "Outcome" IN ('Authorized', 'InsufficientFunds'))
                            OR ("Action" = 'CardSettle' AND "Outcome" = 'Settled')
                            OR ("Action" = 'CardCancel' AND "Outcome" = 'Cancelled'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "DebitCards", "DebitAuthorizations", "OperationAuditEvents" IN ACCESS EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "DebitCards") OR EXISTS (SELECT 1 FROM "DebitAuthorizations")
                        OR EXISTS (SELECT 1 FROM "OperationAuditEvents"
                            WHERE "Action" IN ('CardIssue', 'CardAuthorize', 'CardSettle', 'CardCancel')) THEN
                        RAISE EXCEPTION 'Cannot remove debit card history';
                    END IF;
                END;
                $$;
                ALTER TABLE "OperationAuditEvents"
                    DROP CONSTRAINT "OperationAuditEvents_Action_check",
                    DROP CONSTRAINT "OperationAuditEvents_check",
                    ADD CONSTRAINT "OperationAuditEvents_Action_check"
                        CHECK ("Action" IN ('Deposit', 'Transfer', 'PixCreate', 'PixRetry')),
                    ADD CONSTRAINT "OperationAuditEvents_check"
                        CHECK (("Action" = 'Deposit' AND "Outcome" = 'Completed')
                            OR ("Action" = 'Transfer' AND "Outcome" IN ('Completed', 'InsufficientFunds'))
                            OR ("Action" = 'PixCreate' AND "Outcome" IN ('Pending', 'InsufficientFunds'))
                            OR ("Action" = 'PixRetry' AND "Outcome" = 'Pending'));
                """);
            migrationBuilder.DropTable(
                name: "DebitAuthorizations");

            migrationBuilder.DropTable(
                name: "DebitCards");
        }
    }
}
