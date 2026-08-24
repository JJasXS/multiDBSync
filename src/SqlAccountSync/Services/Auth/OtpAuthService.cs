using System.Security.Cryptography;
using Amazon.SecretsManager;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MimeKit;
using SqlAccountSync.Authorization;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;
using SqlAccountSync.Services.Tenants;
using System.Security.Claims;

namespace SqlAccountSync.Services.Auth;

public sealed class OtpAuthService
{
    private readonly IUserDirectory _users;
    private readonly IMemoryCache _cache;
    private readonly ISecretResolver _secrets;
    private readonly ITenantEmailBootstrap _tenantEmail;
    private readonly IAmazonSecretsManager? _aws;
    private readonly IConfiguration _config;
    private readonly SmtpOptions _smtp;
    private readonly SyncOptions _sync;
    private readonly IHostEnvironment _env;
    private readonly ILogger<OtpAuthService> _logger;

    public OtpAuthService(
        IUserDirectory users,
        IMemoryCache cache,
        ISecretResolver secrets,
        ITenantEmailBootstrap tenantEmail,
        IConfiguration config,
        IOptions<SmtpOptions> smtp,
        IOptions<SyncOptions> sync,
        IHostEnvironment env,
        ILogger<OtpAuthService> logger,
        IAmazonSecretsManager? aws = null)
    {
        _users = users;
        _cache = cache;
        _secrets = secrets;
        _tenantEmail = tenantEmail;
        _config = config;
        _smtp = smtp.Value;
        _sync = sync.Value;
        _env = env;
        _logger = logger;
        _aws = aws;
    }

    public async Task<(bool Ok, string Message)> SendOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (false, "No active user matches that email.");

        var code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var minutes = Math.Clamp(_sync.OtpMinutes, 2, 30);
        _cache.Set("otp:" + email.Trim().ToLowerInvariant(), code, TimeSpan.FromMinutes(minutes));

        var subject = "Your Login OTP Code";
        var html = $@"
<html><body>
  <p>Your OTP code is:</p>
  <h2 style=""letter-spacing:4px"">{code}</h2>
  <p>This code expires in {minutes} minutes.</p>
</body></html>";

        var (sent, err) = await TrySendEmailAsync(email, subject, html, cancellationToken).ConfigureAwait(false);
        if (sent)
            return (true, "A sign-in code was sent to your email.");

        if (_env.IsDevelopment() && _sync.DebugOtpToConsole)
        {
            Console.WriteLine($"[OTP] {email}: {code}");
            _logger.LogWarning("OTP email not delivered ({Error}); code written to console in development.", err);
            return (true, "Email send failed; OTP written to the server console in development.");
        }

        return (false, string.IsNullOrWhiteSpace(err) ? "Could not send the sign-in email." : err);
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

    /// <summary>
    /// Same merge order as eQuotation / ApprovalPO:
    /// appsettings Smtp → tenant email/proaccEmail → smtpCredentialsSecretRef → smtpAppPasswordSecretRef.
    /// </summary>
    private async Task<(bool Sent, string? Error)> TrySendEmailAsync(
        string toAddress, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        var tenantCode = (_config["TenantBootstrap:TenantCode"]
                          ?? _sync.TenantCode
                          ?? Environment.GetEnvironmentVariable("TENANT_CODE")
                          ?? "").Trim();

        TenantEmailOverride? tenantEmail = null;
        if (tenantCode.Length > 0)
        {
            try
            {
                tenantEmail = await _tenantEmail.GetEmailOverrideAsync(tenantCode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SMTP] Could not load tenant email overrides for {Tenant}.", tenantCode);
            }
        }

        if (tenantEmail?.OtpEnabled == false)
            return (false, "OTP email is disabled for this tenant.");

        var host = (_smtp.Host ?? "").Trim();
        if (host.Length == 0)
            host = "smtp.gmail.com";
        var port = _smtp.Port <= 0 ? 587 : _smtp.Port;
        var user = (_smtp.User ?? "").Trim();
        if (string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(_smtp.From))
            user = _smtp.From.Trim();

        if (tenantEmail is not null)
        {
            if (!string.IsNullOrWhiteSpace(tenantEmail.SmtpHost))
                host = tenantEmail.SmtpHost.Trim();
            if (tenantEmail.SmtpPort is > 0)
                port = tenantEmail.SmtpPort.Value;
            if (!string.IsNullOrWhiteSpace(tenantEmail.SmtpSenderEmail))
                user = tenantEmail.SmtpSenderEmail.Trim();
        }

        TenantSmtpCredentialsSecretPayload? bundle = null;
        if (!string.IsNullOrWhiteSpace(tenantEmail?.SmtpCredentialsSecretRef))
        {
            bundle = await TenantSmtpSecretResolver
                .ResolveSmtpCredentialsSecretAsync(_aws, tenantEmail.SmtpCredentialsSecretRef, cancellationToken)
                .ConfigureAwait(false);
            if (bundle is not null)
            {
                if (!string.IsNullOrWhiteSpace(bundle.Host))
                    host = bundle.Host.Trim();
                if (bundle.Port is > 0)
                    port = bundle.Port.Value;
                if (!string.IsNullOrWhiteSpace(bundle.User))
                    user = bundle.User.Trim();
            }
        }

        string pass;
        if (!string.IsNullOrWhiteSpace(bundle?.Password))
        {
            pass = bundle.Password.Replace(" ", "").Trim();
        }
        else
        {
            var fallback = await _secrets.ResolveAsync(_smtp.PasswordSecretRef, cancellationToken).ConfigureAwait(false) ?? "";
            pass = (await TenantSmtpSecretResolver.ResolveSmtpPasswordAsync(
                    _aws,
                    tenantEmail?.SmtpAppPasswordSecretRef,
                    fallback,
                    cancellationToken)
                .ConfigureAwait(false) ?? "").Replace(" ", "").Trim();
        }

        if (string.IsNullOrWhiteSpace(host))
            return (false, "SMTP is not configured.");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            return (false,
                "SMTP user or password empty. Tenant must provide proaccEmail/email smtpCredentialsSecretRef or smtpAppPasswordSecretRef (same as eQuotation), and the app needs AWS credentials to read Secrets Manager.");
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("SQL Account Sync", user));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = htmlBody };

            using var client = new SmtpClient();
            var secure = port == 465
                ? SecureSocketOptions.SslOnConnect
                : (_smtp.EnableSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None);
            await client.ConnectAsync(host, port, secure, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("OTP email sent to {Email} via {Host}:{Port}", toAddress, host, port);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP send failed.");
            return (false, "Could not send the sign-in email.");
        }
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
