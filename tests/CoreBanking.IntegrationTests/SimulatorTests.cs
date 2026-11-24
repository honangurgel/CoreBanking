using System.Net;
using System.Net.Http.Json;
using CoreBanking.Simulator;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CoreBanking.IntegrationTests;

public sealed class SimulatorTests
{
    [Theory]
    [InlineData("Confirm", "Confirmed")]
    [InlineData("Reject", "Rejected")]
    public async Task SameRequest_ReturnsSameSimulatedDecision(string scenario, string expected)
    {
        using var factory = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var client = factory.CreateClient();
        var request = new SimulatedSettlementRequest(Guid.NewGuid(), Guid.NewGuid(), "00000000", "Outgoing", 100, scenario, 0);
        using var first = await client.PostAsJsonAsync("/api/v1/settlements", request);
        using var second = await client.PostAsJsonAsync("/api/v1/settlements", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<SimulatedSettlementResult>();
        Assert.Equal(new SimulatedSettlementResult(request.OperationId, expected, true), result);
        Assert.Equal(result, await second.Content.ReadFromJsonAsync<SimulatedSettlementResult>());
    }

    [Fact]
    public async Task UnavailabilityAndInvalidRequests_AreExplicit()
    {
        using var factory = new WebApplicationFactory<SimulatorAssemblyMarker>();
        using var client = factory.CreateClient();
        var request = new SimulatedSettlementRequest(Guid.NewGuid(), Guid.NewGuid(), "60701190", "Incoming", 1, "Unavailable", 0);
        using var unavailable = await client.PostAsJsonAsync("/api/v1/settlements", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/settlements", request with { InstitutionIspb = "99999999" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var amount = await client.PostAsJsonAsync("/api/v1/settlements", request with { AmountMinor = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, amount.StatusCode);
        using var delay = await client.PostAsJsonAsync("/api/v1/settlements", request with { DelayMilliseconds = 10001 });
        Assert.Equal(HttpStatusCode.BadRequest, delay.StatusCode);
    }
}
