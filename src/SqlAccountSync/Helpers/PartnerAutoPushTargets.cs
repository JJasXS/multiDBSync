using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Resolves linked sub databases that can receive an automatic customer/supplier push
/// after a create on a master (or BOTH) source.
/// Write readiness depends on <see cref="SqlAccountingWriteOptions.Provider"/>:
/// Rest needs API credentials; Sdk needs Firebird path (COM Login uses FbPath file name + DCF).
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

    /// <summary>
    /// Active linked subs that are not write-ready for the configured provider
    /// (missing API credentials in Rest mode, or missing FbPath in Sdk mode).
    /// </summary>
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

    /// <summary>REST paid-API credentials (api.sql.my + ApiCredRef). Not required when Provider=Sdk.</summary>
    public static bool HasApiCredentials(RegisteredDatabase db) =>
        !string.IsNullOrWhiteSpace(db.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(db.ApiCredRef);

    /// <summary>SDK company target: SYNC_DATABASE.FbPath file name used as Login FDB parameter.</summary>
    public static bool HasSdkTarget(RegisteredDatabase db) =>
        !string.IsNullOrWhiteSpace(db.FbPath);

    public static bool IsWriteReady(RegisteredDatabase db, string? writeProvider) =>
        SqlAccountingWriteProviders.IsSdk(writeProvider)
            ? HasSdkTarget(db)
            : HasApiCredentials(db);

    public static string DescribeMissingWriteConfig(RegisteredDatabase db, string? writeProvider)
    {
        if (IsWriteReady(db, writeProvider))
            return "";

        if (SqlAccountingWriteProviders.IsSdk(writeProvider))
        {
            return db.DisplayName +
                   " has no Firebird path (FbPath) configured for SDK Login.";
        }

        return db.DisplayName +
               " has no SQL Accounting API credentials configured.";
    }

    public static string SkippedTargetsHint(string? writeProvider) =>
        SqlAccountingWriteProviders.IsSdk(writeProvider)
            ? "Skipped (no Firebird path for SDK):"
            : "Skipped (no API credentials):";
}
