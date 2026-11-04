using SqlAccountSync.Models;
using SqlAccountSync.Options;

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
        IReadOnlyList<DatabaseLink> links,
        string? writeProvider = null)
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
                        && IsWriteReady(d, writeProvider))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Active linked subs that are missing write credentials for the current mode.</summary>
    public static List<RegisteredDatabase> ResolveSkippedMissingApi(
        RegisteredDatabase source,
        IReadOnlyList<RegisteredDatabase> databases,
        IReadOnlyList<DatabaseLink> links,
        string? writeProvider = null)
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
                        && !IsWriteReady(d, writeProvider))
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasApiCredentials(RegisteredDatabase db) =>
        !string.IsNullOrWhiteSpace(db.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(db.ApiCredRef);

    /// <summary>
    /// REST needs API base + cred ref. SDK only needs an active company code
    /// (login details live in sdkConnections).
    /// </summary>
    public static bool IsWriteReady(RegisteredDatabase db, string? writeProvider) =>
        SqlAccountingWriteProviders.IsSdk(writeProvider) || HasApiCredentials(db);

    public static string SkippedTargetsHint(string? writeProvider) =>
        SqlAccountingWriteProviders.IsSdk(writeProvider)
            ? "Skipped: add the company Code to AWS sdkConnections (User / Pass / DCF / Database)."
            : "Skipped: missing SQL Account API access key / secret key.";
}
