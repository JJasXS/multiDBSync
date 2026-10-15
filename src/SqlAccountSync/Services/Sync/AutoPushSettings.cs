using SqlAccountSync.Data;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Sync;

public sealed class AutoPushFlags
{
    public bool Customer { get; init; }
    public bool Supplier { get; init; }
    public bool Account { get; init; }

    public bool Any => Customer || Supplier || Account;

    public IReadOnlyList<string> Entities()
    {
        var list = new List<string>(3);
        if (Customer) list.Add(EntityCodes.Customer);
        if (Supplier) list.Add(EntityCodes.Supplier);
        if (Account) list.Add(EntityCodes.Account);
        return list;
    }
}

public interface IAutoPushSettings
{
    Task<AutoPushFlags> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AutoPushFlags flags, CancellationToken cancellationToken = default);
}

public sealed class AutoPushSettings : IAutoPushSettings
{
    public const string CustomerKey = "AUTO_PUSH_CUSTOMER";
    public const string SupplierKey = "AUTO_PUSH_SUPPLIER";
    public const string AccountKey = "AUTO_PUSH_ACCOUNT";

    private readonly CentralDb _db;

    public AutoPushSettings(CentralDb db) => _db = db;

    public async Task<AutoPushFlags> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return new AutoPushFlags();

        var rows = await _db.QueryAsync(
            """
            SELECT SETKEY, SETVALUE FROM SYNC_SETTING
            WHERE SETKEY IN (@C, @S, @A)
            """,
            r => (CentralDb.Str(r, "SETKEY"), CentralDb.Str(r, "SETVALUE")),
            new { C = CustomerKey, S = SupplierKey, A = AccountKey },
            cancellationToken).ConfigureAwait(false);

        var on = rows
            .Where(row => IsOn(row.Item2))
            .Select(row => row.Item1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AutoPushFlags
        {
            Customer = on.Contains(CustomerKey),
            Supplier = on.Contains(SupplierKey),
            Account = on.Contains(AccountKey)
        };
    }

    public async Task SaveAsync(AutoPushFlags flags, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            throw new InvalidOperationException("Central database is not configured.");

        await UpsertAsync(CustomerKey, flags.Customer, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(SupplierKey, flags.Supplier, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(AccountKey, flags.Account, cancellationToken).ConfigureAwait(false);
    }

    private Task UpsertAsync(string key, bool on, CancellationToken cancellationToken) =>
        _db.ExecuteAsync(
            """
            UPDATE OR INSERT INTO SYNC_SETTING (SETKEY, SETVALUE, UPDATEDAT)
            VALUES (@SETKEY, @SETVALUE, CURRENT_TIMESTAMP)
            MATCHING (SETKEY)
            """,
            new { SETKEY = key, SETVALUE = on ? "1" : "0" },
            cancellationToken);

    private static bool IsOn(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
