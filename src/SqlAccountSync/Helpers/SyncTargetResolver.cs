using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Resolves destination databases for a master-data push from a source company.</summary>
public static class SyncTargetResolver
{
    /// <summary>
    /// For MASTER/BOTH with active SYNC_DBLINK rows: linked subs only.
    /// Otherwise: other active DBs with role SUB, BOTH, or MASTER (excluding source).
    /// </summary>
    public static List<RegisteredDatabase> ResolveLinkedSubs(
        RegisteredDatabase? source,
        IReadOnlyList<RegisteredDatabase> databases,
        IReadOnlyList<DatabaseLink> links)
    {
        if (source is null || source.DbId <= 0)
            return [];

        var role = (source.DbRole ?? "").Trim().ToUpperInvariant();
        if (role is DatabaseRoles.Master or DatabaseRoles.Both)
        {
            var linkedSubIds = links
                .Where(l => l.IsActive && l.MasterDbId == source.DbId)
                .Select(l => l.SubDbId)
                .ToHashSet();
            if (linkedSubIds.Count > 0)
            {
                return databases
                    .Where(d => linkedSubIds.Contains(d.DbId))
                    .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return databases
            .Where(d => d.DbId != source.DbId && d.DbRole is "SUB" or "BOTH" or "MASTER")
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsMasterOrBoth(RegisteredDatabase? db)
    {
        var role = (db?.DbRole ?? "").Trim().ToUpperInvariant();
        return role is DatabaseRoles.Master or DatabaseRoles.Both;
    }
}
