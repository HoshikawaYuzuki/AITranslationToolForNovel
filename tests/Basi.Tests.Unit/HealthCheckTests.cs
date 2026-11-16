using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Basi.Api.Data;

namespace Basi.Tests.Unit;

public class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory)
    {
        // Replace the real PostgreSQL DbContext with an in-memory SQLite so
        // tests don't need a running database.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove the real DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<BasiDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                // Add in-memory database
                services.AddDbContext<BasiDbContext>(options =>
                    options.UseInMemoryDatabase("TestDb"));
            });
        });
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
        // In-memory DB will show "disconnected" because CanConnectAsync on InMemory
        // doesn't behave like a real DB, but the shape is correct
        var dbStatus = dbProp.GetString();
        Assert.True(dbStatus == "connected" || dbStatus == "disconnected");
    }
}
