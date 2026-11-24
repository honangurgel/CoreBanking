using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulatedDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Deposits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deposits", x => x.Id);
                    table.CheckConstraint("CK_Deposits_Amount", "\"AmountMinor\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deposits_IdempotencyKey",
                table: "Deposits",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "LedgerAccounts" ("Id", "CustomerAccountId", "BalanceMinor")
                VALUES ('a6127328-00c1-48fd-8c22-fd22289349fa', NULL, 0)
                ON CONFLICT ("Id") DO NOTHING;
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM "LedgerAccounts"
                        WHERE "Id" = 'a6127328-00c1-48fd-8c22-fd22289349fa' AND "CustomerAccountId" IS NULL) THEN
                        RAISE EXCEPTION 'Invalid simulated deposit counterparty';
                    END IF;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The counterparty may have ledger history and cannot be deleted.
            migrationBuilder.DropTable(
                name: "Deposits");
        }
    }
}
