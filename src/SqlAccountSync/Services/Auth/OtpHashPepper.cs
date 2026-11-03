using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Auth;

/// <summary>
/// HMAC key for OTP digests. Prefer a shared secret via <see cref="SyncOptions.OtpHashPepperSecretRef"/>
/// so multiple app instances validate the same codes. Falls back to a process-local random key.
/// </summary>
public sealed class OtpHashPepper
{
    public OtpHashPepper(ISecretResolver secrets, IOptions<SyncOptions> sync, ILogger<OtpHashPepper> logger)
    {
        var configured = (sync.Value.OtpHashPepperSecretRef ?? "").Trim();
        byte[] key;
        if (configured.Length > 0)
        {
            var secret = secrets.ResolveAsync(configured).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(secret))
            {
                key = SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()));
                logger.LogInformation("OTP HMAC pepper loaded from secret reference.");
                Key = key;
                IsShared = true;
                return;
            }

            logger.LogWarning(
                "OTP HMAC pepper secret ref '{Ref}' resolved empty; using process-local pepper.",
                configured);
        }

        key = RandomNumberGenerator.GetBytes(32);
        logger.LogWarning(
            "OTP HMAC pepper is process-local. Set Sync:OtpHashPepperSecretRef (env: or sm:) for multi-instance deployments.");
        Key = key;
        IsShared = false;
    }

    /// <summary>Test constructor.</summary>
    public OtpHashPepper(byte[] key)
    {
        Key = key.Length >= 16 ? key : SHA256.HashData(key);
        IsShared = true;
    }

    public byte[] Key { get; }
    public bool IsShared { get; }

    public string HashOtp(string code)
    {
        var digest = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(code ?? ""));
        return Convert.ToHexString(digest);
    }
}
