using System.Security.Claims;
using Basi.Api.Data;
using Basi.Module.Identity.Models;
using Basi.Module.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Basi.Tests.Integration;

public class OAuthUserProvisionerTests
{
    private static IServiceProvider CreateServiceProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>();
        services.AddLogging();

        return services.BuildServiceProvider();
    }

    private static (ServiceProvider Provider, UserManager<ApplicationUser> UserManager) CreateTestScope()
    {
        var dbName = $"user_prov_test_{Guid.NewGuid():N}";
        var provider = (ServiceProvider)CreateServiceProvider(dbName);
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
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim("email_verified", "true")
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
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim("email_verified", "true")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("missing required claims", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_MissingEmailVerifiedClaim_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-unverified"),
                new Claim(ClaimTypes.Email, "missing_verified@example.com"),
                new Claim(ClaimTypes.Name, "Test User")
                // email_verified claim is completely missing
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("must be verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var fetched = await userManager.FindByEmailAsync("missing_verified@example.com");
            Assert.Null(fetched);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_FalseEmailVerifiedClaim_ReturnsFailure()
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
            Assert.Contains("must be verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var fetched = await userManager.FindByEmailAsync("unverified@example.com");
            Assert.Null(fetched);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_InvalidEmailVerifiedClaim_ReturnsFailure()
    {
        var (provider, userManager) = CreateTestScope();
        using (provider)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-invalid-claim"),
                new Claim(ClaimTypes.Email, "invalid_claim@example.com"),
                new Claim(ClaimTypes.Name, "Invalid Claim User"),
                new Claim("email_verified", "not-a-boolean")
            });
            var principal = new ClaimsPrincipal(identity);

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("must be verified", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
                new Claim(ClaimTypes.Name, "New User"),
                new Claim("email_verified", "true")
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
    public async Task ProvisionGoogleUserAsync_ExistingGoogleLogin_ReturnsExistingUser_WithoutRequiringEmailVerifiedClaim()
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

            // Returning user whose token doesn't include email_verified claim is accepted because login is already linked
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
            // Seed initial user without external login
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
                new Claim(ClaimTypes.Name, "Local User"),
                new Claim("email_verified", "true")
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
                new Claim(ClaimTypes.Name, "Attacker User"),
                new Claim("email_verified", "true")
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
    public async Task ProvisionGoogleUserAsync_RecoveryPath_WhenConcurrentAccountHasDifferentGoogleId_RejectsConflict()
    {
        // Tests P1 fix: in recovery path after CreateAsync failure, if the recovered account
        // has a different Google ID, it must be rejected rather than bound to the incoming subject.
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<ConcurrentConflictingUserProvisioningManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            // Request A tries to provision for race_conflict@example.com with subject-A.
            // ConcurrentConflictingUserProvisioningManager simulates that:
            // 1. Initial FindByEmailAsync returns null
            // 2. CreateAsync fails because another concurrent request created the user with subject-B
            // 3. Concurrency recovery finds the concurrent user with subject-B
            var principalA = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-subject-A"),
                new Claim(ClaimTypes.Email, "race_conflict@example.com"),
                new Claim(ClaimTypes.Name, "Subject A"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principalA, userManager);

            // Must reject the conflict!
            Assert.False(result.Succeeded);
            Assert.Contains("already linked to a different Google account", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // Verify subject-A was NOT linked to the user
            var user = await userManager.FindByEmailAsync("race_conflict@example.com");
            Assert.NotNull(user);
            var logins = await userManager.GetLoginsAsync(user);
            Assert.Single(logins);
            Assert.Equal("google-subject-B", logins[0].ProviderKey);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_RecoveryPath_WhenConcurrentAccountHasSameGoogleId_ReturnsSuccess()
    {
        // Tests P1 recovery success: if CreateAsync fails because another concurrent request created the
        // user and linked the same Google ID, recovery returns Success(user).
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<ConcurrentSameSubjectUserProvisioningManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-subject-A"),
                new Claim(ClaimTypes.Email, "race_same@example.com"),
                new Claim(ClaimTypes.Name, "Subject A"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.NotNull(result.User);
            Assert.Equal("race_same@example.com", result.User.Email);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_RecoveryPath_WhenUserNotFoundAfterCreateAsyncFailure_ReturnsFailure()
    {
        // Tests recovery failure when user cannot be found after CreateAsync failure
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<FailingCreateAndRecoveryUserManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-subject-fail"),
                new Claim(ClaimTypes.Email, "fail_recover@example.com"),
                new Claim(ClaimTypes.Name, "Fail User"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("Failed to create local user account", result.ErrorMessage);
            Assert.Contains("Database connection timed out", result.ErrorMessage);
        }
    }

    private class FaultyAddLoginUserManager : UserManager<ApplicationUser>
    {
        public FaultyAddLoginUserManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override Task<IdentityResult> AddLoginAsync(ApplicationUser user, UserLoginInfo login)
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Simulated AddLogin failure." }));
        }
    }

    private class FaultyDeleteUserManager : UserManager<ApplicationUser>
    {
        public FaultyDeleteUserManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override Task<IdentityResult> AddLoginAsync(ApplicationUser user, UserLoginInfo login)
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Simulated AddLogin failure." }));
        }

        public override Task<IdentityResult> DeleteAsync(ApplicationUser user)
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "Simulated Delete failure." }));
        }
    }

    private class ConcurrentLoginDuringAddLoginFailureUserManager : UserManager<ApplicationUser>
    {
        public ConcurrentLoginDuringAddLoginFailureUserManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override async Task<IdentityResult> AddLoginAsync(ApplicationUser user, UserLoginInfo login)
        {
            // Simulate: another process attached a login to user right as our AddLogin failed
            await base.AddLoginAsync(user, new UserLoginInfo("Google", "concurrent-linked-subject", "Google"));
            return IdentityResult.Failed(new IdentityError { Description = "Simulated primary AddLogin failure." });
        }
    }

    private class ConcurrentConflictingUserProvisioningManager : UserManager<ApplicationUser>
    {
        private bool _firstLookup = true;

        public ConcurrentConflictingUserProvisioningManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override async Task<ApplicationUser?> FindByEmailAsync(string email)
        {
            if (_firstLookup)
            {
                _firstLookup = false;
                return null; // First check returns null, triggering CreateAsync
            }
            return await base.FindByEmailAsync(email);
        }

        public override async Task<IdentityResult> CreateAsync(ApplicationUser user)
        {
            // Simulate: during CreateAsync, another request creates the user and binds Google subject-B
            var conflictingUser = new ApplicationUser
            {
                UserName = user.Email,
                Email = user.Email,
                DisplayName = "Conflicting Subject B",
                EmailConfirmed = true
            };
            await base.CreateAsync(conflictingUser);
            await base.AddLoginAsync(conflictingUser, new UserLoginInfo("Google", "google-subject-B", "Google"));

            return IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = "A user with this email already exists." });
        }
    }

    private class ConcurrentSameSubjectUserProvisioningManager : UserManager<ApplicationUser>
    {
        private bool _firstLookup = true;

        public ConcurrentSameSubjectUserProvisioningManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override async Task<ApplicationUser?> FindByEmailAsync(string email)
        {
            if (_firstLookup)
            {
                _firstLookup = false;
                return null; // First check returns null, triggering CreateAsync
            }
            return await base.FindByEmailAsync(email);
        }

        public override async Task<IdentityResult> CreateAsync(ApplicationUser user)
        {
            // Simulate: during CreateAsync, another request creates the user and binds the same Google subject-A
            var raceUser = new ApplicationUser
            {
                UserName = user.Email,
                Email = user.Email,
                DisplayName = "Race User A",
                EmailConfirmed = true
            };
            await base.CreateAsync(raceUser);
            await base.AddLoginAsync(raceUser, new UserLoginInfo("Google", "google-subject-A", "Google"));

            return IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = "A user with this email already exists." });
        }
    }

    private class FailingCreateAndRecoveryUserManager : UserManager<ApplicationUser>
    {
        public FailingCreateAndRecoveryUserManager(
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override Task<ApplicationUser?> FindByEmailAsync(string email) => Task.FromResult<ApplicationUser?>(null);
        public override Task<ApplicationUser?> FindByLoginAsync(string loginProvider, string providerKey) => Task.FromResult<ApplicationUser?>(null);
        public override Task<IdentityResult> CreateAsync(ApplicationUser user) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "DbError", Description = "Database connection timed out." }));
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_WhenAddLoginFailsForNewUser_CleansUpOrphanAccount()
    {
        // Tests P2 fix: if AddLoginAsync fails for a newly created user, orphan cleanup deletes the user.
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<FaultyAddLoginUserManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-orphan-test"),
                new Claim(ClaimTypes.Email, "orphan_candidate@example.com"),
                new Claim(ClaimTypes.Name, "Orphan Candidate"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            // Must fail
            Assert.False(result.Succeeded);
            Assert.Contains("Simulated AddLogin failure", result.ErrorMessage);

            // Verify orphan cleanup: orphan_candidate@example.com MUST NOT exist in the database!
            var orphanUser = await userManager.FindByEmailAsync("orphan_candidate@example.com");
            Assert.Null(orphanUser);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_WhenAddLoginFailsAndCleanupFails_ReturnsDetailedFailureMessage()
    {
        // Tests P2: if AddLoginAsync fails AND DeleteAsync fails, error message clearly states cleanup failed
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<FaultyDeleteUserManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-orphan-fail"),
                new Claim(ClaimTypes.Email, "orphan_fail@example.com"),
                new Claim(ClaimTypes.Name, "Orphan Fail Candidate"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("orphan cleanup failed", result.ErrorMessage);
            Assert.Contains("Simulated Delete failure", result.ErrorMessage);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_WhenAddLoginFailsForNewUser_ButConcurrentLoginAttached_SkipsOrphanCleanup()
    {
        // Tests P2 safety: if another process linked a login concurrently, cleanup is safely skipped
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<ConcurrentLoginDuringAddLoginFailureUserManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-prevent-orphan"),
                new Claim(ClaimTypes.Email, "orphan_prevented@example.com"),
                new Claim(ClaimTypes.Name, "Orphan Prevented Candidate"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("Simulated primary AddLogin failure", result.ErrorMessage);

            // User must NOT be deleted because another login was attached!
            var userInDb = await userManager.FindByEmailAsync("orphan_prevented@example.com");
            Assert.NotNull(userInDb);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_WhenAddLoginFailsForExistingUser_DoesNotDeleteExistingUser()
    {
        // Tests P2 safety: if linking fails for an EXISTING user (isNewUser == false), existing user is NOT deleted
        var services = new ServiceCollection();
        services.AddDbContext<BasiDbContext>(options =>
            options.UseInMemoryDatabase($"user_prov_test_{Guid.NewGuid():N}"));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<BasiDbContext>()
            .AddUserManager<FaultyAddLoginUserManager>();
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        using (provider)
        {
            var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

            // Create existing user beforehand
            var existingUser = new ApplicationUser
            {
                UserName = "existing_user@example.com",
                Email = "existing_user@example.com",
                DisplayName = "Existing User",
                EmailConfirmed = true
            };
            await userManager.CreateAsync(existingUser);

            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-link-existing"),
                new Claim(ClaimTypes.Email, "existing_user@example.com"),
                new Claim(ClaimTypes.Name, "Existing User"),
                new Claim("email_verified", "true")
            }));

            var result = await OAuthUserProvisioner.ProvisionGoogleUserAsync(principal, userManager);

            Assert.False(result.Succeeded);
            Assert.Contains("Simulated AddLogin failure", result.ErrorMessage);

            // Existing user must STILL exist!
            var userInDb = await userManager.FindByEmailAsync("existing_user@example.com");
            Assert.NotNull(userInDb);
        }
    }

    [Fact]
    public async Task ProvisionGoogleUserAsync_ConcurrentFirstLogin_HandlesRaceCondition_AcrossSeparateScopes()
    {
        // Tests P4: Two independent scopes / DbContexts accessing the same underlying store simultaneously
        var dbName = $"user_prov_test_{Guid.NewGuid():N}";
        var provider1 = (ServiceProvider)CreateServiceProvider(dbName);
        var provider2 = (ServiceProvider)CreateServiceProvider(dbName);

        using (provider1)
        using (provider2)
        {
            var userManager1 = provider1.GetRequiredService<UserManager<ApplicationUser>>();
            var userManager2 = provider2.GetRequiredService<UserManager<ApplicationUser>>();

            var principal1 = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-race"),
                new Claim(ClaimTypes.Email, "race@example.com"),
                new Claim(ClaimTypes.Name, "Race User"),
                new Claim("email_verified", "true")
            }));

            var principal2 = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "google-id-race"),
                new Claim(ClaimTypes.Email, "race@example.com"),
                new Claim(ClaimTypes.Name, "Race User"),
                new Claim("email_verified", "true")
            }));

            var task1 = OAuthUserProvisioner.ProvisionGoogleUserAsync(principal1, userManager1);
            var task2 = OAuthUserProvisioner.ProvisionGoogleUserAsync(principal2, userManager2);

            var results = await Task.WhenAll(task1, task2);

            Assert.True(results[0].Succeeded, results[0].ErrorMessage);
            Assert.True(results[1].Succeeded, results[1].ErrorMessage);
            Assert.Equal(results[0].User!.Id, results[1].User!.Id);

            // Verify in DB that exactly 1 user and 1 login record exist
            var db = provider1.GetRequiredService<BasiDbContext>();
            var totalUsers = await db.Users.CountAsync(u => u.Email == "race@example.com");
            Assert.Equal(1, totalUsers);

            var totalLogins = await db.UserLogins.CountAsync(l => l.ProviderKey == "google-id-race");
            Assert.Equal(1, totalLogins);
        }
    }
}
