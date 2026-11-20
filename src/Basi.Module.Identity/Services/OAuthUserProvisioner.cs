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

        // 1. Check if user already exists with this Google login
        var user = await userManager.FindByLoginAsync("Google", googleId);
        if (user != null)
        {
            return ProvisioningResult.Success(user);
        }

        // 2. Check if a user already exists with the same email
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
                return ProvisioningResult.Failed("Failed to create local user account: " + string.Join("; ", createResult.Errors.Select(e => e.Description)));
            }
        }

        // 3. Link Google external login to the user
        var addLoginResult = await userManager.AddLoginAsync(user, new UserLoginInfo("Google", googleId, "Google"));
        if (!addLoginResult.Succeeded)
        {
            return ProvisioningResult.Failed("Failed to associate Google login with user: " + string.Join("; ", addLoginResult.Errors.Select(e => e.Description)));
        }

        return ProvisioningResult.Success(user);
    }
}
