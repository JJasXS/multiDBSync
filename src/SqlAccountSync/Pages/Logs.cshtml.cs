using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private const int PageSize = 40;
    private readonly ITransactionEngine _engine;
    private readonly IAuditService _audit;
    private readonly IErrorLogService _errors;

    public LogsModel(
        ITransactionEngine engine,
        IAuditService audit,
        IErrorLogService errors)
    {
        _engine = engine;
        _audit = audit;
        _errors = errors;
    }

    public List<SyncTransaction> Transactions { get; set; } = [];
    public List<AuditListItem> Audits { get; set; } = [];
    public List<ErrorEntry> Errors { get; set; } = [];
    public List<LogRow> Rows { get; set; } = [];
    public bool HasMore { get; set; }
    public bool ShowActions { get; set; }

    public async Task<IActionResult> OnGetAsync(string? tab)
    {
        var t = (tab ?? "").Trim().ToLowerInvariant();
        if (t is "transactions" or "transfer" or "docs" or "documents")
            return RedirectToPage("/Transactions/Index");
        if (t is "errors")
            return Redirect("/logs#error-logs");
        if (t is "audit")
            return Redirect("/logs#audit-logs");

        var page = await LoadPageAsync("all", null, null, 0);
        Rows = page.Rows;
        HasMore = page.HasMore;
        ShowActions = CanOperate || CanManage || Rows.Any(r => !string.IsNullOrWhiteSpace(r.LinkUrl));
        return Page();
    }

    public async Task<IActionResult> OnGetMoreAsync(string? kind, string? before, string? beforeKind, int beforeId, int actions)
    {
        var page = await LoadPageAsync(kind, ParseCursor(before), beforeKind, beforeId);
        Response.Headers["X-Has-More"] = page.HasMore ? "1" : "0";
        return Partial("_LogRows", new LogTableBody { Rows = page.Rows, ShowActions = actions == 1 });
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResolveErrorAsync(int id)
    {
        if (!CanManage) return Forbid();
        await _errors.ResolveAsync(id);
        return Redirect("/logs#error-logs");
    }

    private async Task<(List<LogRow> Rows, bool HasMore)> LoadPageAsync(
        string? kind, DateTime? before, string? beforeKind, int beforeId)
    {
        var filter = NormalizeKind(kind);
        var txns = filter is "all" or "transaction"
            ? await _engine.ListPageAsync(before, beforeKind, beforeId, PageSize)
            : [];
        var audits = filter is "all" or "audit"
            ? await _audit.ListDetailedPageAsync(before, beforeKind, beforeId, PageSize)
            : [];
        var errors = filter is "all" or "error"
            ? await _errors.ListPageAsync(before, beforeKind, beforeId, PageSize)
            : [];

        Transactions = txns;
        Audits = audits;
        Errors = errors;
        var ordered = BuildRows();
        var page = ordered.Take(PageSize).ToList();
        var hasMore = ordered.Count > PageSize
            || txns.Count == PageSize
            || audits.Count == PageSize
            || errors.Count == PageSize;
        return (page, hasMore);
    }

    private static string NormalizeKind(string? kind)
    {
        var value = (kind ?? "").Trim().ToLowerInvariant();
        return value is "transaction" or "audit" or "error" ? value : "all";
    }

    private static DateTime? ParseCursor(string? before)
    {
        if (string.IsNullOrWhiteSpace(before))
            return null;
        return DateTime.TryParse(before, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
    }

    private List<LogRow> BuildRows()
    {
        var rows = new List<LogRow>();
        rows.AddRange(Transactions.Select(ToTransactionRow));
        rows.AddRange(Audits.Select(ToAuditRow));
        rows.AddRange(Errors.Select(ToErrorRow));
        return rows.OrderByDescending(r => r.At).ThenByDescending(r => r.SourceId).ToList();
    }

    private LogRow ToTransactionRow(SyncTransaction txn)
    {
        var type = SyncDisplayNames.TxnType(txn.TxnTypeCode);
        var src = $"{txn.SourceName} {txn.SrcDocType} {txn.SrcDocNo}".Trim();
        var dst = $"{txn.DestName} {txn.DstDocType} {txn.DstDocNo}".Trim();
        return new LogRow
        {
            At = txn.UpdatedAt.Year > 2000 ? txn.UpdatedAt : txn.CreatedAt,
            Kind = "transaction",
            KindLabel = "Transaction",
            Detail = $"{type} · {src} → {dst}",
            Status = txn.Status,
            BadgeClass = "badge-" + txn.Status.ToLowerInvariant(),
            LinkUrl = $"/Transactions/Details/{txn.TxnPk}",
            LinkLabel = "Open",
            SourceId = txn.TxnPk,
            RetryTxnPk = CanOperate && txn.Status is not "COMPLETED" ? txn.TxnPk : null
        };
    }

    private LogRow ToAuditRow(AuditListItem row)
    {
        var entry = row.Entry;
        var entity = string.IsNullOrWhiteSpace(entry.Entity) ? "" : SyncDisplayNames.TxnType(entry.Entity);
        var who = string.IsNullOrWhiteSpace(row.DestName) ? entry.UserEmail : row.DestName;
        var parts = new[] { SyncDisplayNames.AuditAction(entry.Action), entity, entry.RecordId, who }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var hideJob = !CanOperate && row.LinkLabel == "View job";
        return new LogRow
        {
            At = entry.AtUtc,
            Kind = "audit",
            KindLabel = "Audit",
            Detail = string.Join(" · ", parts),
            Status = row.DisplayResult,
            BadgeClass = SyncDisplayNames.AuditResultBadgeClass(row.DisplayResult),
            StatusTitle = row.StatusDetail,
            SourceId = entry.AuditId,
            LinkUrl = hideJob ? null : row.LinkUrl,
            LinkLabel = hideJob ? "" : row.LinkLabel
        };
    }

    private LogRow ToErrorRow(ErrorEntry error) => new()
    {
        At = error.AtUtc,
        Kind = "error",
        KindLabel = "Error",
        Detail = $"{error.ErrorType} · {error.UserMessage}".Trim(' ', '·'),
        Status = error.Resolved ? "Resolved" : "Open",
        BadgeClass = error.Resolved ? "badge-completed" : "badge-failed",
        SourceId = error.ErrorId,
        ResolveErrorId = CanManage && !error.Resolved ? error.ErrorId : null
    };
}

public sealed class LogRow
{
    public DateTime At { get; init; }
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; init; } = "";
    public string BadgeClass { get; init; } = "";
    public string? StatusTitle { get; init; }
    public string? LinkUrl { get; init; }
    public string LinkLabel { get; init; } = "";
    public int SourceId { get; init; }
    public int? RetryTxnPk { get; init; }
    public int? ResolveErrorId { get; init; }
}

public sealed class LogTableBody
{
    public List<LogRow> Rows { get; init; } = [];
    public bool ShowActions { get; init; }
}
