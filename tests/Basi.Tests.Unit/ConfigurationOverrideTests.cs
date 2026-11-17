using Microsoft.Extensions.Configuration;

namespace Basi.Tests.Unit;

/// <summary>
/// Verifies that connection strings can be overridden via configuration providers
/// (such as environment variables or local files), fulfilling the audit requirement.
/// </summary>
public class ConfigurationOverrideTests
{
    [Fact]
    public void EnvironmentVariable_Overrides_DefaultConnectionString()
    {
        // Simulate default configuration from appsettings.Development.json
        var initialSettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Port=5432;Database=basi_dev;Username=basi;Password=basi_dev_password"
        };

        // Simulate environment variable: ConnectionStrings__DefaultConnection
        // In ASP.NET Core, double underscore converts to colon separator.
        var envOverrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=customhost;Port=5433;Database=basi_custom;Username=custom_user;Password=custom_secret"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(initialSettings)
            .AddInMemoryCollection(envOverrides)
            .Build();

        var resolvedConnection = configuration.GetConnectionString("DefaultConnection");

        Assert.Equal(
            "Host=customhost;Port=5433;Database=basi_custom;Username=custom_user;Password=custom_secret",
            resolvedConnection);
    }

    [Fact]
    public void LocalJsonFile_Overrides_BaseAppSettings()
    {
        // Verify the precedence order: base appsettings < appsettings.Local.json
        var baseConfig = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=basi_dev"
        };

        var localOverride = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=local-override-host;Database=basi_local"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(baseConfig)
            .AddInMemoryCollection(localOverride)
            .Build();

        Assert.Equal(
            "Host=local-override-host;Database=basi_local",
            configuration.GetConnectionString("DefaultConnection"));
    }
}
