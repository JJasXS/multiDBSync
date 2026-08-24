using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Secrets;

public interface ISqlApiSecretProvisioner
{
    /// <summary>
    /// Ensures a Secrets Manager secret exists for the Code convention (empty access/secret keys).
    /// Does not overwrite an existing secret.
    /// </summary>
    Task<(bool Ok, string Message)> EnsurePlaceholderAsync(string tenantCode, string dbCode, CancellationToken cancellationToken = default);
}

public sealed class SqlApiSecretProvisioner : ISqlApiSecretProvisioner
{
    private readonly IAmazonSecretsManager? _sm;
    private readonly SyncOptions _sync;
    private readonly ILogger<SqlApiSecretProvisioner> _logger;

    public SqlApiSecretProvisioner(
        IOptions<SyncOptions> sync,
        ILogger<SqlApiSecretProvisioner> logger,
        IAmazonSecretsManager? sm = null)
    {
        _sync = sync.Value;
        _logger = logger;
        _sm = sm;
    }

    public async Task<(bool Ok, string Message)> EnsurePlaceholderAsync(
        string tenantCode,
        string dbCode,
        CancellationToken cancellationToken = default)
    {
        if (!_sync.EnsureSqlApiSecretPlaceholder)
            return (false, "Secret placeholder creation is disabled.");

        var secretId = SqlApiCredRefConvention.BuildSecretId(tenantCode, dbCode);
        if (secretId.Length == 0)
            return (false, "Tenant code or database Code missing.");

        if (_sm is null)
            return (false, "AWS Secrets Manager client is not configured.");

        var code = SqlApiCredRefConvention.SanitizeDbCode(dbCode);
        try
        {
            await _sm.CreateSecretAsync(new CreateSecretRequest
            {
                Name = secretId,
                Description = $"SqlAccountSync SQL API credentials for tenant {tenantCode.Trim()} code {code}. Fill accessKey and secretKey.",
                SecretString = """{"accessKey":"","secretKey":""}""",
                Tags =
                [
                    new Tag { Key = "tenant", Value = tenantCode.Trim() },
                    new Tag { Key = "app", Value = "SqlAccountSync" },
                    new Tag { Key = "dbCode", Value = code }
                ]
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created SQL API secret placeholder {SecretId}", secretId);
            return (true, $"Created secret placeholder {secretId}.");
        }
        catch (ResourceExistsException)
        {
            return (true, $"Secret already exists: {secretId}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create SQL API secret placeholder {SecretId}", secretId);
            return (false, $"Could not create secret {secretId}: {ex.Message}");
        }
    }
}
