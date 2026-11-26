using System.Net.Http.Json;
using CoreBanking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.IntegrationTests;

internal static class LoadTestHelpers
{
    public static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        using var customer = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Carga fictícia" });
        customer.EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var account = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId });
        account.EnsureSuccessStatusCode();
        return (await account.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    public static async Task AssertLedgerAsync(BankingDbContext db)
    {
        var invalidEntries = await db.Database.SqlQueryRaw<long>(
            """SELECT count(*) AS "Value" FROM "LedgerEntries" WHERE NOT "Posted" """).SingleAsync();
        var negativeBalances = await db.LedgerAccounts.CountAsync(item => item.CustomerAccountId != null && item.BalanceMinor < 0);
        var unbalanced = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM (
                SELECT e."Id"
                FROM "LedgerEntries" e LEFT JOIN "LedgerPostings" p ON p."EntryId" = e."Id"
                WHERE e."Posted" GROUP BY e."Id"
                HAVING count(p."LedgerAccountId") < 2 OR COALESCE(sum(p."AmountMinor"), 0) <> 0
            ) AS invalid
            """).SingleAsync();
        var mismatched = await db.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM "LedgerAccounts" a
            LEFT JOIN (
                SELECT p."LedgerAccountId", sum(p."AmountMinor") AS total
                FROM "LedgerPostings" p JOIN "LedgerEntries" e ON e."Id" = p."EntryId" AND e."Posted"
                GROUP BY p."LedgerAccountId"
            ) p ON p."LedgerAccountId" = a."Id"
            WHERE a."BalanceMinor" <> COALESCE(p.total, 0)
            """).SingleAsync();
        Assert.Equal(0, unbalanced);
        Assert.Equal(0, mismatched);
        Assert.Equal(0, invalidEntries);
        Assert.Equal(0, negativeBalances);
    }

    private sealed record IdResponse(Guid Id);
}
