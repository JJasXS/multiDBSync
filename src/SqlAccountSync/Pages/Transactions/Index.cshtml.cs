using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Transactions;

namespace SqlAccountSync.Pages.Transactions;

public class IndexModel : SyncPageModel
{
    private readonly ITransactionEngine _engine;
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _read;

    public IndexModel(
        ITransactionEngine engine,
        IDatabaseRegistry registry,
        ISqlAccountReadAdapter read)
    {
        _engine = engine;
        _registry = registry;
        _read = read;
    }

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
    /// <summary>Keys are "{docId}|{seq}".</summary>
    [BindProperty] public Dictionary<string, decimal> QtyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnGetLinesAsync(string docId)
    {
        if (!CanOperate) return Forbid();
        await LoadAsync();

        var key = (docId ?? "").Trim();
        if (key.Length == 0 || SourceDbId <= 0)
            return new JsonResult(new { ok = false, message = "Document is required." });

        var source = await _registry.GetAsync(SourceDbId);
        if (source is null)
            return new JsonResult(new { ok = false, message = "Source database was not found." });

        var type = Types.FirstOrDefault(t => t.Code == TxnType);
        if (type.Code is null)
            return new JsonResult(new { ok = false, message = "Unknown transaction type." });

        try
        {
            var doc = await _read.GetDocumentAsync(source, type.Src, key);
            if (doc is null)
                return new JsonResult(new { ok = false, message = "Source document was not found." });

            return new JsonResult(new
            {
                ok = true,
                docId = string.IsNullOrWhiteSpace(doc.DocNo) ? doc.DocKey : doc.DocNo,
                lines = doc.Lines.Select(l => new
                {
                    seq = l.Seq,
                    itemCode = l.ItemCode,
                    description = l.Description,
                    uom = l.Uom,
                    qty = l.Qty,
                    fromLocation = l.FromLocation,
                    toLocation = l.ToLocation
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = FirebirdErrorMapper.Describe(ex) });
        }
    }

    public async Task<IActionResult> OnPostDryRunAsync()
    {
        if (!CanOperate) return Forbid();
        await LoadAsync();

        var doc = FirstSelectedDoc();
        var destId = FirstSelectedDest();
        if (doc is null || destId <= 0)
        {
            FlashToast = FlashToasts.Fail("Select at least one document and one target database.");
            return Page();
        }

        DryRun = await _engine.DryRunAsync(SourceDbId, destId, TxnType, doc, QtyForDoc(doc));
        return Page();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!CanOperate) return Forbid();
        if (!Confirmed)
        {
            await LoadAsync();
            FlashToast = FlashToasts.Fail("Confirmation is required before creating accounting documents.");
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
            FlashToast = FlashToasts.Fail("Select at least one document and one target database.");
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
                        SourceDbId, destId, TxnType, doc, dryRun: false, CurrentEmail, Confirmed, QtyForDoc(doc));
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

        FlashToast = FlashToasts.Queue(queued, failed, messages, "transaction");
        return Page();
    }

    private Dictionary<int, decimal>? QtyForDoc(string docId)
    {
        var prefix = docId + "|";
        var map = new Dictionary<int, decimal>();
        foreach (var (key, qty) in QtyOverrides)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var seqPart = key[prefix.Length..];
            if (!int.TryParse(seqPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                continue;
            map[seq] = qty;
        }
        return map.Count == 0 ? null : map;
    }

    private string? FirstSelectedDoc() =>
        SelectedDocs.Select(d => (d ?? "").Trim()).FirstOrDefault(d => d.Length > 0);

    private int FirstSelectedDest() =>
        SelectedDestDbIds.FirstOrDefault(id => id > 0 && id != SourceDbId);

    private async Task LoadAsync()
    {
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
