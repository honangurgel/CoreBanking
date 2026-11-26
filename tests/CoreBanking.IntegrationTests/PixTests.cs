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
using Microsoft.Extensions.Logging;
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
        builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", postgres.GetConnectionString());
            builder.UseTestLoopback();
        });
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
        using var logs = new RecordingLogProvider();
        using var api = CreateApi().WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>().WithWebHostBuilder(
            builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var simulatorClient = simulator.CreateClient();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
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
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, loggerFactory.CreateLogger<PixWorker>(), CancellationToken.None),
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, loggerFactory.CreateLogger<PixWorker>(), CancellationToken.None));
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
        Assert.Contains(logs.Entries, item => item.Category == "Program"
            && Equals(item.Properties.GetValueOrDefault("OperationId"), result.Id)
            && item.Properties.ContainsKey("RequestId"));
        Assert.Contains(logs.Entries, item => item.Category == "CoreBanking.Worker.PixWorker"
            && Equals(item.Properties.GetValueOrDefault("OperationId"), result.Id)
            && Equals(item.Properties.GetValueOrDefault("Status"), status));
        Assert.Contains(logs.Entries, item => item.Category == "CoreBanking.Simulator.Settlements"
            && Equals(item.Properties.GetValueOrDefault("OperationId"), result.Id)
            && Equals(item.Properties.GetValueOrDefault("Status"), status));
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
    public async Task BacklogMetrics_ReportPendingOperations()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        var account = await AccountAsync(client);
        using var response = await SubmitAsync(client,
            new PixRequest(account, Guid.NewGuid(), "00000000", "Outgoing", 1, "Confirm"), "backlog-metrics");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        keyResponse.EnsureSuccessStatusCode();
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        using var pending = await SubmitAsync(client,
            new PixRequest(account, key, "00000000", "Incoming", 1, "Confirm"), "pending-metrics");
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);

        using var metrics = new WorkerMetricRecorder();
        await using var services = CreateServices();
        using var monitor = new BacklogMonitor(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BacklogMonitor>.Instance);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!metrics.Counts.Any(item => item.Name == "corebanking.backlog.count" && item.Tag == "outbox")
                && DateTime.UtcNow < deadline)
            {
                if (monitor.ExecuteTask is { IsFaulted: true }) await monitor.ExecuteTask;
                await Task.Delay(100);
            }
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.backlog.count"
                && item.Tag == "pix" && item.Value == 1);
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.backlog.count"
                && item.Tag == "outbox" && item.Value == 0);
            Assert.Contains(metrics.Durations, item => item.Name == "corebanking.backlog.oldest.age" && item.Value >= 0);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unavailable_EntersReviewWithReservationUntilManualRetry()
    {
        using var metrics = new WorkerMetricRecorder();
        using var logs = new RecordingLogProvider();
        using var api = CreateApi().WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        using var unavailable = new HttpClient(new UnavailableHandler()) { BaseAddress = new Uri("http://localhost/") };
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        var workerLogger = loggerFactory.CreateLogger<PixWorker>();
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
            Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), unavailable, workerLogger, CancellationToken.None));
        }
        Assert.Equal(600, await BalanceAsync(client, account));
        Assert.Equal("RequiresReview", (await client.GetFromJsonAsync<PixResult>($"/api/v1/pix/operations/{result.Id}"))!.Status);
        Assert.False(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), unavailable, NullLogger.Instance, CancellationToken.None));
        using var retry = await client.PostAsync($"/api/v1/pix/operations/{result.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var duplicateRetry = await client.PostAsync($"/api/v1/pix/operations/{result.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicateRetry.StatusCode);
        await using (var auditDb = CreateContext())
        {
            var audits = await auditDb.Database.SqlQuery<long>($"""
                SELECT count(*) AS "Value" FROM "OperationAuditEvents"
                WHERE "OperationId" = {result.Id} AND "Action" = 'PixRetry' AND "Outcome" = 'Pending'
                  AND "ActorKind" = 'LocalAnonymous'
                """).SingleAsync();
            Assert.Equal(1, audits);
        }
        Assert.Contains(logs.Entries, item => item.Category == "Program"
            && Equals(item.Properties.GetValueOrDefault("OperationId"), result.Id)
            && Equals(item.Properties.GetValueOrDefault("RetryAccepted"), true));
        Assert.Contains(logs.Entries, item => item.Category == "CoreBanking.Worker.PixWorker"
            && Equals(item.Properties.GetValueOrDefault("OperationId"), result.Id)
            && Equals(item.Properties.GetValueOrDefault("Status"), "RequiresReview")
            && Equals(item.Properties.GetValueOrDefault("Attempts"), 3));
        Assert.Contains(metrics.Counts, item => item.Name == "corebanking.pix.processed"
            && item.Value == 1 && item.Tag == "RequiresReview");
        Assert.Contains(metrics.Durations, item => item.Name == "corebanking.pix.simulator.duration" && item.Value >= 0);
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
            using var unknownAccount = await SubmitAsync(client, request with { AccountId = Guid.NewGuid() }, failedKey);
            Assert.Equal(HttpStatusCode.NotFound, unknownAccount.StatusCode);
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
        await using (var db = CreateContext())
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM "PixThrottleCalls" """).SingleAsync());
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

    [Fact]
    public async Task SharedRateLimit_DefersOperationsWithoutConsumingAttempts()
    {
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var request = new PixRequest(account, key, "00000000", "Incoming", 10, "Confirm");
        using var first = await SubmitAsync(client, request, "rate-first");
        using var second = await SubmitAsync(client, request, "rate-second");
        await using (var db = CreateContext())
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "PixThrottleCalls" ("StartedAt")
                SELECT clock_timestamp() FROM generate_series(1, 599)
                """);

        var calls = await Task.WhenAll(
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None),
            PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Single(calls, processed => processed);
        await using (var db = CreateContext())
        {
            var pending = await db.PixOperations.AsNoTracking().SingleAsync(item => item.Status == "Pending");
            Assert.Equal(0, pending.Attempts);
            Assert.Equal(1, await db.PixOperations.CountAsync(item => item.Status == "Confirmed"));
            Assert.Equal(600, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM "PixThrottleCalls" """).SingleAsync());
            await db.Database.ExecuteSqlRawAsync("""UPDATE "PixThrottleCalls" SET "StartedAt" = clock_timestamp() - interval '61 seconds'""");
        }
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(20, await BalanceAsync(client, account));
    }

    [Fact]
    public async Task SharedConcurrencyLimit_DefersOtherWorkersUntilCallFinishes()
    {
        using var metrics = new WorkerMetricRecorder();
        using var api = CreateApi();
        using var client = api.CreateClient();
        using var simulator = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var simulatorClient = simulator.CreateClient();
        var handler = new BlockingHandler(simulatorClient);
        using var blocking = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var services = CreateServices();
        var account = await AccountAsync(client);
        using var keyResponse = await client.PostAsJsonAsync("/api/v1/pix/keys", new { accountId = account });
        var key = (await keyResponse.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var request = new PixRequest(account, key, "00000000", "Incoming", 10, "Confirm");
        using var first = await SubmitAsync(client, request, "concurrent-first");
        using var second = await SubmitAsync(client, request, "concurrent-second");
        await using (var db = CreateContext())
            await db.Database.ExecuteSqlRawAsync("""UPDATE "PixThrottleSettings" SET "MaxConcurrent" = 1""");
        var processing = PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), blocking, NullLogger.Instance, CancellationToken.None);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
            Assert.Contains(metrics.Counts, item => item.Name == "corebanking.pix.throttled" && item.Value == 1);
        }
        finally
        {
            handler.Release.TrySetResult();
            await processing;
        }
        Assert.True(await processing);
        Assert.True(await PixWorker.ProcessNextAsync(services.GetRequiredService<IServiceScopeFactory>(), simulatorClient, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(20, await BalanceAsync(client, account));
    }

    private sealed class BlockingHandler(HttpClient simulator) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync(cancellationToken))
            };
            copy.Content.Headers.ContentType = request.Content.Headers.ContentType;
            return await simulator.SendAsync(copy, cancellationToken);
        }
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
