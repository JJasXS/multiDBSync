using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.SecretsManager;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
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
    public const int MaxVerifyFailures = 5;
    public const int SendCooldownSeconds = 30;
    public const int MaxSendsPerEmailPerHour = 10;
    public const int MaxSendsPerIpPerHour = 30;
    public const int MaxVerifyPerIpPerHour = 60;

    private static readonly ConcurrentDictionary<string, object> KeyLocks = new(StringComparer.Ordinal);

    private readonly ILoginUserResolver _loginUsers;
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache _distributed;
    private readonly OtpHashPepper _pepper;
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
        IDistributedCache distributed,
        OtpHashPepper pepper,
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
        _distributed = distributed;
        _pepper = pepper;
        _secrets = secrets;
        _tenantEmail = tenantEmail;
        _config = config;
        _smtp = smtp.Value;
        _sync = sync.Value;
        _env = env;
        _logger = logger;
        _aws = aws;
    }

    /// <summary>
    /// Requests an OTP. Always returns a generic success-shaped message for unknown emails
    /// (no account enumeration). Debug OTP is returned only for local loopback + explicit opt-in.
    /// </summary>
    public async Task<(bool Ok, string Message, string? DebugCode)> SendOtpAsync(
        string email,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0 || !normalized.Contains('@', StringComparison.Ordinal))
            return (false, "Enter a valid email address.", null);

        if (!await TryAcquireSendQuotaAsync(normalized, clientIp, cancellationToken).ConfigureAwait(false))
            return (false, "Please wait before requesting another code.", null);

        var user = await _loginUsers.ResolveForLoginAsync(normalized, cancellationToken).ConfigureAwait(false);
        const string genericOk = "If that email is registered, a sign-in code was sent.";

        if (user is null)
        {
            _logger.LogInformation("OTP requested for unknown or inactive email (not enumerated to client).");
            return (true, genericOk, null);
        }

        var code = GenerateOtpCode();
        var minutes = Math.Clamp(_sync.OtpMinutes, 2, 30);
        StoreOtp(normalized, code, TimeSpan.FromMinutes(minutes));

        var subject = "Your Login OTP Code";
        var html = $@"
<html><body>
  <p>Your OTP code is:</p>
  <h2 style=""letter-spacing:4px"">{WebUtility.HtmlEncode(code)}</h2>
  <p>This code expires in {minutes} minutes.</p>
