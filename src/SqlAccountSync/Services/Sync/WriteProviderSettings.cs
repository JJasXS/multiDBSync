using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Sync;

public interface IWriteProviderSettings
{
    string Current { get; }
    bool IsSdk { get; }
    bool IsRest { get; }
    Task<string> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(string provider, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists API vs SDK write mode in SYNC_SETTING so the nav toggle survives restarts.
/// </summary>
public sealed class WriteProviderSettings : IWriteProviderSettings
{
    public const string SettingKey = "SQL_ACCOUNTING_WRITE_PROVIDER";
    private const string CacheKey = "write-provider:current";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly CentralDb _db;
    private readonly IMemoryCache _cache;
    private readonly SqlAccountingWriteOptions _defaults;

    public WriteProviderSettings(
        CentralDb db,
        IMemoryCache cache,
        IOptions<SqlAccountingWriteOptions> defaults)
    {
        _db = db;
        _cache = cache;
        _defaults = defaults.Value;
    }

    public string Current =>
        _cache.TryGetValue(CacheKey, out string? hit) && !string.IsNullOrWhiteSpace(hit)
            ? Normalize(hit)
            : Normalize(_defaults.Provider);

    public bool IsSdk => SqlAccountingWriteProviders.IsSdk(Current);
    public bool IsRest => SqlAccountingWriteProviders.IsRest(Current);

    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            return Normalize(cached);

        var fromDb = await ReadDbAsync(cancellationToken).ConfigureAwait(false);
        var value = string.IsNullOrWhiteSpace(fromDb)
            ? Normalize(_defaults.Provider)
            : Normalize(fromDb);
        _cache.Set(CacheKey, value, CacheFor);
        return value;
    }

    public async Task SetAsync(string provider, CancellationToken cancellationToken = default)
    {
        var value = Normalize(provider);
        if (!_db.IsConfigured)
            throw new InvalidOperationException("Central database is not configured.");

        await _db.ExecuteAsync(
            """
            UPDATE OR INSERT INTO SYNC_SETTING (SETKEY, SETVALUE, UPDATEDAT)
            VALUES (@SETKEY, @SETVALUE, CURRENT_TIMESTAMP)
            MATCHING (SETKEY)
            """,
            new { SETKEY = SettingKey, SETVALUE = value },
            cancellationToken).ConfigureAwait(false);

        _cache.Set(CacheKey, value, CacheFor);
    }

    private async Task<string?> ReadDbAsync(CancellationToken cancellationToken)
    {
        if (!_db.IsConfigured)
            return null;

        var rows = await _db.QueryAsync(
            "SELECT SETVALUE FROM SYNC_SETTING WHERE SETKEY = @K",
            r => CentralDb.Str(r, "SETVALUE"),
            new { K = SettingKey },
            cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private static string Normalize(string? provider) =>
        SqlAccountingWriteProviders.IsSdk(provider)
            ? SqlAccountingWriteProviders.Sdk
            : SqlAccountingWriteProviders.Rest;
}
