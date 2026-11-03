using SqlAccountSync.Helpers;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Sdk;

/// <summary>Picks which company COM should sit on when the sync queue is idle.</summary>
public static class SdkHomeResolver
{
    public static async Task<string> ResolveAsync(
        IDatabaseRegistry registry,
        ISdkConnectionBundle bundle,
        SyncOptions sync,
        CancellationToken cancellationToken = default,
        string? preferredDbCode = null)
    {
        foreach (var candidate in Candidates(sync, preferredDbCode))
        {
            var creds = await bundle.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (creds is not null)
                return candidate;
        }

        var dbs = (await registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(d => d.IsActive && SyncTargetResolver.IsMasterOrBoth(d))
            .OrderBy(d => HomeRank(d.DbCode, d.DisplayName, d.CompanyName))
            .ThenBy(d => d.DbCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var db in dbs)
        {
            var code = (db.DbCode ?? "").Trim();
            if (code.Length == 0)
                continue;
            var creds = await bundle.ResolveAsync(code, cancellationToken).ConfigureAwait(false);
            if (creds is not null)
                return code;
        }

        return "";
    }

    private static IEnumerable<string> Candidates(SyncOptions sync, string? preferredDbCode)
    {
        var preferred = (preferredDbCode ?? "").Trim();
        if (preferred.Length > 0)
            yield return preferred;

        var configured = (sync.SdkHomeDbCode ?? "").Trim();
        if (configured.Length > 0)
            yield return configured;

        // Common Main company codes used by this tenant.
        foreach (var code in new[] { "MAINTEST", "MAIN", "HQ" })
            yield return code;
    }

    private static int HomeRank(string? dbCode, string? displayName, string? companyName)
    {
        var blob = $"{dbCode} {displayName} {companyName}".ToUpperInvariant();
        if (blob.Contains("MAIN COMPANY", StringComparison.Ordinal)) return 0;
        if (blob.Contains("MAINTEST", StringComparison.Ordinal)) return 1;
        if (blob.Contains("MAIN", StringComparison.Ordinal)) return 2;
        if (blob.Contains("HQ", StringComparison.Ordinal) || blob.Contains("HEAD", StringComparison.Ordinal))
            return 3;
        return 10;
    }
}