</body></html>";

        var (sent, err) = await TrySendEmailAsync(normalized, subject, html, cancellationToken).ConfigureAwait(false);
        var debugCode = CanRevealDebugOtp(clientIp) ? code : null;

        if (sent)
            return (true, genericOk, debugCode);

        if (debugCode is not null)
        {
            _logger.LogWarning(
                "OTP email not delivered in local Development ({Error}); debug OTP returned to loopback UI only.",
                err);
            return (true, "Development mode: use the code shown on this page.", debugCode);
        }

        return (false, "Could not send the sign-in email. Try again later.", null);
    }

    /// <summary>
    /// Debug OTP in HTTP responses requires Development + AppMode=dev + AllowLocalOtpReveal + loopback IP.
    /// Never active for non-loopback clients (publicly reachable deployments).
    /// </summary>
    internal bool CanRevealDebugOtp(string? clientIp) =>
        _env.IsDevelopment()
        && _sync.IsDevAppMode
        && _sync.AllowLocalOtpReveal
        && HostHardening.IsLoopbackIp(clientIp);

    private static string GenerateOtpCode() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");

    public async Task<(bool Ok, AppUser? User, string Message)> VerifyAsync(
        string email,
        string code,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email);
        var trimmedCode = (code ?? "").Trim();
        if (normalized.Length == 0 || trimmedCode.Length == 0)
            return (false, null, "Invalid or expired code.");

        if (!await TryAcquireVerifyIpQuotaAsync(clientIp, cancellationToken).ConfigureAwait(false))
            return (false, null, "Too many verification attempts. Try again later.");

        var key = OtpKey(normalized);
        var gate = KeyLocks.GetOrAdd(key, static _ => new object());
        OtpEntry? entry;
        lock (gate)
        {
            if (!_cache.TryGetValue(key, out entry) || entry is null)
                return (false, null, "Invalid or expired code.");

            if (!FixedTimeEquals(entry.CodeHash, _pepper.HashOtp(trimmedCode)))
            {
                entry.Failures++;
                if (entry.Failures >= MaxVerifyFailures)
                {
                    _cache.Remove(key);
                    _logger.LogWarning("OTP invalidated after {Failures} failed attempts.", entry.Failures);
                    return (false, null, "Too many invalid attempts. Request a new code.");
                }

                _cache.Set(key, entry, entry.AbsoluteExpiration);
                return (false, null, "Invalid or expired code.");
            }

            _cache.Remove(key);
        }

        var user = await _loginUsers.ResolveForLoginAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (user is null)
            return (false, null, "Invalid or expired code.");

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
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(id),
            new AuthenticationProperties { IsPersistent = false });
    }

    private void StoreOtp(string normalizedEmail, string code, TimeSpan lifetime)
    {
        var key = OtpKey(normalizedEmail);
        var entry = new OtpEntry
        {
            CodeHash = _pepper.HashOtp(code),
            Failures = 0,
            AbsoluteExpiration = DateTimeOffset.UtcNow.Add(lifetime)
        };
        _cache.Set(key, entry, entry.AbsoluteExpiration);
    }

    private async Task<bool> TryAcquireSendQuotaAsync(string email, string? clientIp, CancellationToken cancellationToken)
    {
        var cooldownKey = $"otp:send:cd:{email}";
        if (_cache.TryGetValue(cooldownKey, out _))
            return false;

        if (!await TryIncrementWindowAsync($"otp:send:email:{email}", MaxSendsPerEmailPerHour, TimeSpan.FromHours(1), cancellationToken)
                .ConfigureAwait(false))
            return false;

        var ip = NormalizeIp(clientIp);
        if (ip.Length > 0
            && !await TryIncrementWindowAsync($"otp:send:ip:{ip}", MaxSendsPerIpPerHour, TimeSpan.FromHours(1), cancellationToken)
                .ConfigureAwait(false))
            return false;

        _cache.Set(cooldownKey, 1, TimeSpan.FromSeconds(SendCooldownSeconds));
        return true;
    }

    private async Task<bool> TryAcquireVerifyIpQuotaAsync(string? clientIp, CancellationToken cancellationToken)
    {
        var ip = NormalizeIp(clientIp);
        if (ip.Length == 0)
            return true;
        return await TryIncrementWindowAsync(
                $"otp:verify:ip:{ip}", MaxVerifyPerIpPerHour, TimeSpan.FromHours(1), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Process-local lock + distributed counter so multi-instance hosts share soft rate limits
    /// when a shared <see cref="IDistributedCache"/> (e.g. Redis) is configured.
    /// </summary>
    private async Task<bool> TryIncrementWindowAsync(string key, int max, TimeSpan window, CancellationToken cancellationToken)
    {
        var gate = KeyLocks.GetOrAdd(key, static _ => new object());
        lock (gate)
        {
            var local = _cache.TryGetValue(key, out int n) ? n : 0;
            if (local >= max)
                return false;
            _cache.Set(key, local + 1, window);
        }

        try
        {
            var existing = await _distributed.GetAsync(key, cancellationToken).ConfigureAwait(false);
            var count = existing is { Length: >= 4 } ? BitConverter.ToInt32(existing, 0) : 0;
            if (count >= max)
                return false;
            var next = BitConverter.GetBytes(count + 1);
            await _distributed.SetAsync(
                    key,
                    next,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = window },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Distributed OTP rate-limit cache unavailable; using process-local limits only.");
        }

        return true;
    }

    private static string OtpKey(string normalizedEmail) => "otp:" + normalizedEmail;

    private static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static string NormalizeIp(string? ip)
    {
        var t = (ip ?? "").Trim();
        if (t.Length == 0 || t.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "";
        return t.Length > 64 ? t[..64] : t;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private sealed class OtpEntry
    {
        public required string CodeHash { get; init; }
        public int Failures { get; set; }
        public DateTimeOffset AbsoluteExpiration { get; init; }
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

    public static bool CanManage(ClaimsPrincipal user) => IsAdmin(user);

    public static bool IsAdmin(ClaimsPrincipal user) => AppRoles.Rank(Role(user)) >= AppRoles.Rank(AppRoles.Admin);

    public static bool CanOperate(ClaimsPrincipal user) => IsAdmin(user);
}
