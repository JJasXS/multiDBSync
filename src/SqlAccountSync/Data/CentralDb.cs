using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Data;

public sealed class CentralDb
{
    private readonly CentralDbOptions _options;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<CentralDb> _logger;

    public CentralDb(IOptions<CentralDbOptions> options, ISecretResolver secrets, ILogger<CentralDb> logger)
    {
        _options = options.Value;
        _secrets = secrets;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Path) && !string.IsNullOrWhiteSpace(_options.Host);

    public async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        var path = _options.Path.Replace('/', Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(path))
            return;

        var cs = await BuildConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Creating central Firebird database at {Path}", path);
        FbConnection.CreateDatabase(cs, overwrite: false);
    }

    public async Task<FbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var cs = await BuildConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        var conn = new FbConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    public async Task<string> BuildConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        var password = await _secrets.ResolveAsync(_options.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        var csb = new FbConnectionStringBuilder
        {
            DataSource = _options.Host,
            Port = _options.Port,
            Database = _options.Path.Replace('\\', '/'),
            UserID = _options.User,
            Password = password ?? "",
            Charset = string.IsNullOrWhiteSpace(_options.Charset) ? "UTF8" : _options.Charset,
            Dialect = (byte)Math.Clamp(_options.Dialect, 1, 3),
            Pooling = _options.Pooling
        };
        return csb.ToString();
    }

    public async Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<object?> ScalarAsync(string sql, object? args = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(map(reader));
        return list;
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, Func<FbDataReader, T> map, object? args = null, CancellationToken cancellationToken = default)
    {
        var list = await QueryAsync(sql, map, args, cancellationToken).ConfigureAwait(false);
        return list.Count == 0 ? default : list[0];
    }

    public async Task<long> NextIdAsync(string generator, CancellationToken cancellationToken = default)
    {
        var v = await ScalarAsync($"SELECT GEN_ID({generator}, 1) FROM RDB$DATABASE", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt64(v);
    }

    public static void Bind(FbCommand cmd, object? args)
    {
        if (args is null)
            return;
        if (args is IDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = kv.Key.StartsWith('@') ? kv.Key : "@" + kv.Key;
                p.Value = kv.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            return;
        }

        foreach (var prop in args.GetType().GetProperties())
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + prop.Name;
            p.Value = prop.GetValue(args) ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    public static string Str(FbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i))?.Trim() ?? "";
    }

    public static int Int(FbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    }

    public static int? IntN(FbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
    }

    public static bool Flag(FbDataReader r, string col) => Int(r, col) != 0;

    public static DateTime? Time(FbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i))
            return null;
        return Convert.ToDateTime(r.GetValue(i));
    }

    public static DateTime TimeReq(FbDataReader r, string col) => Time(r, col) ?? DateTime.MinValue;
}

public sealed class CompanyConnectionFactory
{
    private readonly ISecretResolver _secrets;
    private readonly IHostEnvironment? _environment;
    private readonly CentralRegistryOptions? _registry;

    public CompanyConnectionFactory(
        ISecretResolver secrets,
        IHostEnvironment? environment = null,
        IOptions<CentralRegistryOptions>? registry = null)
    {
        _secrets = secrets;
        _environment = environment;
        _registry = registry?.Value;
    }

    /// <summary>
    /// Development + live registry stores company hosts as localhost, which is the live Firebird
    /// server. Use CentralRegistry:Host for those connections. Production keeps the registry host.
    /// </summary>
    public string EffectiveHost(RegisteredDatabase db)
    {
        var registryHost = (db.FbHost ?? "").Trim();
        if (_environment?.IsDevelopment() != true)
            return registryHost;
        if (!registryHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return registryHost;
        var overrideHost = (_registry?.Host ?? "").Trim();
        if (_registry?.IsLiveRequested != true || overrideHost.Length == 0)
            return registryHost;
        return overrideHost;
    }

    public async Task<string> BuildAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var password = await _secrets.ResolveAsync(db.FbCredRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            // Local Firebird default when the reference is blank / unset.
            password = Environment.GetEnvironmentVariable("CENTRAL_FB_MASTERKEY")
                ?? Environment.GetEnvironmentVariable("CENTRAL_FB_PASSWORD")
                ?? "masterkey";
        }

        if (string.IsNullOrEmpty(password))
        {
            var r = (db.FbCredRef ?? "").Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(r)
                    ? "Firebird password secret reference is empty. Use masterkey, env:VAR, sm:secret-id, or dpapi:file."
                    : $"Could not resolve Firebird credential reference '{r}'. Use masterkey (default SYSDBA), env:CENTRAL_FB_MASTERKEY, sm:, or dpapi:.");
        }

        var csb = new FbConnectionStringBuilder
        {
            DataSource = EffectiveHost(db),
            Port = db.FbPort <= 0 ? 3050 : db.FbPort,
            Database = db.FbPath.Replace('\\', '/'),
            UserID = string.IsNullOrWhiteSpace(db.FbUser) ? "SYSDBA" : db.FbUser,
            Password = password,
            Charset = string.IsNullOrWhiteSpace(db.FbCharset) ? "UTF8" : db.FbCharset,
            Dialect = (byte)Math.Clamp(db.FbDialect <= 0 ? 3 : db.FbDialect, 1, 3),
            Pooling = true
        };
        return csb.ToString();
    }

    public async Task<FbConnection> OpenAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var cs = await BuildAsync(db, cancellationToken).ConfigureAwait(false);
        var conn = new FbConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Drop pooled connections for this company database. The next <see cref="OpenAsync"/>
    /// opens a new physical connection instead of reusing one that just failed.
    /// </summary>
    public async Task DiscardPooledConnectionAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var cs = await BuildAsync(db, cancellationToken).ConfigureAwait(false);
        using var pooled = new FbConnection(cs);
        FbConnection.ClearPool(pooled);
    }
}
