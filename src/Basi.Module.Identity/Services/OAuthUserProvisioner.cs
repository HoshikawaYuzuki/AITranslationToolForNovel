using System.Security.Claims;
using Basi.Module.Identity.Models;
using Microsoft.AspNetCore.Identity;

namespace Basi.Module.Identity.Services;

public record ProvisioningResult(bool Succeeded, ApplicationUser? User, string? ErrorMessage)
{
    public static ProvisioningResult Success(ApplicationUser user) => new(true, user, null);
    public static ProvisioningResult Failed(string error) => new(false, null, error);
}

public static class OAuthUserProvisioner
{
    public const string GoogleProvider = "Google";

    public static async Task<ProvisioningResult> ProvisionGoogleUserAsync(
        ClaimsPrincipal? principal,
        UserManager<ApplicationUser> userManager)
    {
        ArgumentNullException.ThrowIfNull(userManager);

        var googleId = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal?.FindFirst(ClaimTypes.Email)?.Value;
        var name = principal?.FindFirst(ClaimTypes.Name)?.Value;

        if (string.IsNullOrEmpty(googleId) || string.IsNullOrEmpty(email))
        {
            return ProvisioningResult.Failed("Google authentication response missing required claims.");
        }

        // 1. Check if user already exists with this exact Google login
        var user = await userManager.FindByLoginAsync(GoogleProvider, googleId);
        if (user != null)
        {
            return ProvisioningResult.Success(user);
        }

        // 2. For new accounts or linking, verify that email is verified by Google
        var emailVerifiedClaim = principal?.FindFirst("email_verified")?.Value;
        if (string.IsNullOrEmpty(emailVerifiedClaim) || !bool.TryParse(emailVerifiedClaim, out var isVerified) || !isVerified)
        {
            return ProvisioningResult.Failed("Google email address must be verified to provision or link an account.");
        }

        // 3. Check if an account already exists with this email
        var isNewUser = false;
        user = await userManager.FindByEmailAsync(email);
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
            if (!createResult.Succeeded)
            {
                // Concurrency recovery: user might have been created concurrently by email or login
                user = await userManager.FindByLoginAsync(GoogleProvider, googleId)
                    ?? await userManager.FindByEmailAsync(email);

                if (user == null)
                {
                    return ProvisioningResult.Failed("Failed to create local user account: " + string.Join("; ", createResult.Errors.Select(e => e.Description)));
                }
            }
            else
            {
                isNewUser = true;
            }
        }

        // 4. Uniform identity check across ALL code paths (initial lookup & concurrency recovery)
        var logins = await userManager.GetLoginsAsync(user);

        // Already linked to this exact Google ID?
        if (logins.Any(l => l.LoginProvider == GoogleProvider && l.ProviderKey == googleId))
        {
            return ProvisioningResult.Success(user);
        }

        // Conflict: already linked to a different Google ID?
        if (logins.Any(l => l.LoginProvider == GoogleProvider && l.ProviderKey != googleId))
        {
            return ProvisioningResult.Failed("An existing account with this email is already linked to a different Google account.");
        }

        // 5. Link Google external login to user
        var addLoginResult = await userManager.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, googleId, GoogleProvider));
        if (!addLoginResult.Succeeded)
        {
            if (isNewUser)
            {
                // Only clean up if user has no other logins
                var currentLogins = await userManager.GetLoginsAsync(user);
                if (currentLogins.Count == 0)
                {
                    var deleteResult = await userManager.DeleteAsync(user);
                    if (!deleteResult.Succeeded)
                    {
                        return ProvisioningResult.Failed(
                            "Failed to associate Google login with user, and orphan cleanup failed: " +
                            string.Join("; ", deleteResult.Errors.Select(e => e.Description)));
                    }
                }
            }
            return ProvisioningResult.Failed("Failed to associate Google login with user: " + string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));
        }

        return ProvisioningResult.Success(user);
    }
}
