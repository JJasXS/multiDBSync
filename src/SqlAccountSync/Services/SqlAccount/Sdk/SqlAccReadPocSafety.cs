using System.Net;
using System.Text.RegularExpressions;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Safety gate for SDK Read POC. Blocks last-used defaults and blocks LAN/UNC targets
/// unless the caller supplies an exact authorized target match.
/// </summary>
public static class SqlAccReadPocSafety
{
    private static readonly Regex Ipv4 = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const string MinimalMetadataSql =
        "SELECT COUNT(*) AS CNT FROM RDB$DATABASE";

    public static void EnsureExplicitLoginTarget(string? dcfPath, string? databaseFileName)
    {
        if (string.IsNullOrWhiteSpace(dcfPath))
            throw new InvalidOperationException(
                "SDK Read POC refused: DCF path is required. Last-used / registry defaults are not allowed.");
        if (string.IsNullOrWhiteSpace(databaseFileName))
            throw new InvalidOperationException(
                "SDK Read POC refused: database file name (FDB) is required. Last-used defaults are not allowed.");
        if (databaseFileName.Contains('\\') || databaseFileName.Contains('/'))
            throw new InvalidOperationException(
                "SDK Read POC refused: FDB parameter must be a file name without path (per SQLSDK Login docs).");
        if (SqlAccProjectTargets.IsForbiddenDatabaseFileName(databaseFileName))
            throw new InvalidOperationException(
                "SDK POC refused: ACC-UAMAT.FDB is not an authorized project database.");
    }

    public static bool IsLanOrUncTarget(string dcfPath)
    {
        var dcf = (dcfPath ?? "").Trim();
        if (dcf.Length == 0) return false;
        if (dcf.StartsWith(@"\\", StringComparison.Ordinal) || dcf.Contains(@"\\", StringComparison.Ordinal))
            return true;
        if (dcf.Contains("file://", StringComparison.OrdinalIgnoreCase) && dcf.Contains('\\'))
            return true;
        var m = Ipv4.Match(dcf);
        if (!m.Success) return false;
        if (!IPAddress.TryParse(m.Value, out var ip)) return true;
        return !IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Authorizes live connect only when LAN/UNC is either absent, or explicitly authorized
    /// with an exact DCF|FDB match (no substring guessing).
    /// </summary>
    public static void EnsureLiveReadAuthorized(
        string dcfPath,
        string databaseFileName,
        bool authorizeLanRead,
        string? authorizedExactTarget)
    {
        EnsureExplicitLoginTarget(dcfPath, databaseFileName);
        var key = NormalizeTarget(dcfPath, databaseFileName);

        if (!IsLanOrUncTarget(dcfPath))
            return;

        if (!authorizeLanRead)
            throw new InvalidOperationException(
                "SDK Read POC safety gate: target DCF looks like LAN/UNC. " +
                "Set AuthorizeLanRead=true and AuthorizedExactTarget to the exact 'DCF|FDB' pair only after human authorization. " +
                $"Refused target key '{key}'.");

        var expected = (authorizedExactTarget ?? "").Trim();
        if (expected.Length == 0)
            throw new InvalidOperationException(
                "SDK Read POC safety gate: AuthorizeLanRead is true but AuthorizedExactTarget is empty.");

        if (!IsAuthorizedTargetMatch(key, expected))
            throw new InvalidOperationException(
                "SDK Read POC safety gate: AuthorizedExactTarget does not match the requested DCF|FDB pair. " +
                "Provide exact 'DCF|FDB' or a semicolon-separated allow-list of such pairs.");
    }

    /// <summary>
    /// <paramref name="authorizedExactTarget"/> may be one <c>DCF|FDB</c> or several separated by <c>;</c>.
    /// </summary>
    public static bool IsAuthorizedTargetMatch(string normalizedRequestKey, string authorizedExactTarget)
    {
        foreach (var part in authorizedExactTarget.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(NormalizeTargetKey(part), normalizedRequestKey, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string NormalizeTarget(string dcfPath, string databaseFileName) =>
        NormalizeTargetKey(dcfPath.Trim() + "|" + databaseFileName.Trim());

    private static string NormalizeTargetKey(string key) =>
        key.Replace('/', '\\').Trim();
}
