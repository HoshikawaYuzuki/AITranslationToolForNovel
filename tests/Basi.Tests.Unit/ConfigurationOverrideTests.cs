using System.Diagnostics;
using Basi.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Basi.Tests.Unit;

/// <summary>
/// Disables test parallelization for configuration override tests that mutate
/// process-level environment variables, ensuring complete isolation.
/// </summary>
[CollectionDefinition("ConfigurationTests", DisableParallelization = true)]
public class ConfigurationTestCollection : ICollectionFixture<object>
{
}

/// <summary>
/// Verifies configuration provider wiring, environment variable mapping,
/// and local JSON override behavior on the actual application host (Program.cs composition).
/// Addresses R1 and R2 audit requirements.
/// </summary>
[Collection("ConfigurationTests")]
public class ConfigurationOverrideTests
{
    private const string ConnectionStringEnvVar = "ConnectionStrings__DefaultConnection";

    [Fact]
    public void EnvironmentVariable_Overrides_BaseConfiguration_OnActualHost()
    {
        // R1.1 & R1.3 & R1.4: Real environment variable provider, real Program.cs host,
        // verifies BasiDbContext receives the overridden connection string without opening DB connection.
        var originalEnv = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        var tempDir = CreateIsolatedContentRoot(includeLocalJson: false);

        try
        {
            var expectedConnStr = "Host=env-postgres;Port=5433;Database=basi_env;Username=env_user;Password=env_pass";
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, expectedConnStr);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(tempDir);
                });

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var actualConnStr = db.Database.GetConnectionString();

            Assert.Equal(expectedConnStr, actualConnStr);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, originalEnv);
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public void LocalJsonFile_Overrides_BaseConfiguration_OnActualHost()
    {
        // R1.2 & R1.3 & R1.4 & R1.6: Real appsettings.Local.json in content root,
        // verifies Program.cs AddJsonFile("appsettings.Local.json") successfully overrides base settings.
        var tempDir = CreateIsolatedContentRoot(
            includeLocalJson: true,
            localConnStr: "Host=local-override-host;Port=5433;Database=basi_local;Username=local_user;Password=local_pass");

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(tempDir);
                });

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var actualConnStr = db.Database.GetConnectionString();

            Assert.Equal(
                "Host=local-override-host;Port=5433;Database=basi_local;Username=local_user;Password=local_pass",
                actualConnStr);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public void MissingLocalJsonFile_AllowsSuccessfulStartup_OnActualHost()
    {
        // R1.5: Missing appsettings.Local.json must not fail startup (optional: true).
        var tempDir = CreateIsolatedContentRoot(includeLocalJson: false);

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(tempDir);
                });

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var actualConnStr = db.Database.GetConnectionString();

            // Falls back to base appsettings.json DefaultConnection
            Assert.Equal(
                "Host=basehost;Port=5433;Database=basi_base;Username=base_user;Password=base_pass",
                actualConnStr);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public void PrecedencePolicy_LocalJsonFile_TakesPrecedenceOverEnvironmentVariable_WhenBothPresent()
    {
        // R2.1 & R2.2: Proves the precedence policy of Program.cs where appsettings.Local.json
        // is registered after default providers, allowing it to override environment variables.
        var originalEnv = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        var tempDir = CreateIsolatedContentRoot(
            includeLocalJson: true,
            localConnStr: "Host=local-json-winner;Port=5433;Database=basi_local;Username=local_user;Password=local_pass");

        try
        {
            Environment.SetEnvironmentVariable(
                ConnectionStringEnvVar,
                "Host=env-loser;Port=5433;Database=basi_env;Username=env_user;Password=env_pass");

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(tempDir);
                });

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var actualConnStr = db.Database.GetConnectionString();

            Assert.Equal(
                "Host=local-json-winner;Port=5433;Database=basi_local;Username=local_user;Password=local_pass",
                actualConnStr);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, originalEnv);
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public void PrecedencePolicy_EnvironmentVariable_TakesPrecedenceOverBaseAppSettings_WhenLocalJsonAbsent()
    {
        // R2.1 & R2.2: When appsettings.Local.json is absent (default state),
        // environment variables take precedence over base appsettings.json.
        var originalEnv = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        var tempDir = CreateIsolatedContentRoot(includeLocalJson: false);

        try
        {
            var expectedEnvConnStr = "Host=env-winner;Port=5433;Database=basi_env;Username=env_user;Password=env_pass";
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, expectedEnvConnStr);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseContentRoot(tempDir);
                });

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var actualConnStr = db.Database.GetConnectionString();

            Assert.Equal(expectedEnvConnStr, actualConnStr);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, originalEnv);
            CleanupDirectory(tempDir);
        }
    }

    private static string CreateIsolatedContentRoot(bool includeLocalJson, string? localConnStr = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BasiConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Minimal base appsettings.json
        var baseJson = """
        {
          "Logging": { "LogLevel": { "Default": "Warning" } },
          "ConnectionStrings": {
            "DefaultConnection": "Host=basehost;Port=5433;Database=basi_base;Username=base_user;Password=base_pass"
          }
        }
        """;
        File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), baseJson);

        if (includeLocalJson && !string.IsNullOrEmpty(localConnStr))
        {
            var localJson = $$"""
            {
              "ConnectionStrings": {
                "DefaultConnection": "{{localConnStr}}"
              }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "appsettings.Local.json"), localJson);
        }

        return tempDir;
    }

    private static void CleanupDirectory(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in tests
        }
    }
}
