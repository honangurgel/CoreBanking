using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using CoreBanking.Application;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class CustomerAccountApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using var db = new BankingDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseTestLoopback();
        });

    [Fact]
    public async Task CreatesCustomerAndAccount_AndPersistsAcrossHostRestart()
    {
        Guid customerId;
        Guid accountId;
        using (var factory = CreateFactory())
        using (var client = factory.CreateClient())
        {
            using var customerResponse = await client.PostAsJsonAsync("/api/v1/customers", new { name = " Cliente Fictício " });
            Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
            var customer = await customerResponse.Content.ReadFromJsonAsync<CustomerResponse>();
            Assert.NotNull(customer);
            customerId = customer.Id;
            Assert.Equal("Cliente Fictício", customer.Name);
            Assert.Equal($"/api/v1/customers/{customerId}", customerResponse.Headers.Location?.OriginalString);

            using var accountResponse = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId });
            Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
            var account = await accountResponse.Content.ReadFromJsonAsync<AccountResponse>();
            Assert.NotNull(account);
            accountId = account.Id;
            Assert.Equal(customerId, account.CustomerId);
            Assert.Equal("Active", account.Status);
            Assert.Equal(0, account.BalanceMinor);
            Assert.Equal("BRL", account.Currency);
            Assert.Equal($"/api/v1/accounts/{accountId}", accountResponse.Headers.Location?.OriginalString);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
            var customerLedger = await db.LedgerAccounts.AsNoTracking().SingleAsync(item => item.CustomerAccountId == accountId);
            var systemLedger = LedgerAccount.ForSystem();
            var writer = new LedgerWriter(db);
            await writer.CreateAccountAsync(systemLedger);
            await writer.PostAsync(LedgerEntry.Create("Crédito fictício",
                [new(customerLedger.Id, 123), new(systemLedger.Id, -123)]));
        }

        using var restartedFactory = CreateFactory();
        using var restartedClient = restartedFactory.CreateClient();
        var persistedCustomer = await restartedClient.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customerId}");
        var persistedAccount = await restartedClient.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{accountId}");
        Assert.NotNull(persistedCustomer);
        Assert.Equal("Cliente Fictício", persistedCustomer.Name);
        Assert.NotNull(persistedAccount);
        Assert.Equal(customerId, persistedAccount.CustomerId);
        Assert.Equal("Active", persistedAccount.Status);
        Assert.Equal(123, persistedAccount.BalanceMinor);
        Assert.Equal("BRL", persistedAccount.Currency);
    }

    [Fact]
    public async Task RejectsInvalidInputAndUnknownEntities()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var emptyName = await client.PostAsJsonAsync("/api/v1/customers", new { name = " " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);

        using var longName = await client.PostAsJsonAsync("/api/v1/customers", new { name = new string('x', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);

        using var emptyId = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);

        using var missingCustomer = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, missingCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Statement_PaginatesTiedEntriesAndReversal_WithoutMixingAccounts()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var accountId = await LoadTestHelpers.CreateAccountAsync(client);
        var otherAccountId = await LoadTestHelpers.CreateAccountAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var ledgerId = (await db.LedgerAccounts.SingleAsync(item => item.CustomerAccountId == accountId)).Id;
        var otherLedgerId = (await db.LedgerAccounts.SingleAsync(item => item.CustomerAccountId == otherAccountId)).Id;
        var counterparty = LedgerAccount.ForSystem();
        await new LedgerWriter(db).CreateAccountAsync(counterparty);
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(1, 7).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}")).ToArray();

        async Task PostAsync(Guid entryId, Guid customerLedger, long amount, DateTimeOffset createdAt, Guid? reverses = null)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerEntries\" (\"Id\", \"Description\", \"CreatedAt\", \"Posted\", \"ReversesEntryId\") VALUES ({entryId}, {"Operação fictícia"}, {createdAt}, false, {reverses})");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"LedgerPostings\" (\"EntryId\", \"LedgerAccountId\", \"AmountMinor\") VALUES ({entryId}, {customerLedger}, {amount}), ({entryId}, {counterparty.Id}, {-amount})");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LedgerEntries\" SET \"Posted\" = true WHERE \"Id\" = {entryId}");
            await tx.CommitAsync();
        }

        await PostAsync(ids[0], ledgerId, 100, timestamp);
        await PostAsync(ids[1], ledgerId, -20, timestamp);
        await PostAsync(ids[2], ledgerId, 30, timestamp);
        await PostAsync(ids[3], ledgerId, 7, timestamp);
        await PostAsync(ids[4], ledgerId, -100, timestamp.AddSeconds(1), ids[0]);
        await PostAsync(ids[5], otherLedgerId, 900, timestamp.AddSeconds(1));

        var first = await client.GetFromJsonAsync<AccountStatement>($"/api/v1/accounts/{accountId}/statement?pageSize=2");
        Assert.NotNull(first);
        Assert.Equal(accountId, first.AccountId);
        Assert.Equal("BRL", first.Currency);
        Assert.Equal(new[] { ids[4], ids[3] }, first.Items.Select(item => item.EntryId));
        Assert.Equal(ids[0], first.Items[0].ReversesEntryId);
        Assert.NotNull(first.NextCursor);

        await PostAsync(ids[6], ledgerId, 5, timestamp.AddSeconds(2));
        var all = new List<StatementItem>(first.Items);
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var before = Uri.EscapeDataString(cursor.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            var page = await client.GetFromJsonAsync<AccountStatement>(
                $"/api/v1/accounts/{accountId}/statement?pageSize=2&beforeCreatedAt={before}&beforeEntryId={cursor.EntryId}");
            Assert.NotNull(page);
            Assert.InRange(page.Items.Count, 1, 2);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(all.Count <= 5, "O cursor não avançou corretamente.");
        }
        Assert.Equal(new[] { ids[4], ids[3], ids[2], ids[1], ids[0] }, all.Select(item => item.EntryId));
        Assert.Equal(17, all.Sum(item => item.AmountMinor));
        var account = await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{accountId}");
        Assert.Equal(22, account!.BalanceMinor);
        await LoadTestHelpers.AssertLedgerAsync(db);
    }

    [Fact]
    public async Task Statement_ValidatesPaginationAndReportsEmptyOrMissingAccount()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var accountId = await LoadTestHelpers.CreateAccountAsync(client);
        var path = $"/api/v1/accounts/{accountId}/statement";
        var empty = await client.GetFromJsonAsync<AccountStatement>(path);
        Assert.NotNull(empty);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}?pageSize=100")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}/statement")).StatusCode);
        foreach (var query in new[]
        {
            "?pageSize=0", "?pageSize=101", "?pageSize=not-a-number",
            $"?beforeEntryId={Guid.NewGuid()}", "?beforeCreatedAt=2026-01-01T00%3A00%3A00Z",
            $"?beforeCreatedAt=invalid&beforeEntryId={Guid.NewGuid()}",
            $"?beforeCreatedAt=2026-01-01T00%3A00%3A00Z&beforeEntryId={Guid.Empty}"
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + query)).StatusCode);
    }

    [Fact]
    public async Task Statement_ReflectsCompletedMovementsWithoutDuplicatingRetriesOrRejections()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var source = await LoadTestHelpers.CreateAccountAsync(client);
        var destination = await LoadTestHelpers.CreateAccountAsync(client);

        async Task<HttpStatusCode> SendAsync(string path, string key, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        var deposit = new { accountId = source, amountMinor = 1000 };
        var transfer = new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 250 };
        Assert.Equal(HttpStatusCode.Created, await SendAsync("/api/v1/deposits", "statement-deposit", deposit));
        Assert.Equal(HttpStatusCode.Created, await SendAsync("/api/v1/deposits", "statement-deposit", deposit));
        Assert.Equal(HttpStatusCode.Created, await SendAsync("/api/v1/transfers", "statement-transfer", transfer));
        Assert.Equal(HttpStatusCode.Conflict, await SendAsync("/api/v1/transfers", "statement-rejected",
            new { sourceAccountId = source, destinationAccountId = destination, amountMinor = 9999 }));

        var from = await client.GetFromJsonAsync<AccountStatement>($"/api/v1/accounts/{source}/statement");
        var to = await client.GetFromJsonAsync<AccountStatement>($"/api/v1/accounts/{destination}/statement");
        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(new long[] { -250, 1000 }, from.Items.Select(item => item.AmountMinor));
        Assert.Equal(new long[] { 250 }, to.Items.Select(item => item.AmountMinor));
        Assert.Equal(from.Items[0].EntryId, to.Items[0].EntryId);
        Assert.Equal(750, from.Items.Sum(item => item.AmountMinor));
    }

    [Fact]
    public async Task Statement_EnforcesPageSizeOnLargeAccount()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var accountId = await LoadTestHelpers.CreateAccountAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var ledgerId = (await db.LedgerAccounts.SingleAsync(item => item.CustomerAccountId == accountId)).Id;
        var counterparty = LedgerAccount.ForSystem();
        await new LedgerWriter(db).CreateAccountAsync(counterparty);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LedgerEntries" ("Id", "Description", "CreatedAt", "Posted")
                SELECT md5('statement-' || n)::uuid, 'Carga fictícia',
                       timestamptz '2026-01-01 00:00:00+00' + n * interval '1 second', false
                FROM generate_series(1, 101) n;
                INSERT INTO "LedgerPostings" ("EntryId", "LedgerAccountId", "AmountMinor")
                SELECT md5('statement-' || n)::uuid, {ledgerId}, 1 FROM generate_series(1, 101) n
                UNION ALL
                SELECT md5('statement-' || n)::uuid, {counterparty.Id}, -1 FROM generate_series(1, 101) n;
                UPDATE "LedgerEntries" SET "Posted" = true WHERE "Id" IN
                    (SELECT md5('statement-' || n)::uuid FROM generate_series(1, 101) n);
                """);
            await tx.CommitAsync();
        }
        var path = $"/api/v1/accounts/{accountId}/statement";
        var defaultPage = await client.GetFromJsonAsync<AccountStatement>(path);
        Assert.NotNull(defaultPage);
        Assert.Equal(50, defaultPage.Items.Count);
        Assert.NotNull(defaultPage.NextCursor);
        var maxPage = await client.GetFromJsonAsync<AccountStatement>($"{path}?pageSize=100");
        Assert.NotNull(maxPage);
        Assert.Equal(100, maxPage.Items.Count);
        Assert.NotNull(maxPage.NextCursor);
        var before = Uri.EscapeDataString(maxPage.NextCursor.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        var lastPage = await client.GetFromJsonAsync<AccountStatement>(
            $"{path}?pageSize=100&beforeCreatedAt={before}&beforeEntryId={maxPage.NextCursor.EntryId}");
        Assert.NotNull(lastPage);
        Assert.Single(lastPage.Items);
        Assert.Null(lastPage.NextCursor);
        Assert.Equal(101, maxPage.Items.Sum(item => item.AmountMinor) + lastPage.Items.Sum(item => item.AmountMinor));
    }

    [Fact]
    public async Task StatementIndexes_CanBeRolledBackAndReappliedWithoutLosingData()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var accountId = await LoadTestHelpers.CreateAccountAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261124210000_AddPixThrottle");
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('"IX_LedgerPostings_LedgerAccountId_EntryId"') IS NOT NULL OR to_regclass('"IX_LedgerEntries_CreatedAt_Id_Posted"') IS NOT NULL AS "Value" """).SingleAsync());
        await migrator.MigrateAsync();
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            """SELECT to_regclass('"IX_LedgerPostings_LedgerAccountId_EntryId"') IS NOT NULL AND to_regclass('"IX_LedgerEntries_CreatedAt_Id_Posted"') IS NOT NULL AS "Value" """).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/accounts/{accountId}/statement")).StatusCode);
    }

    private sealed record CustomerResponse(Guid Id, string Name);
    private sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, long BalanceMinor, string Currency);
}
