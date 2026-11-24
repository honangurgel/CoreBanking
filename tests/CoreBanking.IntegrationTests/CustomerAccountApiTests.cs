using System.Net;
using System.Net.Http.Json;
using CoreBanking.Domain.Ledger;
using CoreBanking.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString()));

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

    private sealed record CustomerResponse(Guid Id, string Name);
    private sealed record AccountResponse(Guid Id, Guid CustomerId, string Status, long BalanceMinor, string Currency);
}
