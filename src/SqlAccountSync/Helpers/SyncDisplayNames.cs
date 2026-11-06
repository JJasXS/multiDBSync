namespace SqlAccountSync.Helpers;

/// <summary>User-facing labels for internal sync codes (job kinds, transaction types).</summary>
public static class SyncDisplayNames
{
    private static readonly Dictionary<string, string> JobKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MASTER"] = "Master data",
        ["MASTER_DRYRUN"] = "Master data",
        ["MASTER_UPDATE"] = "Master update",
        ["MASTER_DELETE"] = "Master delete",
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
            "ENQUEUE_MASTER" => "Queued",
            "DRY_RUN_MASTER" => "Dry run queued",
            "MASTER_SYNC_DONE" => "Pushed",
            "MASTER_DRYRUN_DONE" => "Dry run",
            "ENQUEUE_MASTER_UPDATE" => "Update queued",
            "MASTER_UPDATE_DONE" => "Updated",
            "ENQUEUE_MASTER_DELETE" => "Delete queued",
            "MASTER_DELETE_DONE" => "Deleted",
            "MASTER_FB_FALLBACK" => "Firebird fallback write",
            "TXN_ENQUEUE" => "Transfer queued",
            "TXN_DRYRUN" => "Transfer dry run queued",
            "TXN_RETRY" => "Transfer retry queued",
            "TXN_RETRY_REPAIRED" => "Transfer repaired",
            "USER EXECUTED SYNC" => "Sync queued",
            _ => code.Replace("_", " ")
        };
    }

    /// <summary>
    /// Logs detail: "CUSTOMER under MIRACLE of 25-000-A06" (status badge carries success/queued).
    /// </summary>
    public static string AuditDetail(string? action, string? entity, string? recordId, string? companyName)
    {
        var category = (entity ?? "").Trim();
        var code = (recordId ?? "").Trim();
        var company = (companyName ?? "").Trim();
        if (IsAutoPushUser(company))
            company = "";

        if (IsMasterAuditAction(action) && category.Length > 0 && code.Length > 0)
        {
            var verb = MasterAuditVerb(action);
            if (company.Length > 0)
                return verb.Length > 0
                    ? $"{category} under {company} {verb} of {code}"
                    : $"{category} under {company} of {code}";
            return verb.Length > 0
                ? $"{category} {verb} of {code}"
                : $"{category} of {code}";
        }

        var parts = new[] { AuditAction(action), category, code, company }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }

    private static bool IsAutoPushUser(string value) =>
        value.StartsWith("system:", StringComparison.OrdinalIgnoreCase);

    private static bool IsMasterAuditAction(string? action)
    {
        var code = (action ?? "").Trim();
        return code is "ENQUEUE_MASTER" or "DRY_RUN_MASTER" or "MASTER_SYNC_DONE" or "MASTER_DRYRUN_DONE"
            or "ENQUEUE_MASTER_UPDATE" or "MASTER_UPDATE_DONE"
            or "ENQUEUE_MASTER_DELETE" or "MASTER_DELETE_DONE";
    }

    /// <summary>Empty for plain create/push; "update"/"delete"/"queued" when useful.</summary>
    private static string MasterAuditVerb(string? action) =>
        (action ?? "").Trim() switch
        {
            "ENQUEUE_MASTER" or "DRY_RUN_MASTER" => "queued",
            "ENQUEUE_MASTER_UPDATE" => "update queued",
            "MASTER_UPDATE_DONE" => "update",
            "ENQUEUE_MASTER_DELETE" => "delete queued",
            "MASTER_DELETE_DONE" => "delete",
            _ => ""
        };

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
