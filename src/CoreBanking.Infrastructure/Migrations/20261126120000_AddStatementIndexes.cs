using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoreBanking.Infrastructure.Migrations;

[DbContext(typeof(BankingDbContext))]
[Migration("20261126120000_AddStatementIndexes")]
public sealed class AddStatementIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX "IX_LedgerPostings_LedgerAccountId_EntryId"
                ON "LedgerPostings" ("LedgerAccountId", "EntryId");
            CREATE INDEX "IX_LedgerEntries_CreatedAt_Id_Posted"
                ON "LedgerEntries" ("CreatedAt" DESC, "Id" DESC) WHERE "Posted";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX "IX_LedgerEntries_CreatedAt_Id_Posted";
            DROP INDEX "IX_LedgerPostings_LedgerAccountId_EntryId";
            """);
    }
}
