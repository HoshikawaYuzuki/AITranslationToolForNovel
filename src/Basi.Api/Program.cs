using Basi.Api.Data;
using Basi.Core.Models;
using Basi.Module.Identity;
using Basi.Module.Identity.Models;
using Basi.Module.Translation;
using Basi.Module.Translation.Services;
using Basi.Module.StoryMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Register appsettings.Local.json for optional local developer overrides (git-ignored)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------- Database ----------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<BasiDbContext>(options =>
    options.UseNpgsql(connectionString));

// ---------- Health Checks ----------
builder.Services.AddHealthChecks();

// ---------- CORS ----------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ---------- Authentication: Cookie + Google OAuth ----------
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "Basi.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // Return 401 JSON instead of redirect for API calls
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Google:ClientId"] ?? string.Empty;
    options.ClientSecret = builder.Configuration["Google:ClientSecret"] ?? string.Empty;
    options.CallbackPath = "/api/auth/callback";
    options.SaveTokens = false; // tokens stay in RAM only, never persisted
});

builder.Services.AddAuthorization();

// ---------- Antiforgery ----------
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "Basi.Antiforgery";
});

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Basi API", Version = "v0.2.0" });
});

// ---------- Modules ----------
builder.Services.AddIdentityModule()
    .AddEntityFrameworkStores<BasiDbContext>();
builder.Services.AddTranslationModule(useMockProvider: builder.Environment.IsDevelopment());
builder.Services.AddStoryMemoryModule();

// Register TxtParser
builder.Services.AddSingleton<TxtParser>();

var app = builder.Build();

// ---------- Middleware ----------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ======================================================================
// Auth Endpoints
// ======================================================================

// GET /api/auth/login — redirect to Google
app.MapGet("/api/auth/login", (HttpContext ctx, string? returnUrl) =>
{
    var redirectUri = returnUrl ?? "/";
    return Results.Challenge(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            RedirectUri = redirectUri
        },
        [GoogleDefaults.AuthenticationScheme]);
});

// GET /api/auth/callback — handled automatically by the Google middleware.
// After a successful OAuth exchange the middleware calls OnTicketReceived.
// We hook post-login user upsert here via a minimal endpoint that the
// middleware redirects to after the cookie is issued.
app.MapGet("/api/auth/me", async (HttpContext ctx, BasiDbContext db, UserManager<ApplicationUser> userManager) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
    var name = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;

    if (googleId is null || email is null)
        return Results.Unauthorized();

    // Upsert user in DB
    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null)
    {
        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = name ?? email,
            EmailConfirmed = true,
        };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            return Results.Problem("Failed to create user account.");

        await userManager.AddLoginAsync(user, new UserLoginInfo("Google", googleId, "Google"));
    }

    return Results.Ok(new
    {
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName,
        createdAt = user.CreatedAt,
    });
}).RequireAuthorization();

// POST /api/auth/logout
app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

// ======================================================================
// Novel Upload Endpoint
// ======================================================================

// POST /api/novels/upload — multipart/form-data: file (TXT), title, sourceLanguage
app.MapPost("/api/novels/upload", async (
    HttpContext ctx,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    TxtParser parser,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    // Resolve current user
    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var appUser = await userManager.FindByLoginAsync("Google", googleId);
    if (appUser is null) return Results.Unauthorized();

    // Parse multipart form
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest("Request must be multipart/form-data.");

    var form = await ctx.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null)
        return Results.BadRequest("Missing required form field: file.");

    var title = form["title"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest("Missing required form field: title.");

    var sourceLanguage = form["sourceLanguage"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(sourceLanguage))
        return Results.BadRequest("Missing required form field: sourceLanguage.");

    // Read bytes
    byte[] bytes;
    using (var ms = new MemoryStream())
    {
        await file.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }

    // Parse TXT
    IReadOnlyList<Segment> segments;
    try
    {
        var novelId = Guid.NewGuid();
        segments = parser.Parse(bytes, novelId);

        var novel = new Novel
        {
            Id = novelId,
            OwnerId = appUser.Id,
            Title = title,
            SourceLanguage = sourceLanguage,
            UploadedAt = DateTime.UtcNow,
        };

        db.Novels.Add(novel);
        db.Segments.AddRange(segments);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            novelId = novel.Id,
            title = novel.Title,
            sourceLanguage = novel.SourceLanguage,
            segmentCount = segments.Count,
            uploadedAt = novel.UploadedAt,
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

// ======================================================================
// Health Endpoint
// ======================================================================

app.MapGet("/api/health", async (BasiDbContext db, CancellationToken ct) =>
{
    var dbStatus = "disconnected";
    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        if (await db.Database.CanConnectAsync(timeoutCts.Token))
            dbStatus = "connected";
    }
    catch
    {
        // DB unreachable or timed out — report disconnected safely without hanging
    }

    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow.ToString("o"),
        version = "0.2.0",
        database = dbStatus
    });
});

app.Run();

// Make the implicit Program class accessible for integration tests
public partial class Program { }
