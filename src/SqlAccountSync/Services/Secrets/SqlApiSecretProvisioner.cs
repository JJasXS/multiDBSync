using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SqlAccountSync.Helpers;

namespace SqlAccountSync.Services.Secrets;

public interface ISqlApiSecretProvisioner
{
    /// <summary>
    /// Creates or updates the SQL API secret for this company code with the given keys.
    /// </summary>
    Task<(bool Ok, string Message)> SaveCredentialsAsync(
        string tenantCode,
        string dbCode,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default);
}

public sealed class SqlApiSecretProvisioner : ISqlApiSecretProvisioner
{
    private readonly IAmazonSecretsManager? _sm;
    private readonly ILogger<SqlApiSecretProvisioner> _logger;

    public SqlApiSecretProvisioner(
        ILogger<SqlApiSecretProvisioner> logger,
        IAmazonSecretsManager? sm = null)
    {
        _logger = logger;
        _sm = sm;
    }

    public async Task<(bool Ok, string Message)> SaveCredentialsAsync(
        string tenantCode,
        string dbCode,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default)
    {
        var secretId = SqlApiCredRefConvention.BuildSecretId(tenantCode, dbCode);
        if (secretId.Length == 0)
            return (false, "Tenant code or database Code missing.");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            return (false, "Access key and secret key are both required.");
        if (_sm is null)
            return (false, "AWS Secrets Manager client is not configured.");

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["accessKey"] = accessKey.Trim(),
            ["secretKey"] = secretKey.Trim()
        });
        var code = SqlApiCredRefConvention.SanitizeDbCode(dbCode);
        try
        {
            await CreateSecretAsync(secretId, tenantCode, code, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created SQL API secret {SecretId}", secretId);
            return (true, $"Saved secret {secretId}.");
        }
        catch (ResourceExistsException)
        {
            return await UpdateSecretAsync(secretId, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save SQL API secret {SecretId}", secretId);
            return (false, $"Could not save secret {secretId}: {ex.Message}");
        }
    }

    private Task CreateSecretAsync(
        string secretId, string tenantCode, string code, string payload, CancellationToken cancellationToken) =>
        _sm!.CreateSecretAsync(new CreateSecretRequest
        {
            Name = secretId,
            Description = $"SqlAccountSync SQL API credentials for tenant {tenantCode.Trim()} code {code}.",
            SecretString = payload,
            Tags =
            [
                new Tag { Key = "tenant", Value = tenantCode.Trim() },
                new Tag { Key = "app", Value = "SqlAccountSync" },
                new Tag { Key = "dbCode", Value = code }
            ]
        }, cancellationToken);

    private async Task<(bool Ok, string Message)> UpdateSecretAsync(
        string secretId, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await _sm!.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = secretId,
                SecretString = payload
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated SQL API secret {SecretId}", secretId);
            return (true, $"Updated secret {secretId}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update SQL API secret {SecretId}", secretId);
            return (false, $"Could not update secret {secretId}: {ex.Message}");
        }
    }
}
