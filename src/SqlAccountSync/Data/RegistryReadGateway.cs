using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Data;

/// <summary>
/// SELECT-only access to the dashboard company registry.
/// Live mode opens the configured Firebird database in a read-only transaction.
/// Local mode forwards to <see cref="CentralDb"/>.
/// </summary>
public interface IRegistryReadGateway
{
    bool ReadsLiveRegistry { get; }
    bool IsConfigured { get; }
    Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken cancellationToken = default);
    Task<List<T>> QueryAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default);
}

public sealed class RegistryReadGateway : IRegistryReadGateway
{
    private readonly CentralDb _local;
    private readonly CentralRegistryOptions _options;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<RegistryReadGateway> _logger;

    public RegistryReadGateway(
        CentralDb local,
        IOptions<CentralRegistryOptions> options,
        ISecretResolver secrets,
        ILogger<RegistryReadGateway> logger)
    {
        _local = local;
        _options = options.Value;
        _secrets = secrets;
        _logger = logger;
        if (_options.IsLiveRequested && !_options.HasLiveTarget)
            _logger.LogWarning("CentralRegistry Mode is Live but Host or Path is empty. Dashboard registry stays on the local central database.");
        else if (ReadsLiveRegistry)
            _logger.LogInformation("Dashboard registry is read-only against {Host}:{Port} {Path}.", _options.Host.Trim(), _options.Port <= 0 ? 3050 : _options.Port, _options.Path.Trim());
    }

    public bool ReadsLiveRegistry => _options.IsLiveRequested && _options.HasLiveTarget;

    public bool IsConfigured => ReadsLiveRegistry || _local.IsConfigured;

    public Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken cancellationToken = default)
    {
        if (!ReadsLiveRegistry)
            return _local.ScalarAsync(sql, args, cancellationToken);
        return ScalarLiveAsync(sql, args, cancellationToken);
    }

    private async Task<object?> ScalarLiveAsync(string sql, object? args, CancellationToken cancellationToken)
    {
        SqlGuard.EnsureSelectOnly(sql);
        await using var conn = await OpenLiveAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            new FbTransactionOptions { TransactionBehavior = FbTransactionBehavior.Read },
            cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FbConnection> OpenLiveAsync(CancellationToken cancellationToken)
    {
        var conn = new FbConnection(await BuildConnectionStringAsync(cancellationToken).ConfigureAwait(false));
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default)
    {
        if (!ReadsLiveRegistry)
            return await _local.QueryAsync(sql, map, args, cancellationToken).ConfigureAwait(false);

        SqlGuard.EnsureSelectOnly(sql);
        await using var conn = await OpenLiveAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            new FbTransactionOptions { TransactionBehavior = FbTransactionBehavior.Read },
            cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        CentralDb.Bind(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(map(reader));
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }

    private async Task<string> BuildConnectionStringAsync(CancellationToken cancellationToken)
    {
        var password = await _secrets.ResolveAsync(_options.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        return new FbConnectionStringBuilder
        {
            DataSource = _options.Host.Trim(),
            Port = _options.Port <= 0 ? 3050 : _options.Port,
            Database = _options.Path.Replace('\\', '/'),
            UserID = string.IsNullOrWhiteSpace(_options.User) ? "SYSDBA" : _options.User.Trim(),
            Password = password ?? "",
            Charset = string.IsNullOrWhiteSpace(_options.Charset) ? "UTF8" : _options.Charset,
            Dialect = (byte)Math.Clamp(_options.Dialect, 1, 3),
            Pooling = false
        }.ToString();
    }
}
