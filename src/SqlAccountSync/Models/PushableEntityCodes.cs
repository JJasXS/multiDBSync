namespace SqlAccountSync.Models;

/// <summary>Entities exposed in Push / Mappings UI (customer and supplier only).</summary>
public static class PushableEntityCodes
{
    public static readonly string[] All = [EntityCodes.Customer, EntityCodes.Supplier];

    public static bool IsPushable(string? entityCode)
    {
        var e = (entityCode ?? "").Trim().ToUpperInvariant();
        return e is EntityCodes.Customer or EntityCodes.Supplier;
    }

    public static string Normalize(string? entityCode, string fallback = EntityCodes.Customer) =>
        IsPushable(entityCode) ? entityCode!.Trim().ToUpperInvariant() : fallback;
}
