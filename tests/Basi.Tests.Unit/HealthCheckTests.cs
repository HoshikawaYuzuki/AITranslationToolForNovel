using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Basi.Tests.Unit;

/// <summary>
/// Custom factory for health check unit tests.
/// Explicitly configures an unreachable loopback port with Timeout=1 so that
/// database connectivity failure is guaranteed and deterministic, and completes
/// within the bounded timeout without hanging.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
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
    }
}

public class HealthCheckTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthCheckTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsCorrectShape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("0.2.0", root.GetProperty("version").GetString());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("database", out var dbProp));
        var dbStatus = dbProp.GetString();
        Assert.True(dbStatus == "connected" || dbStatus == "disconnected",
            $"database field must be 'connected' or 'disconnected', got: '{dbStatus}'");
    }

    [Fact]
    public async Task HealthEndpoint_DatabaseField_IsDisconnectedWhenNoDb()
    {
        // When PostgreSQL is not available (guaranteed by test factory config),
        // the endpoint must return 200 with database="disconnected" within the bounded timeout.
        var client = _factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();

        var response = await client.GetAsync("/api/health");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var dbStatus = doc.RootElement.GetProperty("database").GetString();
        Assert.Equal("disconnected", dbStatus);

        // Verify bounded execution time (must complete within 3 seconds, well within timeout limits)
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Offline health check took {stopwatch.ElapsedMilliseconds}ms, exceeding the 3s bounded threshold");
    }
}
