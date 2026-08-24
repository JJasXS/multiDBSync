namespace SqlAccountSync.Models;

/// <summary>Entities exposed in Push / Mappings UI (master-data sync).</summary>
public static class PushableEntityCodes
{
    public static readonly string[] All = [EntityCodes.Item, EntityCodes.Customer, EntityCodes.Supplier];

    public static bool IsPushable(string? entityCode)
    {
        var e = (entityCode ?? "").Trim().ToUpperInvariant();
        return e is EntityCodes.Item or EntityCodes.Customer or EntityCodes.Supplier;
    }

    public static string Normalize(string? entityCode, string fallback = EntityCodes.Item) =>
        IsPushable(entityCode) ? entityCode!.Trim().ToUpperInvariant() : fallback;
}
