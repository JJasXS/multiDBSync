using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages;

public class LogsModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;

    public LogsModel(ITransactionEngine engine, IDatabaseRegistry registry, ISqlAccountReadAdapter read)
    {
        _engine = engine;
        _registry = registry;
        _read = read;
    }

    public List<SyncTransaction> Transactions { get; set; } = [];
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<RegisteredDatabase> TargetDatabases { get; set; } = [];
    public List<(string Code, string Name, string Src, string Dst, string DstPath)> Types { get; set; } = [];
    public List<DocumentListItem> Documents { get; set; } = [];
    public DryRunResult? DryRun { get; set; }
    public FlashToastModel? FlashToast { get; set; }

    [BindProperty(SupportsGet = true)] public int SourceDbId { get; set; }
    [BindProperty(SupportsGet = true)] public string TxnType { get; set; } = "SI_TO_PI";
    [BindProperty] public List<string> SelectedDocs { get; set; } = [];
    [BindProperty] public List<int> SelectedDestDbIds { get; set; } = [];
    [BindProperty] public bool Confirmed { get; set; }

    public async Task<IActionResult> OnGetAsync(string? tab)
    {
        var t = (tab ?? "").Trim().ToLowerInvariant();
        if (t is "errors")
            return RedirectToPage("/Settings/Errors");
        if (t is "audit")
            return RedirectToPage("/Settings/Audit");

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        await LoadAsync();

        var doc = FirstSelectedDoc();
        var destId = FirstSelectedDest();
        if (doc is null || destId <= 0)
        {
            FlashToast = new FlashToastModel { Title = "Select at least one document and one target database.", Ok = false };
            return Page();
        }

        DryRun = await _engine.DryRunAsync(SourceDbId, destId, TxnType, doc);
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!CanOperate) return Forbid();
        if (!Confirmed)
        {
            await LoadAsync();
            FlashToast = new FlashToastModel { Title = "Confirmation is required before creating accounting documents.", Ok = false };
            return Page();
        }

        await LoadAsync();
        var docs = SelectedDocs
            .Select(d => (d ?? "").Trim())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dests = SelectedDestDbIds.Where(id => id > 0 && id != SourceDbId).Distinct().ToList();

        if (docs.Count == 0 || dests.Count == 0)
        {
            FlashToast = new FlashToastModel { Title = "Select at least one document and one target database.", Ok = false };
            return Page();
        }

        var messages = new List<string>();
        var queued = 0;
        var failed = 0;
        var dbById = Databases.ToDictionary(d => d.DbId);

        foreach (var doc in docs)
        {
            foreach (var destId in dests)
            {
                try
                {
                    var (_, txnId, msg) = await _engine.EnqueueAsync(
                        SourceDbId, destId, TxnType, doc, dryRun: false, CurrentEmail, Confirmed);
                    if (!string.IsNullOrEmpty(txnId))
                    {
                        queued++;
                        var destLabel = dbById.TryGetValue(destId, out var db) ? db.DisplayName : $"database {destId}";
                        messages.Add($"{doc} to {destLabel} ({txnId})");
                    }
                    else
                    {
                        failed++;
                        messages.Add($"{doc}: {msg}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    messages.Add($"{doc}: {FirebirdErrorMapper.Describe(ex)}");
                }
            }
        }

        if (queued == 0 && failed == 0)
        {
            FlashToast = new FlashToastModel { Title = "Nothing queued.", Ok = false };
        }
        else
        {
            var lines = messages;
            if (lines.Count > 12)
                lines = [.. lines.Take(12), $"+{messages.Count - 12} more"];

            FlashToast = new FlashToastModel
            {
                Title = $"Queued {queued} transaction{(queued == 1 ? "" : "s")}."
                    + (failed > 0 ? $" {failed} could not be queued." : ""),
                Lines = lines,
                Ok = queued > 0 && failed == 0
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(int txnPk)
    {
        if (!CanOperate) return Forbid();
        await _engine.RetryAsync(txnPk, CurrentEmail);
        return RedirectToPage();
    }

    private string? FirstSelectedDoc() =>
        SelectedDocs.Select(d => (d ?? "").Trim()).FirstOrDefault(d => d.Length > 0);

    private int FirstSelectedDest() =>
        SelectedDestDbIds.FirstOrDefault(id => id > 0 && id != SourceDbId);

    private async Task LoadAsync()
    {
        Transactions = await _engine.ListAsync();
        Databases = await _registry.ListAsync();
        Types = await _engine.ListTypesAsync();

        if (SourceDbId == 0)
            SourceDbId = Databases.FirstOrDefault()?.DbId ?? 0;

        if (string.IsNullOrWhiteSpace(TxnType) || Types.All(t => t.Code != TxnType))
            TxnType = Types.FirstOrDefault().Code ?? "SI_TO_PI";

        TargetDatabases = Databases
            .Where(d => d.DbId != SourceDbId)
            .OrderBy(d => d.DisplayName)
            .ToList();

        Documents = [];
        if (SourceDbId <= 0)
            return;

        var source = await _registry.GetAsync(SourceDbId);
        if (source is null)
            return;

        var type = Types.FirstOrDefault(t => t.Code == TxnType);
        if (type.Code is null)
            return;

        try
        {
            Documents = await _read.ListDocumentsAsync(source, type.Src);
        }
        catch
        {
            Documents = [];
        }
    }
}
