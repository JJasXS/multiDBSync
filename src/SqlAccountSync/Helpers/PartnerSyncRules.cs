using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Resolves target entity type for master-data sync (same-type vs reciprocal party flip).</summary>
public static class PartnerSyncRules
{
    public static string ResolveTargetEntity(RegisteredDatabase? source, RegisteredDatabase? dest, string sourceEntity)
    {
        var srcEntity = (sourceEntity ?? "").Trim().ToUpperInvariant();
        if (!IsPartyEntity(srcEntity))
            return srcEntity;

        var srcRole = (source?.DbRole ?? "").Trim().ToUpperInvariant();
        var dstRole = (dest?.DbRole ?? "").Trim().ToUpperInvariant();

        if (srcRole is DatabaseRoles.Master or DatabaseRoles.Both)
            return srcEntity;

        if (srcRole is DatabaseRoles.Sub && dstRole is DatabaseRoles.Sub)
            return FlipParty(srcEntity);

        return srcEntity;
    }

    public static bool IsReciprocalPartySync(RegisteredDatabase? source, RegisteredDatabase? dest, string sourceEntity)
    {
        if (!IsPartyEntity(sourceEntity))
            return false;
        var srcRole = (source?.DbRole ?? "").Trim().ToUpperInvariant();
        var dstRole = (dest?.DbRole ?? "").Trim().ToUpperInvariant();
        return srcRole is DatabaseRoles.Sub && dstRole is DatabaseRoles.Sub;
    }

    public static bool IsPartyEntity(string entityCode) =>
        string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(entityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase);

    public static string FlipParty(string entityCode) =>
        string.Equals(entityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase) ? EntityCodes.Supplier
        : string.Equals(entityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase) ? EntityCodes.Customer
        : entityCode;
}
