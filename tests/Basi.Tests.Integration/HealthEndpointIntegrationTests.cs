using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Basi.Tests.Integration;

/// <summary>
/// Custom factory for integration tests.
/// Explicitly configures an unreachable loopback port with Timeout=1 so that
/// database connectivity failure is guaranteed and deterministic, and completes
/// within the bounded timeout without hanging.
/// </summary>
public class IntegrationTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=54329;Database=basi_offline;Username=none;Password=none;Timeout=1;CommandTimeout=1",
                ["Google:ClientId"] = "test-client-id",
                ["Google:ClientSecret"] = "test-client-secret",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Ephemeral data protection provider avoids sandbox registry/filesystem permission failures
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
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
    public async Task HealthEndpoint_VersionIs0_2_0()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("0.2.0", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task HealthEndpoint_DatabaseField_IsDisconnectedInOfflineEnvironment()
    {
        var client = _factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();

        var response = await client.GetAsync("/api/health");
        stopwatch.Stop();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var dbStatus = doc.RootElement.GetProperty("database").GetString();

        // Must strictly assert disconnected in offline environment
        Assert.Equal("disconnected", dbStatus);

        // Must verify bounded execution time
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Offline health check took {stopwatch.ElapsedMilliseconds}ms, exceeding 3s bounded threshold");
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
