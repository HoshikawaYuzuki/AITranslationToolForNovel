using Basi.Api.Data;
using Basi.Api.Endpoints;
using Basi.Core.Models;
using Basi.Module.Identity;
using Basi.Module.Identity.Models;
using Basi.Module.Identity.Services;
using Basi.Module.Translation;
using Basi.Module.Translation.Services;
using Basi.Module.StoryMemory;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
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

// ---------- Upload / Form Size Limits ----------
builder.WebHost.ConfigureKestrel(options =>
{
    // Max 12MB total request payload at the server transport layer
    options.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
});

builder.Services.Configure<FormOptions>(options =>
{
    // Max 10MB file payload + 2MB allowance for multipart metadata and form fields
    options.MultipartBodyLengthLimit = 12 * 1024 * 1024;
});

// ---------- CORS & Frontend URL ----------
var frontendUrl = (builder.Configuration["FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(frontendUrl)
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
    options.ClaimActions.MapJsonKey("email_verified", "email_verified");

    // Strictly provision user upon receiving OAuth ticket.
    // If user creation or login association fails, the ticket is failed.
    options.Events.OnTicketReceived = async ticketCtx =>
    {
        var userManager = ticketCtx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(ticketCtx.Principal, userManager);
        if (!result.Succeeded)
        {
            ticketCtx.Fail(result.ErrorMessage!);
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

// Register Job Cancellation Registry (F08: in-flight cancellation)
builder.Services.AddSingleton<Basi.Core.Interfaces.IJobCancellationRegistry, Basi.Api.Services.JobCancellationRegistry>();

// Background Queue Worker (ADR-005)
builder.Services.AddHostedService<Basi.Api.Workers.TranslationQueueWorker>();

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

// Root endpoint — redirect to frontend UI
app.MapGet("/", () => Results.Redirect($"{frontendUrl}/"));

// ======================================================================
// Auth Endpoints
// ======================================================================

// GET /api/auth/login — redirect to Google OAuth with open redirect protection
app.MapGet("/api/auth/login", (HttpContext ctx, IConfiguration config, string? returnUrl) =>
{
    var frontendBase = (config["FrontendUrl"] ?? "http://localhost:5173").TrimEnd('/');
    var fallbackUrl = $"{frontendBase}/";

    string redirectUri = fallbackUrl;

    if (!string.IsNullOrWhiteSpace(returnUrl))
    {
        // Case 1: Local relative path (e.g. "/novels/my-list", "/")
        if (returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\"))
        {
            redirectUri = $"{frontendBase}{returnUrl}";
        }
        // Case 2: Absolute URL matching the configured frontend origin
        else if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var targetUri)
                 && Uri.TryCreate(frontendBase, UriKind.Absolute, out var baseUri)
                 && string.Equals(targetUri.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            redirectUri = targetUri.ToString();
        }
        // Otherwise: open redirect attempt (e.g. https://evil.com, //evil.com, /\\evil.com) -> fallbackUrl
    }

    return Results.Challenge(
        new AuthenticationProperties
        {
            RedirectUri = redirectUri
        },
        [GoogleDefaults.AuthenticationScheme]);
});

// GET /api/auth/me — purely reads current authenticated user information from store
app.MapGet("/api/auth/me", async (HttpContext ctx, UserManager<ApplicationUser> userManager) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null)
        return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName,
        createdAt = user.CreatedAt,
    });
}).RequireAuthorization();

// POST /api/auth/logout — signs out and clears session cookie (requires antiforgery validation)
app.MapPost("/api/auth/logout", async (
    HttpContext ctx,
    IAntiforgery antiforgery,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault,
    BasiDbContext db,
    Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId != null)
    {
        var user = await userManager.FindByLoginAsync("Google", googleId);
        if (user != null)
        {
            keyVault.ClearAll(user.Id);

            // In-flight abort: cancel any active jobs for this user upon logout
            var activeJobIds = await db.TranslationJobs
                .Where(j => j.OwnerId == user.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Processing))
                .Select(j => j.Id)
                .ToListAsync();
            foreach (var jId in activeJobIds)
            {
                cancellationRegistry.TryCancelJob(jId);
                keyVault.RemoveJobKey(jId);
            }
        }
    }

    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

