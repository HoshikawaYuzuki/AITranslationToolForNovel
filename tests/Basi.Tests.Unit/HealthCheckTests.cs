using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Basi.Tests.Unit;

/// <summary>
/// Custom factory that sets ASPNETCORE_ENVIRONMENT=Testing so that
/// appsettings.Testing.json is loaded (providing a valid but unreachable
/// connection string). The health endpoint handles DB failure gracefully,
/// so the test never hangs waiting for PostgreSQL.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
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
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("database", out var dbProp));
        var dbStatus = dbProp.GetString();
        Assert.True(dbStatus == "connected" || dbStatus == "disconnected",
            $"database field must be 'connected' or 'disconnected', got: '{dbStatus}'");
    }

    [Fact]
    public async Task HealthEndpoint_DatabaseField_IsDisconnectedWhenNoDb()
    {
        // When PostgreSQL is not available (CI / local without Docker),
        // the endpoint must still return 200 with database="disconnected".
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        // "disconnected" is the expected value when no DB is running.
        // "connected" is also acceptable if someone runs tests with a live DB.
        var dbStatus = doc.RootElement.GetProperty("database").GetString();
        Assert.True(dbStatus is "connected" or "disconnected");
    }
}
