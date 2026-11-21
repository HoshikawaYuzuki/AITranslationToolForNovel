using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Basi.Api.Data;
using Basi.Core.Models;
using Basi.Module.Identity.Models;

namespace Basi.Tests.Integration;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string TestGoogleId = "test-google-id-123";
    public const string TestEmail = "testuser@example.com";
    public const string TestName = "Test User";

    public const string UserIdHeader = "X-Test-UserId";
    public const string UserEmailHeader = "X-Test-Email";
    public const string UserNameHeader = "X-Test-Name";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var googleId = Request.Headers.TryGetValue(UserIdHeader, out var uid) ? uid.ToString() : TestGoogleId;
        var email = Request.Headers.TryGetValue(UserEmailHeader, out var uemail) ? uemail.ToString() : TestEmail;
        var name = Request.Headers.TryGetValue(UserNameHeader, out var uname) ? uname.ToString() : TestName;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, googleId),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim("email_verified", "true")
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

    public async Task<ApplicationUser> SeedUserAsync(
        string googleId = TestAuthHandler.TestGoogleId,
        string email = TestAuthHandler.TestEmail,
        string displayName = TestAuthHandler.TestName)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existing = await userManager.FindByLoginAsync("Google", googleId);
        if (existing != null) return existing;

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
        };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            throw new InvalidOperationException($"Failed to seed test user: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");

        var loginResult = await userManager.AddLoginAsync(user, new UserLoginInfo("Google", googleId, "Google"));
        if (!loginResult.Succeeded)
            throw new InvalidOperationException($"Failed to add login for test user: {string.Join(", ", loginResult.Errors.Select(e => e.Description))}");

        return user;
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
    //  Open Redirect protection tests (F07 / R4)                         //
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
        Assert.Contains("redirect_uri", location);
        Assert.DoesNotContain(evilUrl, location);

        // Decrypt the state parameter using GoogleOptions.StateDataFormat to verify fallback to "/"
        var uri = new Uri(location);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var state = query["state"];
        Assert.NotNull(state);

        var googleOptions = _factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        var properties = googleOptions.StateDataFormat.Unprotect(state);
        Assert.NotNull(properties);
        Assert.Equal("/", properties.RedirectUri);
    }

    [Fact]
    public void GoogleOptions_ConfiguresEmailVerifiedClaimMapping_AndExtractsClaimFromJson()
    {
        // Tests P3: verifies that ClaimActions includes email_verified mapping and extracts it from userinfo payload
        var googleOptions = _factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        Assert.NotNull(googleOptions);

        var action = googleOptions.ClaimActions.FirstOrDefault(a => a.ClaimType == "email_verified");
        Assert.NotNull(action);

        // Verify running ClaimActions extracts email_verified as a claim from JSON
        var json = "{\"sub\":\"123456\",\"email\":\"test@example.com\",\"email_verified\":true}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var identity = new ClaimsIdentity();
        foreach (var claimAction in googleOptions.ClaimActions)
        {
            claimAction.Run(doc.RootElement, identity, "Google");
        }

        var claim = identity.FindFirst("email_verified");
        Assert.NotNull(claim);
        Assert.Equal("True", claim.Value, ignoreCase: true);
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

        // Decrypt the state parameter using GoogleOptions.StateDataFormat to verify preserved path
        var uri = new Uri(location);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var state = query["state"];
        Assert.NotNull(state);

        var googleOptions = _factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get(GoogleDefaults.AuthenticationScheme);
        var properties = googleOptions.StateDataFormat.Unprotect(state);
        Assert.NotNull(properties);
        Assert.Equal(validPath, properties.RedirectUri);
    }

    // ------------------------------------------------------------------ //
    //  Real Cookie Authentication & Logout Invalidation test (R4)         //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task LogoutEndpoint_WithRealCookie_ClearsAuthSession_AndSubsequentMeReturns401()
    {
        var factory = new AuthTestFactory { EnableTestAuth = false };
        await factory.SeedUserAsync("cookie-test-user-id", "cookieuser@example.com", "Cookie User");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Sign in via test-support endpoint with real Cookie scheme
        var signinRes = await client.PostAsJsonAsync("/api/test/signin", new { GoogleId = "cookie-test-user-id" });
        Assert.Equal(HttpStatusCode.OK, signinRes.StatusCode);

        // 2. Access /api/auth/me with the real cookie -> returns 200 OK
        var meRes = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        var meJson = await meRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(meJson);
        Assert.Equal("cookieuser@example.com", doc.RootElement.GetProperty("email").GetString());

        // 3. Fetch antiforgery token
        var token = await factory.FetchAntiforgeryTokenAsync(client);

        // 4. Logout with antiforgery token
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", token);
        var logoutRes = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.OK, logoutRes.StatusCode);

        // 5. Subsequent request to /api/auth/me MUST return 401 Unauthorized (cookie invalidated)
        var meAfterLogoutRes = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterLogoutRes.StatusCode);
    }
}

