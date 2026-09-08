using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Resolves linked sub databases that can receive an automatic customer/supplier push
/// after a create on a master (or BOTH) source.
/// </summary>
public static class PartnerAutoPushTargets
{
    public static List<RegisteredDatabase> Resolve(
        RegisteredDatabase source,
        IReadOnlyList<RegisteredDatabase> databases,
        IReadOnlyList<DatabaseLink> links)
    {
        var role = (source.DbRole ?? "").Trim().ToUpperInvariant();
        if (role is not (DatabaseRoles.Master or DatabaseRoles.Both))
            return [];

        var linkedSubIds = links
            .Where(l => l.IsActive && l.MasterDbId == source.DbId)
            .Select(l => l.SubDbId)
            .ToHashSet();

        if (linkedSubIds.Count == 0)
            return [];

        return databases
            .Where(d => d.IsActive
                        && linkedSubIds.Contains(d.DbId)
                        && HasApiCredentials(d))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Active linked subs that are missing SQL Acc API credentials (for UI warnings).</summary>
    public static List<RegisteredDatabase> ResolveSkippedMissingApi(
        RegisteredDatabase source,
        IReadOnlyList<RegisteredDatabase> databases,
        IReadOnlyList<DatabaseLink> links)
    {
        var role = (source.DbRole ?? "").Trim().ToUpperInvariant();
        if (role is not (DatabaseRoles.Master or DatabaseRoles.Both))
            return [];

        var linkedSubIds = links
            .Where(l => l.IsActive && l.MasterDbId == source.DbId)
            .Select(l => l.SubDbId)
            .ToHashSet();

        if (linkedSubIds.Count == 0)
            return [];

        return databases
            .Where(d => d.IsActive
                        && linkedSubIds.Contains(d.DbId)
                        && !HasApiCredentials(d))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasApiCredentials(RegisteredDatabase db) =>
        !string.IsNullOrWhiteSpace(db.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(db.ApiCredRef);
}
