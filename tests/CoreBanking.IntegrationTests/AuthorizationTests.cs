using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using CoreBanking.Application.Pix;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CoreBanking.IntegrationTests;

public sealed class AuthorizationTests
{
    private const string Issuer = "https://issuer.example/realms/corebanking";
    private const string Audience = "corebanking-api";

    [Theory]
    [InlineData("Production", "Disabled", null, null)]
    [InlineData("Production", "Oidc", "http://127.0.0.1:8080/realms/corebanking", Audience)]
    [InlineData("Production", "Oidc", Issuer, null)]
    [InlineData("Development", "Oidc", "http://example.com/realms/corebanking", Audience)]
    public void UnsafeOrIncompleteConfiguration_FailsAtStartup(string environment, string mode, string? authority, string? audience)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Banking", "Host=localhost;Database=unused;Username=unused");
            builder.UseSetting("Authentication:Mode", mode);
            builder.UseSetting("Authentication:Authority", authority);
            builder.UseSetting("Authentication:Audience", audience);
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task DisabledAuthentication_RejectsMissingRemoteAddress()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DisabledAuthentication_RejectsNonLoopbackClients()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseTestRemoteAddress(IPAddress.Parse("192.0.2.1")));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RequireTheirIndependentRoles_AndValidateTokens()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Banking", "Host=localhost;Database=unused;Username=unused");
            builder.UseSetting("Authentication:Mode", "Oidc");
            builder.UseSetting("Authentication:Authority", Issuer);
            builder.UseSetting("Authentication:Audience", Audience);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                    config.SigningKeys.Add(key);
                    options.Configuration = config;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
                services.AddScoped<IPixService, StubPixService>();
            });
        });
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var endpoints = new (HttpMethod Method, string Path, string Role)[]
        {
            (HttpMethod.Post, "/api/v1/customers", "operador"),
            (HttpMethod.Get, $"/api/v1/customers/{id}", "operador"),
            (HttpMethod.Post, "/api/v1/accounts", "operador"),
            (HttpMethod.Get, $"/api/v1/accounts/{id}", "operador"),
            (HttpMethod.Post, "/api/v1/pix/keys", "operador"),
            (HttpMethod.Get, $"/api/v1/pix/keys/{id}", "operador"),
            (HttpMethod.Get, $"/api/v1/transfers/{id}", "operador"),
            (HttpMethod.Get, $"/api/v1/deposits/{id}", "operador"),
            (HttpMethod.Get, $"/api/v1/pix/operations/{id}", "operador"),
            (HttpMethod.Get, $"/api/v1/pix/operations/{id}/receipt", "operador"),
            (HttpMethod.Post, "/api/v1/transfers", "financeiro"),
            (HttpMethod.Post, "/api/v1/deposits", "financeiro"),
            (HttpMethod.Post, "/api/v1/pix/operations", "financeiro"),
            (HttpMethod.Post, $"/api/v1/pix/operations/{id}/retry", "administrador")
        };
        foreach (var endpoint in endpoints)
        {
            using var anonymous = new HttpRequestMessage(endpoint.Method, endpoint.Path);
            using var unauthenticated = await client.SendAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

            var otherRole = endpoint.Role == "operador" ? "financeiro" : "operador";
            using var forbiddenRequest = new HttpRequestMessage(endpoint.Method, endpoint.Path);
            forbiddenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(key, otherRole));
            using var forbidden = await client.SendAsync(forbiddenRequest);
            Assert.True(forbidden.StatusCode == HttpStatusCode.Forbidden,
                $"{endpoint.Path}: {forbidden.StatusCode} {string.Join(" ", forbidden.Headers.WwwAuthenticate)}");
        }

        foreach (var (path, role) in new[] { ("/api/v1/pix/keys", "operador"), ("/api/v1/deposits", "financeiro") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(key, role));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pix/operations/{id}/retry");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(key, "administrador"));
        using var retryResponse = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);

        using var wrongKeyRsa = RSA.Create(2048);
        var invalidTokens = new (string Token, HttpStatusCode Status)[]
        {
            (Token(key, "operador", issuer: "https://other.example"), HttpStatusCode.Unauthorized),
            (Token(key, "operador", audience: "other-api"), HttpStatusCode.Unauthorized),
            (Token(key, "operador", expires: DateTime.UtcNow.AddMinutes(-10)), HttpStatusCode.Unauthorized),
            (Token(new RsaSecurityKey(wrongKeyRsa) { KeyId = key.KeyId }, "operador"), HttpStatusCode.Unauthorized),
            (Token(key, "operador", tokenType: "ID"), HttpStatusCode.Forbidden)
        };
        foreach (var (token, expected) in invalidTokens)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pix/keys") { Content = JsonContent.Create(new { }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    private static string Token(SecurityKey key, string role, string issuer = Issuer, string audience = Audience,
        DateTime? expires = null, string tokenType = "Bearer") =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
            [new Claim("roles", role), new Claim("typ", tokenType)],
            expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));

    private sealed class StubPixService : IPixService
    {
        public Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Guid?> CreateKeyAsync(Guid accountId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PixKeyResult?> FindKeyAsync(Guid id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PixResponse> CreateAsync(PixRequest request, string key, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PixResult?> FindAsync(Guid id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PixReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
