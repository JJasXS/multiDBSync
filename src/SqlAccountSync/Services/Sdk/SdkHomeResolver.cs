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
        CancellationToken cancellationToken = default)
    {
        var configured = (sync.SdkHomeDbCode ?? "").Trim();
        if (configured.Length > 0)
        {
            var creds = await bundle.ResolveAsync(configured, cancellationToken).ConfigureAwait(false);
            if (creds is not null)
                return configured;
        }

        var dbs = await registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var db in dbs.Where(d => d.IsActive && SyncTargetResolver.IsMasterOrBoth(d)))
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
}
