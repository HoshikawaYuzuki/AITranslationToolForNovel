using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Basi.Tests.Integration;

/// <summary>
/// Custom factory for integration tests.
/// Sets ASPNETCORE_ENVIRONMENT=Testing so appsettings.Testing.json is loaded,
/// providing a valid connection string without requiring a live PostgreSQL server.
/// </summary>
public class IntegrationTestFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
    }
}

/// <summary>
/// Integration tests for the Basi API health endpoint.
/// These run against a real in-process host (no mocking of routing/middleware).
/// </summary>
public class HealthEndpointIntegrationTests : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory;

    public HealthEndpointIntegrationTests(IntegrationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsValidJson()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        // Must be valid JSON — JsonDocument.Parse throws on invalid input
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task HealthEndpoint_StatusIsHealthy()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthEndpoint_VersionIs0_1_0()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task HealthEndpoint_DatabaseField_IsConnectedOrDisconnected()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var dbStatus = doc.RootElement.GetProperty("database").GetString();
        Assert.True(
            dbStatus is "connected" or "disconnected",
            $"Expected 'connected' or 'disconnected', got: '{dbStatus}'");
    }

    [Fact]
    public async Task HealthEndpoint_TimestampIsIso8601()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var ts = doc.RootElement.GetProperty("timestamp").GetString();
        Assert.True(
            DateTimeOffset.TryParse(ts, out _),
            $"timestamp is not a valid ISO-8601 date: '{ts}'");
    }
}

