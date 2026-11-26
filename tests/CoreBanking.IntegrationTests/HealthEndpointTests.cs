using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace CoreBanking.IntegrationTests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealth_ReturnsHealthyStatus()
    {
        using var local = factory.WithWebHostBuilder(builder => builder.UseTestLoopback());
        using var client = local.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.NotNull(result);
        Assert.Equal("Healthy", result.Status);
    }

    [Fact]
    public async Task Requests_AboveConfiguredLimit_ReturnTooManyRequestsExceptHealth()
    {
        using var limited = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:PermitsPerMinute", "2");
            builder.UseTestLoopback();
        });
        using var client = limited.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/v1/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    private sealed record HealthResponse(string Status);
}

public sealed class DependencyHealthTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public async Task InitializeAsync() => await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await rabbit.DisposeAsync();
        await postgres.DisposeAsync();
    }

    [Fact]
    public async Task Readiness_ReportsEachDependencyAndKeepsLivenessIndependent()
    {
        var simulatorState = "Healthy";
        var builder = WebApplication.CreateSlimBuilder();
        await using var simulator = builder.Build();
        simulator.Urls.Add("http://127.0.0.1:0");
        simulator.MapGet("/health", () => simulatorState switch
        {
            "Healthy" => Results.Ok(new { status = "Healthy", simulated = true }),
            "Invalid" => Results.Ok(new { status = "Healthy", simulated = false }),
            _ => Results.StatusCode(503)
        });
        await simulator.StartAsync();
        var simulatorUrl = simulator.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        using var factory = CreateFactory(postgres.GetConnectionString(), rabbit.GetConnectionString(), simulatorUrl);
        using var client = factory.CreateClient();
        await AssertReadinessAsync(client, HttpStatusCode.OK, "Healthy", "Healthy", "Healthy");

        simulatorState = "Unavailable";
        await AssertReadinessAsync(client, HttpStatusCode.ServiceUnavailable, "Healthy", "Healthy", "Unhealthy");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        simulatorState = "Invalid";
        await AssertReadinessAsync(client, HttpStatusCode.ServiceUnavailable, "Healthy", "Healthy", "Unhealthy");
        simulatorState = "Healthy";

        using var badDatabase = CreateFactory("Host=127.0.0.1;Port=1;Database=missing;Username=missing;Timeout=1",
            rabbit.GetConnectionString(), simulatorUrl);
        using var badDatabaseClient = badDatabase.CreateClient();
        await AssertReadinessAsync(badDatabaseClient, HttpStatusCode.ServiceUnavailable, "Unhealthy", "Healthy", "Healthy");

        using var badBroker = CreateFactory(postgres.GetConnectionString(), "amqp://guest:guest@127.0.0.1:1/", simulatorUrl);
        using var badBrokerClient = badBroker.CreateClient();
        await AssertReadinessAsync(badBrokerClient, HttpStatusCode.ServiceUnavailable, "Healthy", "Unhealthy", "Healthy");

        using var missingBroker = CreateFactory(postgres.GetConnectionString(), "", simulatorUrl);
        using var missingBrokerClient = missingBroker.CreateClient();
        await AssertReadinessAsync(missingBrokerClient, HttpStatusCode.ServiceUnavailable, "Healthy", "Unhealthy", "Healthy");

        using var missingSimulator = CreateFactory(postgres.GetConnectionString(), rabbit.GetConnectionString(), "");
        using var missingSimulatorClient = missingSimulator.CreateClient();
        await AssertReadinessAsync(missingSimulatorClient, HttpStatusCode.ServiceUnavailable, "Healthy", "Healthy", "Unhealthy");

        using var remoteSimulator = CreateFactory(postgres.GetConnectionString(), rabbit.GetConnectionString(), "http://example.com");
        using var remoteSimulatorClient = remoteSimulator.CreateClient();
        await AssertReadinessAsync(remoteSimulatorClient, HttpStatusCode.ServiceUnavailable, "Healthy", "Healthy", "Unhealthy");
    }

    private static WebApplicationFactory<Program> CreateFactory(string database, string broker, string simulator) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Banking", database);
            builder.UseTestLoopback();
            builder.UseSetting("RabbitMQ:ConnectionString", broker);
            builder.UseSetting("Pix:SimulatorUrl", simulator);
        });

    private static async Task AssertReadinessAsync(HttpClient client, HttpStatusCode expectedStatus,
        string postgresStatus, string rabbitStatus, string simulatorStatus)
    {
        using var response = await client.GetAsync("/health/ready");
        Assert.True(response.StatusCode == expectedStatus,
            $"Expected {expectedStatus}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var result = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        Assert.NotNull(result);
        Assert.Equal(expectedStatus == HttpStatusCode.OK ? "Healthy" : "Unhealthy", result.Status);
        Assert.Equal(postgresStatus, result.Checks["postgres"]);
        Assert.Equal(rabbitStatus, result.Checks["rabbitmq"]);
        Assert.Equal(simulatorStatus, result.Checks["simulator"]);
    }

    private sealed record ReadinessResponse(string Status, Dictionary<string, string> Checks);
}
