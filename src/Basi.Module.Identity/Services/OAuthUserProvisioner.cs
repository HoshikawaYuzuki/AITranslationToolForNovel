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

        // Validate email_verified if present in claims
        var emailVerifiedClaim = principal?.FindFirst("email_verified")?.Value;
        if (emailVerifiedClaim != null && bool.TryParse(emailVerifiedClaim, out var isVerified) && !isVerified)
        {
            return ProvisioningResult.Failed("Google email address is not verified.");
        }

        // 1. Check if user already exists with this Google login
        var user = await userManager.FindByLoginAsync(GoogleProvider, googleId);
        if (user != null)
        {
            return ProvisioningResult.Success(user);
        }

        // 2. Check if a user already exists with the same email
        var isNewUser = false;
        user = await userManager.FindByEmailAsync(email);
        if (user != null)
        {
            // Verify that the existing account is not already bound to a conflicting Google ID
            var existingLogins = await userManager.GetLoginsAsync(user);
            if (existingLogins.Any(l => l.LoginProvider == GoogleProvider && l.ProviderKey != googleId))
            {
                return ProvisioningResult.Failed("An existing account with this email is already linked to a different Google account.");
            }
        }
        else
        {
            isNewUser = true;
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
                // Handle potential race condition on concurrent first login
                user = await userManager.FindByLoginAsync(GoogleProvider, googleId)
                    ?? await userManager.FindByEmailAsync(email);

                if (user == null)
                {
                    return ProvisioningResult.Failed("Failed to create local user account: " + string.Join("; ", createResult.Errors.Select(e => e.Description)));
                }

                // If concurrent request already linked the Google login, return success
                var concurrentLogins = await userManager.GetLoginsAsync(user);
                if (concurrentLogins.Any(l => l.LoginProvider == GoogleProvider && l.ProviderKey == googleId))
                {
                    return ProvisioningResult.Success(user);
                }

                isNewUser = false;
            }
        }

        // 3. Link Google external login to the user
        var addLoginResult = await userManager.AddLoginAsync(user, new UserLoginInfo(GoogleProvider, googleId, GoogleProvider));
        if (!addLoginResult.Succeeded)
        {
            if (isNewUser)
            {
                // Cleanup orphan user account if external login association fails
                await userManager.DeleteAsync(user);
            }
            return ProvisioningResult.Failed("Failed to associate Google login with user: " + string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));
        }

        return ProvisioningResult.Success(user);
    }
}
