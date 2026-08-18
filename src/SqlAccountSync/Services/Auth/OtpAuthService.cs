using System.Security.Cryptography;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MimeKit;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;
using System.Security.Claims;

namespace SqlAccountSync.Services.Auth;

public sealed class OtpAuthService
{
    private readonly IUserDirectory _users;
    private readonly IMemoryCache _cache;
    private readonly ISecretResolver _secrets;
    private readonly SmtpOptions _smtp;
    private readonly SyncOptions _sync;
    private readonly ILogger<OtpAuthService> _logger;

    public OtpAuthService(
        IUserDirectory users,
        IMemoryCache cache,
        ISecretResolver secrets,
        IOptions<SmtpOptions> smtp,
        IOptions<SyncOptions> sync,
        ILogger<OtpAuthService> logger)
    {
        _users = users;
        _cache = cache;
        _secrets = secrets;
        _smtp = smtp.Value;
        _sync = sync.Value;
        _logger = logger;
    }

    public async Task<(bool Ok, string Message)> SendOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (false, "No active user matches that email.");

        var code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        if (_sync.DebugOtpToConsole && !string.IsNullOrWhiteSpace(_sync.MockSignInCode))
            code = _sync.MockSignInCode.Trim();
        _cache.Set("otp:" + email.Trim().ToLowerInvariant(), code, TimeSpan.FromMinutes(Math.Clamp(_sync.OtpMinutes, 2, 30)));

        if (_sync.DebugOtpToConsole)
            _logger.LogInformation("OTP for {Email} generated (console debug enabled).", email);

        if (string.IsNullOrWhiteSpace(_smtp.Host))
        {
            if (_sync.DebugOtpToConsole)
                Console.WriteLine($"[OTP] {email}: {code}");
            var hint = _sync.DebugOtpToConsole && !string.IsNullOrWhiteSpace(_sync.MockSignInCode)
                ? $" Development code: {code}"
                : " In development it is written to the console when SMTP is not configured.";
            return (true, "OTP issued." + hint);
        }

        try
        {
            var password = await _secrets.ResolveAsync(_smtp.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(_smtp.From) ? _smtp.User : _smtp.From));
            msg.To.Add(MailboxAddress.Parse(email));
            msg.Subject = "SQL Account Sync sign-in code";
            msg.Body = new TextPart("plain") { Text = $"Your sign-in code is {code}. It expires in {_sync.OtpMinutes} minutes." };
            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.EnableSsl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(_smtp.User))
                await client.AuthenticateAsync(_smtp.User, password ?? "", cancellationToken).ConfigureAwait(false);
            await client.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            return (true, "A sign-in code was sent to your email.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send OTP email.");
            if (_sync.DebugOtpToConsole)
            {
                Console.WriteLine($"[OTP] {email}: {code}");
                return (true, "Email send failed; OTP written to console in development.");
            }
            return (false, "Could not send the sign-in email.");
        }
    }

    public async Task<(bool Ok, AppUser? User, string Message)> VerifyAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var key = "otp:" + email.Trim().ToLowerInvariant();
        if (!_cache.TryGetValue(key, out string? stored) || stored != code.Trim())
            return (false, null, "Invalid or expired code.");
        _cache.Remove(key);
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (false, null, "User is no longer active.");
        return (true, user, "Signed in.");
    }

    public static async Task SignInAsync(HttpContext http, AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role)
        };
        var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
    }
}

public static class CurrentUser
{
    public static int UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public static string Email(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "";

    public static string Role(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role) ?? AppRoles.Viewer;

    public static bool CanManage(ClaimsPrincipal user) => AppRoles.Rank(Role(user)) >= AppRoles.Rank(AppRoles.Manager);

    public static bool IsAdmin(ClaimsPrincipal user) => AppRoles.Rank(Role(user)) >= AppRoles.Rank(AppRoles.Admin);

    public static bool CanOperate(ClaimsPrincipal user) => AppRoles.Rank(Role(user)) >= AppRoles.Rank(AppRoles.Operator);
}
