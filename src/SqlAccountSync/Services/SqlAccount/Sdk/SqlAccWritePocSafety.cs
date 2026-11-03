namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Write POC gates: authorized TEST company only + restorable-backup evidence before first write.
/// Backup evidence is either an on-disk archive path, or an explicit supervisor-managed confirmation
/// scoped to ACC-MIRACLE.FDB / ACC-ONYAT.FDB / ACC-STEPPES.FDB (never a silent bypass).
/// </summary>
public static class SqlAccWritePocSafety
{
    public const string PocCodePrefix = "SDKPOC";

    /// <summary>
    /// Exact confirmation token prefix. Full token must be
    /// <c>SUPERVISOR-MANAGED-RESTORABLE-BACKUP:ACC-STEPPES.FDB</c> (or MIRACLE / ONYAT).
    /// </summary>
    public const string SupervisorManagedBackupPrefix = "SUPERVISOR-MANAGED-RESTORABLE-BACKUP:";

    public static string NewUniqueAgentCode() =>
        PocCodePrefix + DateTime.UtcNow.ToString("yyMMddHHmmss") + Random.Shared.Next(10, 99);

    public static string BuildSupervisorManagedBackupConfirm(string databaseFileName) =>
        SupervisorManagedBackupPrefix + (databaseFileName ?? "").Trim();

    /// <summary>
    /// Accepts either <paramref name="backupPath"/> to an existing restorable archive,
    /// or <paramref name="supervisorManagedBackupConfirm"/> matching the target FDB for
    /// supervisor-authorized project companies only.
    /// </summary>
    public static void EnsureRestorableBackup(
        string? databaseFileName,
        string? backupPath,
        string? supervisorManagedBackupConfirm = null)
    {
        var fdb = (databaseFileName ?? "").Trim();
        if (fdb.Length == 0)
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: database file name is required for backup gate.");

        if (SqlAccProjectTargets.IsForbiddenDatabaseFileName(fdb))
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: ACC-UAMAT.FDB is not an authorized project write target.");

        var confirm = (supervisorManagedBackupConfirm ?? "").Trim();
        if (confirm.Length > 0)
        {
            EnsureSupervisorManagedBackupConfirm(fdb, confirm);
            return;
        }

        if (string.IsNullOrWhiteSpace(backupPath))
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: no restorable TEST backup evidence. " +
                "Pass --backup-path to an existing .fbk/.zip/.7z/.bak, " +
                "or pass --confirm-supervisor-backup \"" + BuildSupervisorManagedBackupConfirm(fdb) + "\" " +
                "after the supervisor confirms restorable backups for this authorized TEST company " +
                "(or set SQLACC_SDK_CONFIRM_SUPERVISOR_BACKUP to that exact token).");

        var path = backupPath.Trim();
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: backup file not found: " + path);

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (ext.Length == 0 ||
            !(ext.Equals(".fbk", StringComparison.OrdinalIgnoreCase)
              || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
              || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase)
              || ext.Equals(".bak", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: backup path must be a restorable archive (.fbk/.zip/.7z/.bak). Found: " + name);
        }
    }

    public static void EnsureSupervisorManagedBackupConfirm(string databaseFileName, string confirmToken)
    {
        var fdb = (databaseFileName ?? "").Trim();
        if (!IsAuthorizedProjectWriteDatabase(fdb))
        {
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: supervisor-managed backup confirmation is only allowed for " +
                SqlAccProjectTargets.MiracleFdb + ", " + SqlAccProjectTargets.OnyatFdb +
                ", or " + SqlAccProjectTargets.SteppesFdb +
                ". Refused FDB='" + fdb + "'.");
        }

        var expected = BuildSupervisorManagedBackupConfirm(fdb);
        if (!string.Equals(confirmToken.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WRITE SAFETY STOP: supervisor-managed backup confirmation token does not match the target FDB. " +
                "Expected exact token '" + expected + "'.");
        }
    }

    public static bool IsAuthorizedProjectWriteDatabase(string? databaseFileName) =>
        SqlAccProjectTargets.IsAuthorizedProjectDatabaseFileName(databaseFileName);

    public static bool LooksLikePocAgentCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && code.Trim().StartsWith(PocCodePrefix, StringComparison.OrdinalIgnoreCase);
}
