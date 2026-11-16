using Basi.Api.Data;
using Basi.Module.Identity;
using Basi.Module.Translation;
using Basi.Module.StoryMemory;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

// ---------- Auth (placeholder for cookie auth + Google OAuth in Task 2) ----------
builder.Services.AddAuthentication()
    .AddCookie(options =>
    {
        options.Cookie.Name = "Basi.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/api/auth/login";
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
    c.SwaggerDoc("v1", new() { Title = "Basi API", Version = "v0.1.0" });
});

// ---------- Modules ----------
builder.Services.AddIdentityModule();
builder.Services.AddTranslationModule(useMockProvider: builder.Environment.IsDevelopment());
builder.Services.AddStoryMemoryModule();

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

// ---------- Health endpoint ----------
app.MapGet("/api/health", async (BasiDbContext db) =>
{
    var dbStatus = "disconnected";
    try
    {
        if (await db.Database.CanConnectAsync())
            dbStatus = "connected";
    }
    catch
    {
        // DB unreachable — that's fine, report disconnected
    }

    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow.ToString("o"),
        version = "0.1.0",
        database = dbStatus
    });
});

app.Run();

// Make the implicit Program class accessible for integration tests
public partial class Program { }
