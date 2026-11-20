using Basi.Api.Data;
using Basi.Core.Models;
using Basi.Module.Identity;
using Basi.Module.Identity.Models;
using Basi.Module.Translation;
using Basi.Module.Translation.Services;
using Basi.Module.StoryMemory;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

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
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;

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

    // Provision user immediately upon receiving OAuth ticket
    options.Events.OnTicketReceived = async ticketCtx =>
    {
        var userManager = ticketCtx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var googleId = ticketCtx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = ticketCtx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var name = ticketCtx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;

        if (!string.IsNullOrEmpty(googleId) && !string.IsNullOrEmpty(email))
        {
            var user = await userManager.FindByLoginAsync("Google", googleId);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = name ?? email,
                    EmailConfirmed = true,
                };
                var createResult = await userManager.CreateAsync(user);
                if (createResult.Succeeded)
                {
                    await userManager.AddLoginAsync(user, new UserLoginInfo("Google", googleId, "Google"));
                }
            }
        }
    };
});

builder.Services.AddAuthorization();

// ---------- Antiforgery ----------
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "Basi.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
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
// Antiforgery Token Endpoint
// ======================================================================

// GET /api/antiforgery/token — Issues an antiforgery token for write operations
app.MapGet("/api/antiforgery/token", (IAntiforgery antiforgery, HttpContext ctx) =>
{
    var tokens = antiforgery.GetAndStoreTokens(ctx);
    return Results.Ok(new
    {
        token = tokens.RequestToken,
        headerName = "X-XSRF-TOKEN"
    });
});

// ======================================================================
// Auth Endpoints
// ======================================================================

// GET /api/auth/login — redirect to Google OAuth with open redirect protection
app.MapGet("/api/auth/login", (HttpContext ctx, string? returnUrl) =>
{
    // Strictly validate returnUrl to prevent open redirects:
    // Must start with '/' and must NOT start with '//' or '/\'
    var safeReturnUrl = "/";
    if (!string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//")
        && !returnUrl.StartsWith("/\\"))
    {
        safeReturnUrl = returnUrl;
    }

    return Results.Challenge(
        new AuthenticationProperties
        {
            RedirectUri = safeReturnUrl
        },
        [GoogleDefaults.AuthenticationScheme]);
});

// GET /api/auth/me — returns current authenticated user information
app.MapGet("/api/auth/me", async (HttpContext ctx, UserManager<ApplicationUser> userManager) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null)
        return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null)
    {
        // Safe fallback in case ticket received hook was bypassed (e.g. test schemes)
        var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var name = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        if (email is not null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = name ?? email,
                EmailConfirmed = true,
            };
            var createResult = await userManager.CreateAsync(user);
            if (createResult.Succeeded)
            {
                await userManager.AddLoginAsync(user, new UserLoginInfo("Google", googleId, "Google"));
            }
            else
            {
                return Results.Problem("Failed to create user account.");
            }
        }
        else
        {
            return Results.Unauthorized();
        }
    }

    return Results.Ok(new
    {
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName,
        createdAt = user.CreatedAt,
    });
}).RequireAuthorization();

// POST /api/auth/logout — signs out and clears session cookie (requires antiforgery validation)
app.MapPost("/api/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

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
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    // 1. Antiforgery validation
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    // 2. Auth check
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var appUser = await userManager.FindByLoginAsync("Google", googleId);
    if (appUser is null)
    {
        // Fallback provisioning if not present
        var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        var name = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        if (email is not null)
        {
            appUser = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = name ?? email,
                EmailConfirmed = true,
            };
            var createRes = await userManager.CreateAsync(appUser);
            if (createRes.Succeeded)
            {
                await userManager.AddLoginAsync(appUser, new UserLoginInfo("Google", googleId, "Google"));
            }
            else
            {
                return Results.Unauthorized();
            }
        }
        else
        {
            return Results.Unauthorized();
        }
    }

    // 3. Form content-type check
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest("Request must be multipart/form-data.");

    var form = await ctx.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest("Missing or empty file.");

    // 4. Early file size check (BEFORE reading bytes into memory)
    if (file.Length > TxtParser.MaxFileSizeBytes)
        return Results.BadRequest($"File size {file.Length} bytes exceeds the 10 MB limit.");

    // 5. File extension check
    var extension = Path.GetExtension(file.FileName);
    if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only .txt files are supported.");

    // 6. Title validation (max 512)
    var title = form["title"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest("Missing required form field: title.");
    if (title.Length > 512)
        return Results.BadRequest("Title must not exceed 512 characters.");

    // 7. Source language validation (max 32, BCP-47 tag format)
    var sourceLanguage = form["sourceLanguage"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(sourceLanguage))
        return Results.BadRequest("Missing required form field: sourceLanguage.");
    if (sourceLanguage.Length > 32 || !Regex.IsMatch(sourceLanguage, @"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$"))
        return Results.BadRequest("Invalid sourceLanguage format. Must be a valid language code (e.g. ja, zh-CN, ko, en).");

    // 8. Read bytes safely
    byte[] bytes;
    using (var ms = new MemoryStream((int)file.Length))
    {
        await file.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }

    // 9. Parse and persist
    try
    {
        var novelId = Guid.NewGuid();
        var parseResult = parser.ParseFull(bytes, novelId, sourceLanguage);

        var novel = new Novel
        {
            Id = novelId,
            OwnerId = appUser.Id,
            Title = title,
            SourceLanguage = sourceLanguage,
            RawContent = parseResult.RawContent,
            UploadedAt = DateTime.UtcNow,
        };

        db.Novels.Add(novel);
        db.Segments.AddRange(parseResult.Segments);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            novelId = novel.Id,
            title = novel.Title,
            sourceLanguage = novel.SourceLanguage,
            segmentCount = parseResult.Segments.Count,
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
