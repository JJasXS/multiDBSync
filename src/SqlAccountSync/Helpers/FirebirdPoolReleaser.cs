using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Drops idle Firebird client pools for this process so monitor attachments
/// (e.g. SqlAccountSync.dll) disappear immediately after web logout.
/// </summary>
public static class FirebirdPoolReleaser
{
    public static void ClearAllPools(ILogger? logger = null)
    {
        try
        {
            FbConnection.ClearAllPools();
            logger?.LogInformation("Firebird connection pools cleared on logout");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear Firebird connection pools on logout");
        }
    }
}