// ======================================================================
// BYOK API Key Endpoints (RAM-Only per ADR-004)
// ======================================================================

// POST /api/auth/api-keys — sets user's AI provider key in RAM only
app.MapPost("/api/auth/api-keys", async (
    HttpContext ctx,
    SetApiKeyRequest req,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault,
    BasiDbContext db,
    IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Provider) || string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "Provider and ApiKey are required." });

    var normProvider = req.Provider.Trim().ToLowerInvariant();
    if (normProvider is not ("gemini" or "openai"))
        return Results.BadRequest(new { error = "Supported providers are 'gemini' and 'openai'." });

    keyVault.SetKey(user.Id, normProvider, req.ApiKey.Trim());

    // State machine: WaitingForApiKey -> Queued when key is provided (CONTRACTS.md)
    var waitingJobs = await db.TranslationJobs
        .Where(j => j.OwnerId == user.Id && j.Status == JobStatus.WaitingForApiKey && j.Provider.ToLower() == normProvider)
        .ToListAsync();
    foreach (var wj in waitingJobs)
    {
        wj.Status = JobStatus.Queued;
        wj.ErrorMessage = null;
        wj.UpdatedAt = DateTime.UtcNow;
    }
    if (waitingJobs.Count > 0)
    {
        await db.SaveChangesAsync();
    }

    return Results.Ok(new { success = true, provider = normProvider });
}).RequireAuthorization();

// GET /api/auth/api-keys — returns active status of keys in RAM (never returns raw keys)
app.MapGet("/api/auth/api-keys", async (
    HttpContext ctx,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var status = keyVault.GetKeyStatus(user.Id);
    return Results.Ok(status);
}).RequireAuthorization();

