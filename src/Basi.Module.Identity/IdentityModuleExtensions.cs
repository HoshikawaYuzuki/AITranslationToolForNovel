using Basi.Module.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Module.Identity;

public static class IdentityModuleExtensions
{
    public static IdentityBuilder AddIdentityModule(this IServiceCollection services)
    {
        return services.AddIdentityCore<ApplicationUser>(options =>
        {
            // Relax password complexity for development convenience.
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 1;
        });
    }
}
