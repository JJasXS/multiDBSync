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
    private readonly ILoginUserResolver _loginUsers;
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
        ILoginUserResolver loginUsers,
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
        _loginUsers = loginUsers;
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

    public async Task<(bool Ok, string Message, string? DebugCode)> SendOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _loginUsers.ResolveForLoginAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (false, "No active user matches that email.", null);

        var code = GenerateOtpCode();
        var minutes = Math.Clamp(_sync.OtpMinutes, 2, 30);
        _cache.Set("otp:" + email.Trim().ToLowerInvariant(), code, TimeSpan.FromMinutes(minutes));
        var debugCode = DevDebugOtpEnabled ? code : null;

        var subject = "Your Login OTP Code";
        var html = $@"
<html><body>
  <p>Your OTP code is:</p>
  <h2 style=""letter-spacing:4px"">{code}</h2>
  <p>This code expires in {minutes} minutes.</p>
</body></html>";

        var (sent, err) = await TrySendEmailAsync(email, subject, html, cancellationToken).ConfigureAwait(false);
        if (sent)
            return (true, "A sign-in code was sent to your email.", debugCode);

        if (DevDebugOtpEnabled)
        {
            Console.WriteLine($"[OTP] {email}: {code}");
            _logger.LogWarning("OTP email not delivered ({Error}); code written to console in development.", err);
            return (true, "Development mode: OTP auto-filled below.", debugCode);
        }

        return (false, string.IsNullOrWhiteSpace(err) ? "Could not send the sign-in email." : err, null);
    }

    private bool DevDebugOtpEnabled => _env.IsDevelopment() && _sync.DebugOtpToConsole;

    private string GenerateOtpCode()
    {
        if (DevDebugOtpEnabled)
        {
            var mock = (_sync.MockSignInCode ?? "").Trim();
            if (mock.Length == 6 && mock.All(char.IsDigit))
                return mock;
        }

        return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    }

    public async Task<(bool Ok, AppUser? User, string Message)> VerifyAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var key = "otp:" + email.Trim().ToLowerInvariant();
        if (!_cache.TryGetValue(key, out string? stored) || stored != code.Trim())
            return (false, null, "Invalid or expired code.");
        _cache.Remove(key);
        var user = await _loginUsers.ResolveForLoginAsync(email, cancellationToken).ConfigureAwait(false);
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
        var settings = await ResolveSmtpSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Error is not null)
            return (false, settings.Error);

        try
        {
            await SendSmtpHtmlAsync(settings.Value!, toAddress, subject, htmlBody, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("OTP email sent to {Email} via {Host}:{Port}", toAddress, settings.Value!.Host, settings.Value.Port);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP send failed.");
            return (false, "Could not send the sign-in email.");
        }
    }

    private sealed record SmtpSettings(string Host, int Port, string User, string Pass, bool EnableSsl);

    private async Task<(SmtpSettings? Value, string? Error)> ResolveSmtpSettingsAsync(CancellationToken cancellationToken)
    {
        var tenantEmail = await LoadTenantEmailOverrideAsync(cancellationToken).ConfigureAwait(false);
        if (tenantEmail?.OtpEnabled == false)
            return (null, "OTP email is disabled for this tenant.");

        var host = FirstNonEmpty(_smtp.Host, "smtp.gmail.com")!;
        var port = _smtp.Port <= 0 ? 587 : _smtp.Port;
        var user = FirstNonEmpty(_smtp.User, _smtp.From) ?? "";
        ApplyTenantSmtpFields(tenantEmail, ref host, ref port, ref user);

        var bundle = await LoadSmtpCredentialsBundleAsync(tenantEmail, cancellationToken).ConfigureAwait(false);
        ApplyBundleSmtpFields(bundle, ref host, ref port, ref user);

        var pass = await ResolveSmtpPasswordAsync(bundle, tenantEmail, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(host))
            return (null, "SMTP is not configured.");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            return (null,
                "SMTP user or password empty. Tenant must provide proaccEmail/email smtpCredentialsSecretRef or smtpAppPasswordSecretRef (same as eQuotation), and the app needs AWS credentials to read Secrets Manager.");
        }

        return (new SmtpSettings(host, port, user, pass, _smtp.EnableSsl), null);
    }

    private async Task<TenantEmailOverride?> LoadTenantEmailOverrideAsync(CancellationToken cancellationToken)
    {
        var tenantCode = (_config["TenantBootstrap:TenantCode"]
                          ?? _sync.TenantCode
                          ?? Environment.GetEnvironmentVariable("TENANT_CODE")
                          ?? "").Trim();
        if (tenantCode.Length == 0)
            return null;

        try
        {
            return await _tenantEmail.GetEmailOverrideAsync(tenantCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SMTP] Could not load tenant email overrides for {Tenant}.", tenantCode);
            return null;
        }
    }

    private async Task<TenantSmtpCredentialsSecretPayload?> LoadSmtpCredentialsBundleAsync(
        TenantEmailOverride? tenantEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantEmail?.SmtpCredentialsSecretRef))
            return null;

        return await TenantSmtpSecretResolver
            .ResolveSmtpCredentialsSecretAsync(_aws, tenantEmail.SmtpCredentialsSecretRef, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyTenantSmtpFields(
        TenantEmailOverride? tenantEmail, ref string host, ref int port, ref string user)
    {
        if (tenantEmail is null)
            return;
        host = FirstNonEmpty(tenantEmail.SmtpHost, host)!;
        if (tenantEmail.SmtpPort is > 0)
            port = tenantEmail.SmtpPort.Value;
        user = FirstNonEmpty(tenantEmail.SmtpSenderEmail, user) ?? user;
    }

    private static void ApplyBundleSmtpFields(
        TenantSmtpCredentialsSecretPayload? bundle, ref string host, ref int port, ref string user)
    {
        if (bundle is null)
            return;
        host = FirstNonEmpty(bundle.Host, host)!;
        if (bundle.Port is > 0)
            port = bundle.Port.Value;
        user = FirstNonEmpty(bundle.User, user) ?? user;
    }

    private async Task<string> ResolveSmtpPasswordAsync(
        TenantSmtpCredentialsSecretPayload? bundle,
        TenantEmailOverride? tenantEmail,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(bundle?.Password))
            return bundle.Password.Replace(" ", "").Trim();

        var fallback = await _secrets.ResolveAsync(_smtp.PasswordSecretRef, cancellationToken).ConfigureAwait(false) ?? "";
        return (await TenantSmtpSecretResolver.ResolveSmtpPasswordAsync(
                _aws,
                tenantEmail?.SmtpAppPasswordSecretRef,
                fallback,
                cancellationToken)
            .ConfigureAwait(false) ?? "").Replace(" ", "").Trim();
    }

    private static async Task SendSmtpHtmlAsync(
        SmtpSettings settings, string toAddress, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("SQL Account Sync", settings.User));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        var secure = settings.Port == 465
            ? SecureSocketOptions.SslOnConnect
            : (settings.EnableSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None);
        await client.ConnectAsync(settings.Host, settings.Port, secure, cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(settings.User, settings.Pass, cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            var t = (v ?? "").Trim();
            if (t.Length > 0)
                return t;
        }
        return null;
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
