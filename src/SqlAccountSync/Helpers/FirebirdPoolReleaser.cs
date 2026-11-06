using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Drops idle Firebird client pools for this process so monitor attachments
/// (e.g. SqlAccountSync.dll) disappear immediately after web logout.
/// </summary>
public static class FirebirdPoolReleaser
{
    /// <returns>True when pools were cleared; false when clear threw.</returns>
    public static bool ClearAllPools(ILogger? logger = null)
    {
        try
        {
            FbConnection.ClearAllPools();
            logger?.LogInformation("Firebird connection pools cleared on logout");
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear Firebird connection pools on logout");
            return false;
        }
    }
}
