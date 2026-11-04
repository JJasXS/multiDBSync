namespace SqlAccountSync.Services.SqlAccount;

/// <summary>
/// Fail-fast HttpMessageHandler for SQL Accounting REST clients when SDK-only mode is active.
/// Ensures no request can reach api.sql.my even if an adapter bypasses the Provider refuse gate.
/// </summary>
public sealed class BlockSqlAccountingRestHandler : DelegatingHandler
{
    public const string BlockMessage =
        "SQL Accounting REST HTTP blocked (SqlAccountingWrite SDK-only / BlockRestHttp). No request was sent.";

    public BlockSqlAccountingRestHandler()
    {
    }

    public BlockSqlAccountingRestHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(BlockMessage);
}