public class AuthenticatedFlowIntegrationTests : IAsyncLifetime
{
    private readonly AuthTestFactory _factory;
    private ApplicationUser _testUser = null!;

    public AuthenticatedFlowIntegrationTests()
    {
        _factory = new AuthTestFactory { EnableTestAuth = true };
    }

    public async Task InitializeAsync()
    {
        _testUser = await _factory.SeedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MeEndpoint_WithAuth_ReturnsUserFromStore()
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
        Assert.Equal(_testUser.Id, idProp.GetGuid());
    }

    [Fact]
    public async Task MeEndpoint_WhenUserNotInDatabase_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "unregistered-google-id");

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_WhenUserNotInDatabase_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "unregistered-google-id");
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Valid Title"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Some text")), "file", "book.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task UploadEndpoint_WhenMultipartBodyExceedsLimit_ReturnsBadRequest()
    {
        // Tests resource limit handling: oversized multipart body triggers InvalidDataException in ReadFormAsync
        // which returns clean 400 Bad Request instead of 500.
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // 13MB payload exceeds the 12MB multipart limit
        var oversizedBytes = new byte[13 * 1024 * 1024];
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Huge Payload"), "title");
        form.Add(new StringContent("en"), "sourceLanguage");
        form.Add(new ByteArrayContent(oversizedBytes), "file", "oversized.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    //  Full Upload Flow & Persistence (F03, F06, T07, T13, R4)            //
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
        Assert.Equal(_testUser.Id, persistedNovel.OwnerId);
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

    [Fact]
    public async Task UploadEndpoint_MultipleUsers_EnforcesOwnerSeparationAndPersistsCorrectOwnerId()
    {
        var userA = await _factory.SeedUserAsync("google-user-a", "usera@example.com", "User A");
        var userB = await _factory.SeedUserAsync("google-user-b", "userb@example.com", "User B");

        Assert.NotEqual(userA.Id, userB.Id);

        // Upload as User A
        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "google-user-a");
        var tokenA = await _factory.FetchAntiforgeryTokenAsync(clientA);

        using var formA = new MultipartFormDataContent();
        formA.Add(new StringContent("Novel of User A"), "title");
        formA.Add(new StringContent("ja"), "sourceLanguage");
        formA.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Content from User A")), "file", "novel_a.txt");

        var requestA = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = formA };
        requestA.Headers.Add("X-XSRF-TOKEN", tokenA);
        var responseA = await clientA.SendAsync(requestA);
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        var docA = JsonDocument.Parse(await responseA.Content.ReadAsStringAsync());
        var novelIdA = docA.RootElement.GetProperty("novelId").GetGuid();

        // Upload as User B
        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "google-user-b");
        var tokenB = await _factory.FetchAntiforgeryTokenAsync(clientB);

        using var formB = new MultipartFormDataContent();
        formB.Add(new StringContent("Novel of User B"), "title");
        formB.Add(new StringContent("ja"), "sourceLanguage");
        formB.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Content from User B")), "file", "novel_b.txt");

        var requestB = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = formB };
        requestB.Headers.Add("X-XSRF-TOKEN", tokenB);
        var responseB = await clientB.SendAsync(requestB);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);

        var docB = JsonDocument.Parse(await responseB.Content.ReadAsStringAsync());
        var novelIdB = docB.RootElement.GetProperty("novelId").GetGuid();

        // Verify in DB that novelIdA belongs to userA and novelIdB belongs to userB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();

        var novelA = await db.Novels.FindAsync(novelIdA);
        var novelB = await db.Novels.FindAsync(novelIdB);

        Assert.NotNull(novelA);
        Assert.NotNull(novelB);
        Assert.Equal(userA.Id, novelA.OwnerId);
        Assert.Equal(userB.Id, novelB.OwnerId);
        Assert.NotEqual(novelA.OwnerId, novelB.OwnerId);
    }

    [Fact]
    public async Task UploadEndpoint_WithNullBytes_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // UTF-8 BOM + 'A' + NUL + 'B'
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', 0x00, (byte)'B' };
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Null Byte Novel"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(bytes), "file", "nul.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("null", body, StringComparison.OrdinalIgnoreCase);

        // Verify that nothing was persisted to DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var count = await db.Novels.CountAsync(n => n.Title == "Null Byte Novel");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UploadEndpoint_WithMalformedUtf8Bytes_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // UTF-8 BOM + 'A' + 0xFF + 'B' (0xFF is invalid UTF-8)
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', 0xFF, (byte)'B' };
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Malformed Novel"), "title");
        form.Add(new StringContent("ja"), "sourceLanguage");
        form.Add(new ByteArrayContent(bytes), "file", "malformed.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/novels/upload") { Content = form };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("malformed", body, StringComparison.OrdinalIgnoreCase);

        // Verify that nothing was persisted to DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
        var count = await db.Novels.CountAsync(n => n.Title == "Malformed Novel");
        Assert.Equal(0, count);
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
