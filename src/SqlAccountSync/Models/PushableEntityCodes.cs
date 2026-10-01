namespace SqlAccountSync.Models;

/// <summary>Entities exposed in Push / Mappings UI.</summary>
public static class PushableEntityCodes
{
    /// <summary>Multi-row push (/push). ACCOUNT sources are limited to Master or BOTH.</summary>
    public static readonly string[] Push = [EntityCodes.Customer, EntityCodes.Supplier, EntityCodes.Account];

    /// <summary>Mappings and other UIs that still list ACCOUNT.</summary>
    public static readonly string[] All = [EntityCodes.Customer, EntityCodes.Supplier, EntityCodes.Account];

    public static bool IsPushable(string? entityCode)
    {
        var e = (entityCode ?? "").Trim().ToUpperInvariant();
        return e is EntityCodes.Customer or EntityCodes.Supplier or EntityCodes.Account;
    }

    public static bool IsMappable(string? entityCode)
    {
        var e = (entityCode ?? "").Trim().ToUpperInvariant();
        return e is EntityCodes.Customer or EntityCodes.Supplier or EntityCodes.Account;
    }

    public static string Normalize(string? entityCode, string fallback = EntityCodes.Customer) =>
        IsMappable(entityCode) ? entityCode!.Trim().ToUpperInvariant() : fallback;

    public static string NormalizePush(string? entityCode, string fallback = EntityCodes.Customer) =>
        IsPushable(entityCode) ? entityCode!.Trim().ToUpperInvariant() : fallback;
}
