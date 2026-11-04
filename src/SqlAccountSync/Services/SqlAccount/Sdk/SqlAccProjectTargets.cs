namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// POC-only convenience aliases for supervisor-approved TEST companies.
/// Production SDK writes must use <see cref="SqlAccSessionManager"/> + SYNC_DATABASE FbPath
/// (never hardcode company count or these aliases into sync logic).
/// </summary>
public static class SqlAccProjectTargets
{
    public const string SharedDcf = @"\\192.168.100.201\Share\SQLACC.DCF";

    public const string MiracleFdb = "ACC-MIRACLE.FDB";
    public const string OnyatFdb = "ACC-ONYAT.FDB";
    public const string SteppesFdb = "ACC-STEPPES.FDB";

    /// <summary>Not part of this project — always refuse.</summary>
    public const string ForbiddenUamatFdb = "ACC-UAMAT.FDB";

    public const string MiracleDbCode = "MIRACLE";
    public const string OnyatDbCode = "ONYAT";
    public const string SteppesDbCode = "STEPPES";

    public static string MiracleTargetKey =>
        SqlAccReadPocSafety.NormalizeTarget(SharedDcf, MiracleFdb);

    public static string OnyatTargetKey =>
        SqlAccReadPocSafety.NormalizeTarget(SharedDcf, OnyatFdb);

    public static string SteppesTargetKey =>
        SqlAccReadPocSafety.NormalizeTarget(SharedDcf, SteppesFdb);

    /// <summary>Semicolon-separated allow-list for MIRACLE + ONYAT (legacy POC default).</summary>
    public static string AuthorizedBothCompanies =>
        MiracleTargetKey + ";" + OnyatTargetKey;

    /// <summary>Semicolon-separated allow-list for all supervisor-approved POC TEST companies.</summary>
    public static string AuthorizedProjectCompanies =>
        AuthorizedBothCompanies + ";" + SteppesTargetKey;

    public static bool IsForbiddenDatabaseFileName(string? databaseFileName) =>
        !string.IsNullOrWhiteSpace(databaseFileName)
        && string.Equals(databaseFileName.Trim(), ForbiddenUamatFdb, StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthorizedProjectDatabaseFileName(string? databaseFileName)
    {
        var fdb = (databaseFileName ?? "").Trim();
        return fdb.Equals(MiracleFdb, StringComparison.OrdinalIgnoreCase)
               || fdb.Equals(OnyatFdb, StringComparison.OrdinalIgnoreCase)
               || fdb.Equals(SteppesFdb, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolveCompanyAlias(string? alias, out string fdbFileName, out string dbCode)
    {
        fdbFileName = "";
        dbCode = "";
        var a = (alias ?? "").Trim();
        if (a.Length == 0) return false;

        if (a.Equals("miracle", StringComparison.OrdinalIgnoreCase)
            || a.Equals(MiracleDbCode, StringComparison.OrdinalIgnoreCase)
            || a.Equals(MiracleFdb, StringComparison.OrdinalIgnoreCase))
        {
            fdbFileName = MiracleFdb;
            dbCode = MiracleDbCode;
            return true;
        }

        if (a.Equals("onyat", StringComparison.OrdinalIgnoreCase)
            || a.Equals(OnyatDbCode, StringComparison.OrdinalIgnoreCase)
            || a.Equals(OnyatFdb, StringComparison.OrdinalIgnoreCase))
        {
            fdbFileName = OnyatFdb;
            dbCode = OnyatDbCode;
            return true;
        }

        if (a.Equals("steppes", StringComparison.OrdinalIgnoreCase)
            || a.Equals(SteppesDbCode, StringComparison.OrdinalIgnoreCase)
            || a.Equals(SteppesFdb, StringComparison.OrdinalIgnoreCase))
        {
            fdbFileName = SteppesFdb;
            dbCode = SteppesDbCode;
            return true;
        }

        return false;
    }
}
