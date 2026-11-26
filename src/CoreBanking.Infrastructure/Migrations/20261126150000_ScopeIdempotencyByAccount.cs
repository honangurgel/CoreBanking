using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261126150000_ScopeIdempotencyByAccount")]
public sealed class ScopeIdempotencyByAccount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX "IX_Deposits_IdempotencyKey";
            DROP INDEX "IX_Transfers_IdempotencyKey";
            DROP INDEX "IX_PixOperations_IdempotencyKey";
            DROP INDEX "IX_PixOperations_AccountId";
            CREATE UNIQUE INDEX "IX_Deposits_AccountId_IdempotencyKey"
                ON "Deposits" ("AccountId", "IdempotencyKey");
            CREATE UNIQUE INDEX "IX_Transfers_AccountPair_IdempotencyKey"
                ON "Transfers" ("SourceAccountId", "DestinationAccountId", "IdempotencyKey");
            CREATE UNIQUE INDEX "IX_PixOperations_AccountId_IdempotencyKey"
                ON "PixOperations" ("AccountId", "IdempotencyKey");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE "Deposits", "Transfers", "PixOperations" IN ACCESS EXCLUSIVE MODE;
            CREATE UNIQUE INDEX "IX_Deposits_IdempotencyKey" ON "Deposits" ("IdempotencyKey");
            CREATE UNIQUE INDEX "IX_Transfers_IdempotencyKey" ON "Transfers" ("IdempotencyKey");
            CREATE UNIQUE INDEX "IX_PixOperations_IdempotencyKey" ON "PixOperations" ("IdempotencyKey");
            CREATE INDEX "IX_PixOperations_AccountId" ON "PixOperations" ("AccountId");
            DROP INDEX "IX_Deposits_AccountId_IdempotencyKey";
            DROP INDEX "IX_Transfers_AccountPair_IdempotencyKey";
            DROP INDEX "IX_PixOperations_AccountId_IdempotencyKey";
            """);
    }
}
