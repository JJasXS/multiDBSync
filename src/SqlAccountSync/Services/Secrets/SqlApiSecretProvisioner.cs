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
    private readonly ISqlApiCredentialBundle _bundle;

    public SqlApiSecretProvisioner(ISqlApiCredentialBundle bundle)
    {
        _bundle = bundle;
    }

    public Task<(bool Ok, string Message)> SaveCredentialsAsync(
        string tenantCode,
        string dbCode,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default) =>
        _bundle.SaveAsync(tenantCode, dbCode, accessKey, secretKey, cancellationToken);
}
