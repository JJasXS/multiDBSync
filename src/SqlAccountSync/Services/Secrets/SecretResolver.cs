using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Secrets;

public interface ISecretResolver
{
    Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves secret references. Supported prefixes:
/// env:VARNAME — process environment
/// sm:secret-id — AWS Secrets Manager (raw string or JSON "password"/"SecretString")
/// dpapi:filename — DPAPI-protected file under ContentRoot/secrets
/// </summary>
public sealed class SecretResolver : ISecretResolver
{
    private readonly IAmazonSecretsManager? _secrets;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SecretResolver> _logger;

    public SecretResolver(
        IWebHostEnvironment env,
        ILogger<SecretResolver> logger,
        IAmazonSecretsManager? secrets = null)
    {
        _env = env;
        _logger = logger;
        _secrets = secrets;
    }

    [SupportedOSPlatform("windows")]
    public async Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default)
    {
        var r = (secretRef ?? "").Trim();
        if (r.Length == 0)
            return null;

        if (r.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = r[4..].Trim();
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        if (r.StartsWith("sm:", StringComparison.OrdinalIgnoreCase))
        {
            if (_secrets is null)
            {
                _logger.LogWarning("Secrets Manager secret requested but AWS client is not configured.");
                return null;
            }
            var id = r[3..].Trim();
            try
            {
                var resp = await _secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = id }, cancellationToken)
                    .ConfigureAwait(false);
                return InterpretSecretString(resp.SecretString);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Secrets Manager reference.");
                return null;
            }
        }

        if (r.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(r[6..].Trim());
            if (file.Length == 0)
                return null;
            var path = Path.Combine(_env.ContentRootPath, "secrets", file);
            if (!File.Exists(path))
                return null;
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unprotect DPAPI secret file.");
                return null;
            }
        }

        // Bare environment variable name fallback
        var envVal = Environment.GetEnvironmentVariable(r);
        return string.IsNullOrWhiteSpace(envVal) ? null : envVal;
    }

    [SupportedOSPlatform("windows")]
    public static void ProtectToFile(string contentRoot, string fileName, string secret)
    {
        Directory.CreateDirectory(Path.Combine(contentRoot, "secrets"));
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(contentRoot, "secrets", Path.GetFileName(fileName)), bytes);
    }

    private static string? InterpretSecretString(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0)
            return null;
        if (!s.StartsWith('{'))
            return s;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            foreach (var name in new[] { "password", "Password", "secret", "SecretString", "value" })
            {
                if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.GetString();
            }
        }
        catch
        {
            // not JSON
        }
        return s;
    }
}
