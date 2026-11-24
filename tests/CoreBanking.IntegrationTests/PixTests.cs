using System.Net;
using System.Net.Http.Json;
using CoreBanking.Application.Pix;
using CoreBanking.Infrastructure;
using CoreBanking.Simulator;
using CoreBanking.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace CoreBanking.IntegrationTests;

public sealed class PixTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await postgres.DisposeAsync();
    private BankingDbContext CreateContext() => new(new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    private WebApplicationFactory<Program> CreateApi() => new WebApplicationFactory<Program>().WithWebHostBuilder(
        builder => builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString()));
    private ServiceProvider CreateServices() => new ServiceCollection()
        .AddDbContext<BankingDbContext>(options => options.UseNpgsql(postgres.GetConnectionString())).BuildServiceProvider();

    private static async Task<Guid> AccountAsync(HttpClient client)
    {
        using var customerResponse = await client.PostAsJsonAsync("/api/v1/customers", new { name = "Pessoa fictícia" });
        customerResponse.EnsureSuccessStatusCode();
        var customer = await customerResponse.Content.ReadFromJsonAsync<IdResponse>();
        using var accountResponse = await client.PostAsJsonAsync("/api/v1/accounts", new { customerId = customer!.Id });
        accountResponse.EnsureSuccessStatusCode();
        return (await accountResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, PixRequest operation, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pix/operations");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(operation);
        return await client.SendAsync(request);
    }

    private static async Task CreditAsync(HttpClient client, Guid account, long amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deposits");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new { accountId = account, amountMinor = amount });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<long> BalanceAsync(HttpClient client, Guid account) =>
        (await client.GetFromJsonAsync<AccountResponse>($"/api/v1/accounts/{account}"))!.BalanceMinor;

    [Theory]
    [InlineData("Confirm", 600L, "Confirmed")]
    [InlineData("Reject", 1000L, "Rejected")]
    public async Task Outgoing_ReservesAndFinalizesOnce(string scenario, long finalBalance, string status)
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        await CreditAsync(client, account, 1000);
        var operation = new PixRequest(account, Guid.NewGuid(), "00000000", "Outgoing", 400, scenario);
        var responses = await Task.WhenAll(SubmitAsync(client, operation, "same"), SubmitAsync(client, operation, "same"));
        using var first = responses[0];
        using var second = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var result = (await first.Content.ReadFromJsonAsync<PixResult>())!;
        Assert.Equal(result.Id, (await second.Content.ReadFromJsonAsync<PixResult>())!.Id);
        Assert.Equal(600, await BalanceAsync(client, account));
        using var conflict = await SubmitAsync(client, operation with { AmountMinor = 401 }, "same");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var processing = await Task.WhenAll(
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None),
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Single(processing, processed => processed);
        Assert.False(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(finalBalance, await BalanceAsync(client, account));
        Assert.Equal(status, (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{result.Id}"))!.Status);
        var receipt = await client.GetFromJsonAsync<PixReceipt>($"/api/v1/pix/operations/{result.Id}/receipt");
        Assert.NotNull(receipt);
        Assert.NotNull(receipt.ReservationEntryId);
        Assert.NotNull(receipt.SettlementEntryId);
        Assert.Equal(new[] { "Pending", status }, receipt.Transitions.Select(item => item.Status));
        await using var db = CreateContext();
        Assert.Equal(2, await db.PixTransitions.CountAsync(item => item.OperationId == result.Id));
        Assert.Equal(1, await db.PixOperations.CountAsync());
    }

    [Fact]
    public async Task Incoming_RequiresLocalKeyAndHonorsMaximum()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        var request = new PixRequest(account, Guid.NewGuid(), "60701190", "Incoming", PixRules.MaxAmountMinor, "Confirm");
        using var missing = await SubmitAsync(client, request, "incoming");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        request = request with { KeyId = key };
        using var tooLarge = await SubmitAsync(client, request with { AmountMinor = PixRules.MaxAmountMinor + 1 }, "incoming");
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        using var created = await SubmitAsync(client, request, "incoming");
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal(0, await BalanceAsync(client, account));
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(PixRules.MaxAmountMinor, await BalanceAsync(client, account));
    }

    [Fact]
    public async Task Unavailable_EntersReviewWithReservationUntilManualRetry()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        using var unavailable = new HttpClient(new UnavailableHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        await CreditAsync(client, account, 1000);
        var operation = new PixRequest(account, Guid.NewGuid(), "60746948", "Outgoing", 400, "Confirm");
        using var response = await SubmitAsync(client, operation, "failure");
        var result = (await response.Content.ReadFromJsonAsync<PixResult>())!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PixOperations\" SET \"NextAttemptAt\" = now() - interval '1 second' WHERE \"Id\" = {result.Id}");
            Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), unavailable, NullLogger.Instance, CancellationToken.None));
        }
        Assert.Equal(600, await BalanceAsync(client, account));
        Assert.Equal("RequiresReview", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{result.Id}"))!.Status);
        Assert.False(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), unavailable, NullLogger.Instance, CancellationToken.None));
        using var retry = await client.PostAsync($"/api/v1/pix/operations/{result.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("Confirmed", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{result.Id}"))!.Status);
        Assert.Equal(600, await BalanceAsync(client, account));
    }

    [Fact]
    public async Task ConcurrentReservations_CannotSpendSameBalance_AndFailureIsIdempotent()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await AccountAsync(client);
        await CreditAsync(client, account, 100);
        var request = new PixRequest(account, Guid.NewGuid(), "00000000", "Outgoing", 80, "Confirm");
        var responses = await Task.WhenAll(SubmitAsync(client, request, "first"), SubmitAsync(client, request, "second"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var failed = (await responses.Single(response => response.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<PixResult>())!;
            await CreditAsync(client, account, 100);
            await using var db = CreateContext();
            var failedKey = (await db.PixOperations.AsNoTracking().SingleAsync(item => item.Id == failed.Id)).IdempotencyKey;
            using var replay = await SubmitAsync(client, request, failedKey);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Equal(failed.Id, (await replay.Content.ReadFromJsonAsync<PixResult>())!.Id);
            using var conflict = await SubmitAsync(client, request with { AccountId = Guid.NewGuid() }, failedKey);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(120, await BalanceAsync(client, account));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task RejectedIncoming_DoesNotCreditAccount()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var created = await SubmitAsync(client, new PixRequest(account, key, "00000000", "Incoming", 100, "Reject"), "rejected");
        var operation = (await created.Content.ReadFromJsonAsync<PixResult>())!;
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(0, await BalanceAsync(client, account));
        Assert.Equal("Rejected", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{operation.Id}"))!.Status);
    }

    [Fact]
    public async Task CancellationAfterSimulatorResponse_RetriesWithoutDoubleSettlement()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        using var cancellation = new CancellationTokenSource();
        using var interrupted = new HttpClient(new InterruptAfterReplyHandler(simulatorClient, cancellation))
            { BaseAddress = new Uri("http://localhost/") };
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        await CreditAsync(client, account, 1000);
        using var created = await SubmitAsync(client,
            new PixRequest(account, Guid.NewGuid(), "00000000", "Outgoing", 400, "Confirm"), "interrupt");
        var operation = (await created.Content.ReadFromJsonAsync<PixResult>())!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PixWorker.ProcessNextAsync(
            services.GetRequiredService<IServiceScopeFactory>(), interrupted, NullLogger.Instance, cancellation.Token));
        Assert.Equal("Pending", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{operation.Id}"))!.Status);
        Assert.Equal(600, await BalanceAsync(client, account));
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.False(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        var receipt = (await client.GetFromJsonAsync<PixReceipt>($"/api/v1/pix/operations/{operation.Id}/receipt"))!;
        Assert.Equal(new[] { "Pending", "Confirmed" }, receipt.Transitions.Select(item => item.Status));
        Assert.Equal(600, await BalanceAsync(client, account));
    }

    [Fact]
    public async Task InvalidSimulatorResponse_RequiresReviewWithoutReleasingReservation()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var invalid = new HttpClient(new InvalidResponseHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        await CreditAsync(client, account, 100);
        using var created = await SubmitAsync(client,
            new PixRequest(account, Guid.NewGuid(), "00000000", "Outgoing", 40, "Confirm"), "invalid-response");
        var operation = (await created.Content.ReadFromJsonAsync<PixResult>())!;
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), invalid, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("RequiresReview", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{operation.Id}"))!.Status);
        Assert.Equal(60, await BalanceAsync(client, account));
    }

    private sealed class InterruptAfterReplyHandler(HttpClient simulator, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync(cancellationToken))
            };
            copy.Content.Headers.ContentType = request.Content.Headers.ContentType;
            var response = await simulator.SendAsync(copy, cancellationToken);
            cancellation.Cancel();
            return response;
        }
    }

    private sealed class InvalidResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { operationId = Guid.NewGuid(), status = "Confirmed", simulated = true }) });
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed record IdResponse(Guid Id);
    private sealed record AccountResponse(Guid Id, long BalanceMinor);
}