// DELETE /api/auth/api-keys/{provider} — clears provider key from RAM
app.MapDelete("/api/auth/api-keys/{provider}", async (
    HttpContext ctx,
    string provider,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault,
    BasiDbContext db,
    Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry,
    IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var removed = keyVault.RemoveKey(user.Id, provider);

    // In-flight abort: cancel any active jobs using this provider upon key deletion
    var activeJobIds = await db.TranslationJobs
        .Where(j => j.OwnerId == user.Id && j.Provider.ToLower() == provider.ToLower() && (j.Status == JobStatus.Queued || j.Status == JobStatus.Processing))
        .Select(j => j.Id)
        .ToListAsync();
    foreach (var jId in activeJobIds)
    {
        cancellationRegistry.TryCancelJob(jId);
        keyVault.RemoveJobKey(jId);
    }

    return Results.Ok(new { success = removed, provider = provider.ToLowerInvariant() });
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
    if (appUser is null) return Results.Unauthorized();

    // 3. Form content-type check
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest("Request must be multipart/form-data.");

    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync(ct);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest("Uploaded multipart form or file exceeds the maximum allowed size limit.");
    }
    catch (BadHttpRequestException ex)
    {
        return Results.StatusCode(ex.StatusCode);
    }

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

    // 9. Parse and persist (with strict validation: no NUL, no unprintable controls, no malformed bytes)
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

// GET /api/novels — lists uploaded novels for current user
app.MapGet("/api/novels", async (
    HttpContext ctx,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var novels = await db.Novels
        .Where(n => n.OwnerId == user.Id)
        .OrderByDescending(n => n.UploadedAt)
        .Select(n => new
        {
            id = n.Id,
            title = n.Title,
            sourceLanguage = n.SourceLanguage,
            segmentCount = n.Segments.Count,
            uploadedAt = n.UploadedAt
        })
        .ToListAsync(ct);

    return Results.Ok(novels);
}).RequireAuthorization();

// GET /api/novels/{id:guid} — gets details of a single novel with segments
app.MapGet("/api/novels/{id:guid}", async (
    HttpContext ctx,
    Guid id,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var novel = await db.Novels
        .Include(n => n.Segments)
        .FirstOrDefaultAsync(n => n.Id == id && n.OwnerId == user.Id, ct);

    if (novel == null)
        return Results.NotFound(new { error = "Novel not found." });

    return Results.Ok(new
    {
        id = novel.Id,
        title = novel.Title,
        sourceLanguage = novel.SourceLanguage,
        uploadedAt = novel.UploadedAt,
        segments = novel.Segments.OrderBy(s => s.Order).Select(s => new
        {
            id = s.Id,
            order = s.Order,
            content = s.Content
        }).ToList()
    });
}).RequireAuthorization();

// GET /api/novels/{id:guid}/translations — gets translation jobs for a novel
app.MapGet("/api/novels/{id:guid}/translations", async (
    HttpContext ctx,
    Guid id,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var novelExists = await db.Novels.AnyAsync(n => n.Id == id && n.OwnerId == user.Id, ct);
    if (!novelExists)
        return Results.NotFound(new { error = "Novel not found." });

    var jobs = await db.TranslationJobs
        .Where(j => j.NovelId == id && j.OwnerId == user.Id)
        .OrderByDescending(j => j.CreatedAt)
        .Select(j => new
        {
            id = j.Id,
            novelId = j.NovelId,
            targetLanguage = j.TargetLanguage,
            provider = j.Provider,
            model = j.Model,
            actualModel = j.ActualModel,
            requestCount = j.RequestCount,
            status = j.Status.ToString(),
            totalSegments = j.TotalSegments,
            completedSegments = j.CompletedSegments,
            promptTokens = j.PromptTokens,
            completionTokens = j.CompletionTokens,
            totalTokens = j.TotalTokens,
            errorMessage = j.ErrorMessage,
            createdAt = j.CreatedAt,
            updatedAt = j.UpdatedAt
        })
        .ToListAsync(ct);

    return Results.Ok(jobs);
}).RequireAuthorization();

// ======================================================================
// Translation Endpoints (BASI-003)
// ======================================================================

// POST /api/translations — submits novel for translation
app.MapPost("/api/translations", async (
    HttpContext ctx,
    CreateTranslationRequest req,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault,
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    if (req.NovelId == Guid.Empty)
        return Results.BadRequest(new { error = "Valid NovelId is required." });

    if (string.IsNullOrWhiteSpace(req.TargetLanguage))
        return Results.BadRequest(new { error = "TargetLanguage is required." });

    var targetLang = req.TargetLanguage.Trim();
    if (targetLang.Length > 32 || !Regex.IsMatch(targetLang, @"^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$"))
        return Results.BadRequest(new { error = "Invalid TargetLanguage format. Must be a valid BCP-47 tag (e.g. vi, en, ja)." });

    if (string.IsNullOrWhiteSpace(req.Provider))
        return Results.BadRequest(new { error = "Provider is required." });

    var normProvider = req.Provider.Trim().ToLowerInvariant();
    if (normProvider is not ("gemini" or "openai" or "mock"))
        return Results.BadRequest(new { error = "Provider must be 'gemini', 'openai', or 'mock'." });

    var model = string.IsNullOrWhiteSpace(req.Model)
        ? (normProvider == "gemini" ? "gemini-2.5-flash" : normProvider == "openai" ? "gpt-4o-mini" : "mock-v1")
        : req.Model.Trim();

    if (model.Length > 128 || !Regex.IsMatch(model, @"^[a-zA-Z0-9_\-\.\/:]+$"))
        return Results.BadRequest(new { error = "Model name must not exceed 128 characters and contain only alphanumeric characters and delimiters (-, _, ., /, :)." });

    var novel = await db.Novels
        .Include(n => n.Segments)
        .FirstOrDefaultAsync(n => n.Id == req.NovelId && n.OwnerId == user.Id, ct);

    if (novel == null)
        return Results.NotFound(new { error = "Novel not found or access denied." });

    if (novel.Segments.Count == 0)
        return Results.BadRequest(new { error = "Novel has no segments to translate." });

    var job = new TranslationJob
    {
        Id = Guid.NewGuid(),
        OwnerId = user.Id,
        NovelId = novel.Id,
        TargetLanguage = targetLang,
        Provider = normProvider,
        Model = model,
        Status = JobStatus.Queued,
        TotalSegments = novel.Segments.Count,
        CompletedSegments = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    var translatedSegments = novel.Segments
        .OrderBy(s => s.Order)
        .Select(s => new TranslatedSegment
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SegmentId = s.Id,
            Order = s.Order,
            SourceText = s.Content,
            Status = SegmentStatus.Pending,
            UpdatedAt = DateTime.UtcNow
        })
        .ToList();

    foreach (var ts in translatedSegments)
    {
        job.TranslatedSegments.Add(ts);
    }

    // F04-C: Store Job-bound key in RAM vault BEFORE publishing job as Queued to eliminate race condition
    var hasJobKey = !string.IsNullOrWhiteSpace(req.ApiKey);
    if (hasJobKey)
    {
        keyVault.SetJobKey(user.Id, job.Id, req.ApiKey!.Trim());
    }

    try
    {
        db.TranslationJobs.Add(job);
        db.TranslatedSegments.AddRange(translatedSegments);
        await db.SaveChangesAsync(ct);
    }
    catch
    {
        if (hasJobKey)
        {
            keyVault.RemoveJobKey(job.Id);
        }
        throw;
    }

    return Results.Created($"/api/translations/{job.Id}", new
    {
        id = job.Id,
        novelId = job.NovelId,
        targetLanguage = job.TargetLanguage,
        provider = job.Provider,
        model = job.Model,
        actualModel = job.ActualModel,
        requestCount = job.RequestCount,
        status = job.Status.ToString(),
        totalSegments = job.TotalSegments,
        completedSegments = job.CompletedSegments,
        createdAt = job.CreatedAt
    });
}).RequireAuthorization();

// GET /api/translations — lists all translation jobs for current user
app.MapGet("/api/translations", async (
    HttpContext ctx,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var jobs = await db.TranslationJobs
        .Where(j => j.OwnerId == user.Id)
        .OrderByDescending(j => j.CreatedAt)
        .Select(j => new
        {
            id = j.Id,
            novelId = j.NovelId,
            targetLanguage = j.TargetLanguage,
            provider = j.Provider,
            model = j.Model,
            actualModel = j.ActualModel,
            requestCount = j.RequestCount,
            status = j.Status.ToString(),
            totalSegments = j.TotalSegments,
            completedSegments = j.CompletedSegments,
            promptTokens = j.PromptTokens,
            completionTokens = j.CompletionTokens,
            totalTokens = j.TotalTokens,
            errorMessage = j.ErrorMessage,
            createdAt = j.CreatedAt,
            updatedAt = j.UpdatedAt
        })
        .ToListAsync(ct);

    return Results.Ok(jobs);
}).RequireAuthorization();

// GET /api/translations/{id:guid} — gets details and segments of a translation job
app.MapGet("/api/translations/{id:guid}", async (
    HttpContext ctx,
    Guid id,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var job = await db.TranslationJobs
        .Include(j => j.TranslatedSegments)
        .FirstOrDefaultAsync(j => j.Id == id && j.OwnerId == user.Id, ct);

    if (job == null)
        return Results.NotFound(new { error = "Translation job not found." });

    return Results.Ok(new
    {
        id = job.Id,
        novelId = job.NovelId,
        targetLanguage = job.TargetLanguage,
        provider = job.Provider,
        model = job.Model,
        actualModel = job.ActualModel,
        requestCount = job.RequestCount,
        status = job.Status.ToString(),
        totalSegments = job.TotalSegments,
        completedSegments = job.CompletedSegments,
        promptTokens = job.PromptTokens,
        completionTokens = job.CompletionTokens,
        totalTokens = job.TotalTokens,
        errorMessage = job.ErrorMessage,
        createdAt = job.CreatedAt,
        updatedAt = job.UpdatedAt,
        segments = job.TranslatedSegments
            .OrderBy(s => s.Order)
            .Select(s => new
            {
                id = s.Id,
                segmentId = s.SegmentId,
                order = s.Order,
                sourceText = s.SourceText,
                translatedText = s.TranslatedText,
                status = s.Status.ToString(),
                errorMessage = s.ErrorMessage
            })
            .ToList()
    });
}).RequireAuthorization();

// POST /api/translations/{id:guid}/cancel — cancels an in-progress or queued translation job
app.MapPost("/api/translations/{id:guid}/cancel", async (
    HttpContext ctx,
    Guid id,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    Basi.Core.Interfaces.IJobCancellationRegistry cancellationRegistry,
    IApiKeyVault keyVault,
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var job = await db.TranslationJobs
        .FirstOrDefaultAsync(j => j.Id == id && j.OwnerId == user.Id, ct);

    if (job == null)
        return Results.NotFound(new { error = "Translation job not found." });

    if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        return Results.BadRequest(new { error = $"Cannot cancel job in '{job.Status}' status." });

    // F08: Cancel in-flight AI provider call immediately via cancellation registry
    cancellationRegistry.TryCancelJob(id);

    // F04-A: Purge Job-bound key from RAM immediately upon cancel
    keyVault.RemoveJobKey(id);

    job.Status = JobStatus.Cancelled;
    job.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
}).RequireAuthorization();

// POST /api/translations/{id:guid}/api-key — sets/re-enters job-specific API key for a job (F04-B vertical slice)
app.MapPost("/api/translations/{id:guid}/api-key", async (
    HttpContext ctx,
    Guid id,
    SetJobApiKeyRequest req,
    BasiDbContext db,
    UserManager<ApplicationUser> userManager,
    IApiKeyVault keyVault,
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(ctx);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid antiforgery token." });
    }

    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "API key cannot be empty." });

    var googleId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (googleId is null) return Results.Unauthorized();

    var user = await userManager.FindByLoginAsync("Google", googleId);
    if (user is null) return Results.Unauthorized();

    var job = await db.TranslationJobs
        .FirstOrDefaultAsync(j => j.Id == id && j.OwnerId == user.Id, ct);

    if (job == null)
        return Results.NotFound(new { error = "Translation job not found." });

    if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        return Results.BadRequest(new { error = $"Cannot set API key for job in '{job.Status}' status." });

    // Store key in RAM bound specifically to (user.Id, job.Id)
    keyVault.SetJobKey(user.Id, job.Id, req.ApiKey.Trim());

    // If job was waiting for API key, wake it up and requeue
    if (job.Status == JobStatus.WaitingForApiKey)
    {
        job.Status = JobStatus.Queued;
        job.ErrorMessage = null;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
}).RequireAuthorization();

// ======================================================================
// Test Support Endpoints (Testing environment only)
// ======================================================================

if (app.Environment.IsEnvironment("Testing"))
{
    // Test-only endpoint to issue real authentication cookie for integration testing
    app.MapPost("/api/test/signin", async (HttpContext ctx, TestSignInRequest req, UserManager<ApplicationUser> userManager) =>
    {
        var user = await userManager.FindByLoginAsync("Google", req.GoogleId);
        if (user == null) return Results.NotFound("Test user not found");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, req.GoogleId),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
            new Claim(ClaimTypes.Name, user.DisplayName ?? "")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Ok(new { message = "Signed in" });
    });
}

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

app.MapStoryMemoryEndpoints();

app.Run();

// Make the implicit Program class accessible for integration tests
public partial class Program { }

public record TestSignInRequest(string GoogleId);
public record SetApiKeyRequest(string Provider, string ApiKey);
public record CreateTranslationRequest(Guid NovelId, string TargetLanguage, string Provider, string? Model, string? ApiKey = null);
public record SetJobApiKeyRequest(string ApiKey);
