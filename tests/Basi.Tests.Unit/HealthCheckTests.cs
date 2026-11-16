using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Basi.Tests.Unit;

public class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
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
        Assert.True(dbStatus == "connected" || dbStatus == "disconnected");
    }
}
