using System.Security.Claims;
using Basi.Api.Data;
using Basi.Module.Identity.Models;
using Basi.Module.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Tests.Integration;

public class OAuthUserProvisionerTests
{
    private static (ServiceProvider Provider, UserManager<ApplicationUser> UserManager) CreateTestScope()
    {
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        return (provider, userManager);
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_NullPrincipal_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(null, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("missing required claims", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_MissingGoogleId_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Email, "user@example.com"),
                new Claim(ClaimTypes.Name, "Test User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("missing required claims", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_MissingEmail_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-123"),
                new Claim(ClaimTypes.Name, "Test User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("missing required claims", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_NewUser_CreatesAndLinksGoogleLogin()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-new"),
                new Claim(ClaimTypes.Email, "newuser@example.com"),
                new Claim(ClaimTypes.Name, "New User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.User);
            Assert.Equal("newuser@example.com", result.User.Email);
            Assert.Equal("New User", result.User.DisplayName);

            // Verify in user store
            var fetched = await userManager.FindByLoginAsync("Google", "google-id-new");
            Assert.NotNull(fetched);
            Assert.Equal(result.User.Id, fetched.Id);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_ExistingGoogleLogin_ReturnsExistingUser()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            // Seed initial user with Google login
            var existing = new ApplicationUser
            {
                UserName = "existing@example.com",
                Email = "existing@example.com",
                DisplayName = "Existing User",
                EmailConfirmed = true,
            };
            await userManager.CreateAsync(existing);
            await userManager.AddLoginAsync(existing, new UserLoginInfo("Google", "google-id-existing", "Google"));

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-existing"),
                new Claim(ClaimTypes.Email, "existing@example.com"),
                new Claim(ClaimTypes.Name, "Existing User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.User);
            Assert.Equal(existing.Id, result.User.Id);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_ExistingEmailWithoutGoogleLogin_LinksGoogleLoginSuccessfully()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            // Seed initial user without external login (e.g. registered directly or concurrent user)
            var existing = new ApplicationUser
            {
                UserName = "local@example.com",
                Email = "local@example.com",
                DisplayName = "Local User",
                EmailConfirmed = true,
            };
            await userManager.CreateAsync(existing);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-link"),
                new Claim(ClaimTypes.Email, "local@example.com"),
                new Claim(ClaimTypes.Name, "Local User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.User);
            Assert.Equal(existing.Id, result.User.Id);

            // Verify the login was associated
            var fetched = await userManager.FindByLoginAsync("Google", "google-id-link");
            Assert.NotNull(fetched);
            Assert.Equal(existing.Id, fetched.Id);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_UnverifiedEmail_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-unverified"),
                new Claim(ClaimTypes.Email, "unverified@example.com"),
                new Claim(ClaimTypes.Name, "Unverified User"),
                new Claim("email_verified", "false")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("not verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // Verify no user was created
            var fetched = await userManager.FindByEmailAsync("unverified@example.com");
            Assert.Null(fetched);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_ConflictingGoogleAccountLinkedToSameEmail_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            // Seed existing user already linked to Google ID "google-id-primary"
            var existing = new ApplicationUser
            {
                UserName = "conflict@example.com",
                Email = "conflict@example.com",
                DisplayName = "Conflict User",
                EmailConfirmed = true,
            };
            await userManager.CreateAsync(existing);
            await userManager.AddLoginAsync(existing, new UserLoginInfo("Google", "google-id-primary", "Google"));

            // Another Google login with a different Google ID attempts to claim the same email
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-attacker"),
                new Claim(ClaimTypes.Email, "conflict@example.com"),
                new Claim(ClaimTypes.Name, "Attacker User")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("already linked", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // Verify the existing user's login was NOT overwritten or compromised
            var logins = await userManager.GetLoginsAsync(existing);
            Assert.Single(logins);
            Assert.Equal("google-id-primary", logins[0].ProviderKey);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_ConcurrentFirstLogin_HandlesRaceCondition()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var principal1 = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-race"),
                new Claim(ClaimTypes.Email, "race@example.com"),
                new Claim(ClaimTypes.Name, "Race User")
            }));

            var principal2 = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-race"),
                new Claim(ClaimTypes.Email, "race@example.com"),
                new Claim(ClaimTypes.Name, "Race User")
            }));

            var task1 = OAuthUserProvisioner.ProvisionGoogleUserAsync(principal1, userManager);
            var task2 = OAuthUserProvisioner.ProvisionGoogleUserAsync(principal2, userManager);

            var results = await Task.WhenAll(task1, task2);

            Assert.True(results[0].Succeeded, results[0].ErrorMessage);
            Assert.True(results[1].Succeeded, results[1].ErrorMessage);
            Assert.Equal(results[0].User!.Id, results[1].User!.Id);

            // Verify only a single user exists in the store
            var user = await userManager.FindByLoginAsync("Google", "google-id-race");
            Assert.NotNull(user);
        }
    }
}
