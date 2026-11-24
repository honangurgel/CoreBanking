using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261124181000_BackfillCustomerLedgerAccounts")]
public sealed class BackfillCustomerLedgerAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "LedgerAccounts" ("Id", "CustomerAccountId", "BalanceMinor")
            SELECT gen_random_uuid(), a."Id", 0
            FROM "Accounts" a
            WHERE NOT EXISTS (
                SELECT 1 FROM "LedgerAccounts" l WHERE l."CustomerAccountId" = a."Id"
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Ledger accounts may have acquired history and must not be deleted on rollback.
    }
}
