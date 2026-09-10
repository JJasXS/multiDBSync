namespace SqlAccountSync.Helpers;

/// <summary>User-facing labels for internal sync codes (job kinds, transaction types).</summary>
public static class SyncDisplayNames
{
    private static readonly Dictionary<string, string> JobKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MASTER"] = "Master data",
        ["MASTER_DRYRUN"] = "Master data",
        ["MASTER_UPDATE"] = "Account update",
        ["MASTER_DELETE"] = "Account delete",
        ["TXN"] = "Document sync",
        ["TXN_DRYRUN"] = "Document sync",
    };

    private static readonly Dictionary<string, string> TxnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SI_TO_PI"] = "Sales → Purchase",
        ["DO_TO_GR"] = "Delivery → Goods received",
        ["ST_TO_ST"] = "Stock transfer",
        ["SR_TO_PR"] = "Sales return → Purchase return",
        ["CN_TO_PR"] = "Credit note → Purchase return",
        ["CN_TO_DN"] = "Credit note → Debit note",
        ["DN_TO_CN"] = "Debit note → Credit note",
    };

    public static string JobKind(string? jobKind, bool dryRun = false)
    {
        var code = (jobKind ?? "").Trim();
        if (string.IsNullOrEmpty(code))
            return "—";

        var label = JobKinds.TryGetValue(code, out var friendly) ? friendly : code;
        return dryRun ? $"{label} (dry run)" : label;
    }

    public static string TxnType(string? txnTypeCode, string? displayNameFromDb = null)
    {
        var code = (txnTypeCode ?? "").Trim();
        if (string.IsNullOrEmpty(code))
            return "—";

        if (TxnTypes.TryGetValue(code, out var friendly))
            return friendly;

        var fromDb = (displayNameFromDb ?? "").Trim();
        return string.IsNullOrEmpty(fromDb) ? code : fromDb;
    }

    public static string AuditAction(string? action)
    {
        var code = (action ?? "").Trim();
        if (code.Length == 0)
            return "—";

        return code switch
        {
            "ENQUEUE_MASTER" => "Push queued",
            "DRY_RUN_MASTER" => "Push dry run queued",
            "MASTER_SYNC_DONE" => "Push completed",
            "MASTER_DRYRUN_DONE" => "Push dry run completed",
            "ENQUEUE_MASTER_UPDATE" => "Account update queued",
            "MASTER_UPDATE_DONE" => "Account update completed",
            "ENQUEUE_MASTER_DELETE" => "Account delete queued",
            "MASTER_DELETE_DONE" => "Account delete completed",
            "TXN_ENQUEUE" => "Transfer queued",
            "TXN_DRYRUN" => "Transfer dry run queued",
            "TXN_RETRY" => "Transfer retry queued",
            "TXN_RETRY_REPAIRED" => "Transfer repaired",
            "USER EXECUTED SYNC" => "Sync queued",
            _ => code.Replace("_", " ")
        };
    }

    public static string AuditResultBadgeClass(string? result)
    {
        var status = (result ?? "").Trim().ToLowerInvariant();
        return status switch
        {
            "success" or "completed" => "badge-completed",
            "failed" or "cancelled" or "conflict" => "badge-failed",
            "retrying" or "processing" => "badge-processing",
            "pending" or "queued" or "waiting_for_mapping" => "badge-pending",
            _ => "badge-sub"
        };
    }
}
