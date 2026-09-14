using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Auth;

/// <summary>Keeps cookie role/name in sync with <c>SYNC_USER</c> (e.g. after first-admin promotion).</summary>
public static class AuthCookieEvents
{
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true)
            return;

        var email = context.Principal.FindFirstValue(ClaimTypes.Email)
                    ?? context.Principal.Identity?.Name
                    ?? "";
        email = email.Trim();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            return;

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserDirectory>();
        var user = await users.FindByEmailAsync(email, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
            return;
        }

        user = await EnsureAdminIfNoneAsync(users, user, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (user is null)
            return;

        var claimRole = context.Principal.FindFirstValue(ClaimTypes.Role) ?? "";
        var claimName = context.Principal.FindFirstValue(ClaimTypes.Name) ?? "";
        var claimId = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        if (string.Equals(claimRole, user.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claimName, user.DisplayName, StringComparison.Ordinal)
            && claimId == user.UserId.ToString())
            return;

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(ClaimTypes.Role, user.Role)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        context.ReplacePrincipal(new ClaimsPrincipal(identity));
        context.ShouldRenew = true;
    }

    private static async Task<AppUser?> EnsureAdminIfNoneAsync(
        IUserDirectory users, AppUser user, CancellationToken cancellationToken)
    {
        if (AppRoles.Rank(user.Role) >= AppRoles.Rank(AppRoles.Admin))
            return user;

        var admins = await users.CountActiveAdminsAsync(cancellationToken).ConfigureAwait(false);
        if (admins > 0)
            return user;

        user.Role = AppRoles.Admin;
        user.IsActive = true;
        await users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
        return await users.GetByIdAsync(user.UserId, cancellationToken).ConfigureAwait(false) ?? user;
    }
}
