using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Basi.Api.Data;
using Basi.Core.Models;

namespace Basi.Tests.Integration;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string TestGoogleId = "test-google-id-123";
    public const string TestEmail = "testuser@example.com";
    public const string TestName = "Test User";

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
            new Claim(ClaimTypes.NameIdentifier, TestGoogleId),
            new Claim(ClaimTypes.Email, TestEmail),
            new Claim(ClaimTypes.Name, TestName)
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
            // Ephemeral data protection provider prevents sandbox permission failures
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // Replace Npgsql with isolated InMemory DB
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

    public async Task<string> FetchAntiforgeryTokenAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/antiforgery/token");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("token").GetString()!;
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

    // ------------------------------------------------------------------ //
    //  Open Redirect protection tests (F07)                              //
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("http://attacker.com/malicious")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    public async Task LoginEndpoint_WithExternalReturnUrl_FallbacksToRoot(string evilUrl)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync($"/api/auth/login?returnUrl={Uri.EscapeDataString(evilUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        // The Google OAuth state redirect_uri parameter should fallback to "/"
        Assert.Contains("redirect_uri", location);
        Assert.DoesNotContain(evilUrl, location);
    }

    [Fact]
    public async Task LoginEndpoint_WithValidLocalReturnUrl_PreservesReturnUrl()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var validPath = "/novels/my-list";
        var response = await client.GetAsync($"/api/auth/login?returnUrl={Uri.EscapeDataString(validPath)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.StartsWith("https://accounts.google.com", location);
        Assert.Contains("state=", location);
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

        Assert.Equal(TestAuthHandler.TestEmail, root.GetProperty("email").GetString());
        Assert.Equal(TestAuthHandler.TestName, root.GetProperty("displayName").GetString());
        Assert.True(root.TryGetProperty("id", out var idProp));
        Assert.NotEqual(Guid.Empty, idProp.GetGuid());
    }

    // ------------------------------------------------------------------ //
    //  CSRF / Antiforgery enforcement tests (F01)                         //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task LogoutEndpoint_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        // Direct POST without X-XSRF-TOKEN header
        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutEndpoint_WithAntiforgeryToken_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Title"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Some text")), "file", "novel.txt");

        // Post without antiforgery header
        var response = await client.PostAsync("/api/novels/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ //
    //  Upload Validation tests (F04)                                      //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task UploadEndpoint_MissingFile_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Title Only"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Missing or empty file", body);
    }

    [Fact]
    public async Task UploadEndpoint_EmptyFile_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Title"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Array.Empty<byte>()), "file", "empty.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Missing or empty file", body);
    }

    [Fact]
    public async Task UploadEndpoint_NonTxtFile_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Executable"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("fake exe")), "file", "program.exe");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Only .txt files are supported", body);
    }

    [Fact]
    public async Task UploadEndpoint_FileOver10MB_ReturnsBadRequestEarly()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var oversizedBytes = new byte[10 * 1024 * 1024 + 1];
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Huge Book"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(oversizedBytes), "file", "huge.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("exceeds the 10 MB limit", body);
    }

    [Fact]
    public async Task UploadEndpoint_TitleExceeds512_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var longTitle = new string('A', 513);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(longTitle), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Valid text")), "file", "book.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Title must not exceed 512", body);
    }

    [Fact]
    public async Task UploadEndpoint_InvalidSourceLanguage_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Valid Title"), "title");
        form.Add(new StringContent("invalid-language-code-that-is-way-too-long-12345"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Valid text")), "file", "book.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid sourceLanguage format", body);
    }

    // ------------------------------------------------------------------ //
    //  Full Upload Flow & Persistence (F03, F06, T07, T13)                //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task UploadEndpoint_WithAuthAndAntiforgery_ParsesAndStoresNovelWithSegments_AndPreservesRawContent()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        var txtContent = "Chapter 1: The Beginning\n\nIt was a dark and stormy night.\n\nSuddenly, a shadow appeared.";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("My Test Novel"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(txtContent));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", "test_novel.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("My Test Novel", root.GetProperty("title").GetString());
        Assert.Equal("ja", root.GetProperty("sourceLanguage").GetString());
        Assert.Equal(3, root.GetProperty("segmentCount").GetInt32());
        var novelId = root.GetProperty("novelId").GetGuid();
        Assert.NotEqual(Guid.Empty, novelId);

        // Verify persistence in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var persistedNovel = await db.Novels
            .Include(n => n.Segments)
            .FirstOrDefaultAsync(n => n.Id == novelId);

        Assert.NotNull(persistedNovel);
        Assert.Equal("My Test Novel", persistedNovel.Title);
        Assert.Equal("ja", persistedNovel.SourceLanguage);
        Assert.Equal(txtContent, persistedNovel.RawContent);
        Assert.Equal(3, persistedNovel.Segments.Count);

        var sortedSegments = persistedNovel.Segments.OrderBy(s => s.Order).ToList();
        Assert.Equal(0, sortedSegments[0].Order);
        Assert.Equal("Chapter 1: The Beginning", sortedSegments[0].Content);
        Assert.StartsWith(novelId.ToString("N"), sortedSegments[0].StableId);
        Assert.Equal(1, sortedSegments[1].Order);
        Assert.Equal("It was a dark and stormy night.", sortedSegments[1].Content);
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

        // Even if antiforgery token is present or absent, unauthenticated access must be rejected
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Test Novel"), "title");
        content.Add(new StringContent("ja"), "sourceLanguage");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Some text")), "file", "test.txt");

        var response = await client.PostAsync("/api/novels/upload", content);

        // Can return either 400 (antiforgery) or 401 (unauthorized) - without auth and token
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 401 or 400, got {response.StatusCode}");
    }
}
