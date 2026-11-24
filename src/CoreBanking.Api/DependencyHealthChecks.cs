using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using RabbitMQ.Client;

namespace CoreBanking.Api;

public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("Banking")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:Banking.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

public sealed class RabbitMqHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var address = configuration["RabbitMQ:ConnectionString"]
            ?? throw new InvalidOperationException("Configure RabbitMQ:ConnectionString.");
        var factory = new ConnectionFactory
        {
            Uri = new Uri(address),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

public sealed class SimulatorHealthCheck(IConfiguration configuration, HttpClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var address = configuration["Pix:SimulatorUrl"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return HealthCheckResult.Unhealthy("Configure Pix:SimulatorUrl como origem HTTP local.");

        using var response = await client.GetAsync(new Uri(uri, "/health"), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return HealthCheckResult.Unhealthy("Simulador indisponível.");

        var result = await response.Content.ReadFromJsonAsync<SimulatorHealth>(cancellationToken);
        return result is { Status: "Healthy", Simulated: true }
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Resposta inválida do simulador.");
    }

    private sealed record SimulatorHealth(string Status, bool Simulated);
}
