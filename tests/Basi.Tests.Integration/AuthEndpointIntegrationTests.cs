using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Basi.Api.Data;

namespace Basi.Tests.Integration;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-google-id-123"),
            new Claim(ClaimTypes.Email, "testuser@example.com"),
            new Claim(ClaimTypes.Name, "Test User")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class AuthTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"basi_test_{Guid.NewGuid():N}";
    public bool EnableTestAuth { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=54329;Database=x;Username=x;Password=x;Timeout=1",
                ["Google:ClientId"] = "test-client-id",
                ["Google:ClientSecret"] = "test-client-secret",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<BasiDbContext>)
                         || d.ServiceType == typeof(BasiDbContext)
                         || d.ServiceType.FullName?.Contains("IDbContextOptionsConfiguration") == true)
                .ToList();
            foreach (var d in descriptorsToRemove)
                services.Remove(d);

            services.AddDbContext<BasiDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            if (EnableTestAuth)
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            }
        });
    }
}

public class AuthEndpointIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;

    public AuthEndpointIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
        _factory.EnableTestAuth = false;
    }

    [Fact]
    public async Task MeEndpoint_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LogoutEndpoint_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginEndpoint_WithoutAuth_Returns302ToGoogle()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/auth/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("accounts.google.com", location);
    }
}

public class AuthenticatedFlowIntegrationTests
{
    private readonly AuthTestFactory _factory;

    public AuthenticatedFlowIntegrationTests()
    {
        _factory = new AuthTestFactory { EnableTestAuth = true };
    }

    [Fact]
    public async Task MeEndpoint_WithAuth_CreatesAndReturnsUser()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("testuser@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Test User", root.GetProperty("displayName").GetString());
        Assert.True(root.TryGetProperty("id", out var idProp));
        Assert.NotEqual(Guid.Empty, idProp.GetGuid());
    }

    [Fact]
    public async Task UploadEndpoint_WithAuth_ParsesAndStoresNovelWithSegments()
    {
        var client = _factory.CreateClient();

        // Ensure user exists first via /api/auth/me
        var meRes = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);

        // Upload TXT
        var txtContent = "Chapter 1: The Beginning\n\nIt was a dark and stormy night.\n\nSuddenly, a shadow appeared.";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("My Test Novel"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(txtContent));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", "test_novel.txt");

        var response = await client.PostAsync("/api/novels/upload", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("My Test Novel", root.GetProperty("title").GetString());
        Assert.Equal("ja", root.GetProperty("sourceLanguage").GetString());
        Assert.Equal(3, root.GetProperty("segmentCount").GetInt32());
        Assert.True(root.TryGetProperty("novelId", out var novelIdProp));
        Assert.NotEqual(Guid.Empty, novelIdProp.GetGuid());
    }

    [Fact]
    public async Task UploadEndpoint_MissingFile_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/api/auth/me");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Title Only"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");

        var response = await client.PostAsync("/api/novels/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_MissingTitle_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/api/auth/me");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Content")), "file", "test.txt");

        var response = await client.PostAsync("/api/novels/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutEndpoint_WithAuth_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class NovelUploadIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;

    public NovelUploadIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
        _factory.EnableTestAuth = false;
    }

    [Fact]
    public async Task UploadEndpoint_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Test Novel"), "title");
        content.Add(new StringContent("ja"), "sourceLanguage");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Some text")), "file", "test.txt");

        var response = await client.PostAsync("/api/novels/upload", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
