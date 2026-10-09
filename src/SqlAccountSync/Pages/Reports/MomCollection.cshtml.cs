using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Reports;

public class MomCollectionModel : SyncPageModel
{
    private const int CompanyBatchSize = 3;
    private static readonly SemaphoreSlim CollectionLoad = new(1, 1);

    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _companyRead;

    public MomCollectionModel(IDatabaseRegistry registry, ISqlAccountReadAdapter companyRead)
    {
        _registry = registry;
        _companyRead = companyRead;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public bool CentralConfigured { get; set; }
    public List<DashboardDetailRow> GlDetails { get; set; } = [];
    public DashboardDetailsModel Details { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public int? Year { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? DateFrom { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? DateTo { get; set; }
    [BindProperty(SupportsGet = true)] public string Customer { get; set; } = "";
    [BindProperty(SupportsGet = true)] public List<string> Db { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool DbFilter { get; set; }

    public async Task OnGetAsync()
    {
        var registry = await _registry.ReadDashboardRegistryAsync();
        CentralConfigured = registry.Configured;
        Databases = registry.Databases;
        var (dateFrom, dateTo) = ReportRange();
        var subs = DetailDatabases();
        var selected = SelectedDatabases();
        GlDetails = await LoadCollectionAsync(selected, dateFrom, dateTo, CancellationToken.None);
        var selectedCodes = selected.Select(db => db.DbCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Details = new DashboardDetailsModel
        {
            Rows = GlDetails,
            Databases = subs,
            SelectedDbCodes = selectedCodes,
            AllDatabases = !DbFilter || (subs.Count > 0 && selected.Count == subs.Count),
            DateFrom = dateFrom,
            DateTo = dateTo,
            DefaultDateFrom = DefaultDateFrom,
            DefaultDateTo = DefaultDateTo,
            Customer = Customer ?? ""
        };
    }

    public async Task<IActionResult> OnGetDetailsAsync()
    {
        var registry = await _registry.ReadDashboardRegistryAsync();
        CentralConfigured = registry.Configured;
        Databases = registry.Databases;
        var (dateFrom, dateTo) = ReportRange();
        var rows = await LoadCollectionAsync(SelectedDatabases(), dateFrom, dateTo, CancellationToken.None);
        return Partial("_TransactionResults", rows);
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        await LoadRegistryAsync();
        if (!CentralConfigured)
            return Plain(StatusCodes.Status503ServiceUnavailable, "The company registry is not available.");
        DbFilter = true;
        var selected = SelectedDatabases();
        if (selected.Count == 0)
            return Plain(StatusCodes.Status400BadRequest, "Select a company before exporting.");
        var (dateFrom, dateTo) = ReportRange();
        var rows = await LoadCollectionAsync(selected, dateFrom, dateTo, CancellationToken.None);
        return File(MomCollectionExcel.Build(rows), MomCollectionExcel.ContentType, $"Collection-Report-{ReportYear}.xlsx");
    }

    private async Task LoadRegistryAsync()
    {
        var registry = await _registry.ReadDashboardRegistryAsync();
        CentralConfigured = registry.Configured;
        Databases = registry.Databases;
    }

    private static ContentResult Plain(int statusCode, string message) => new()
    {
        StatusCode = statusCode,
        Content = message,
        ContentType = "text/plain; charset=utf-8"
    };

    private int ReportYear
    {
        get
        {
            if (Year is int year && year is >= 1990 and <= 2100)
                return year;
            if (DateFrom is DateTime from && from.Year is >= 1990 and <= 2100)
                return from.Year;
            return DateTime.Today.Year;
        }
    }

    private (DateTime From, DateTime To) ReportRange() =>
        (new DateTime(ReportYear, 1, 1), new DateTime(ReportYear, 12, 31));

    private DateTime DefaultDateFrom => new(DateTime.Today.Year, 1, 1);

    private DateTime DefaultDateTo => new(DateTime.Today.Year, 12, 31);

    private List<RegisteredDatabase> DetailDatabases() =>
        Databases.Where(db => string.Equals(db.DbRole, "SUB", StringComparison.OrdinalIgnoreCase)).ToList();

    private List<RegisteredDatabase> SelectedDatabases()
    {
        var subs = DetailDatabases();
        if (!DbFilter)
            return subs;
        var wanted = Db
            .Select(code => (code ?? "").Trim())
            .Where(code => code.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return subs.Where(db => wanted.Contains(db.DbCode)).ToList();
    }

    private async Task<List<DashboardDetailRow>> LoadCollectionAsync(
        IReadOnlyList<RegisteredDatabase> databases, DateTime dateFrom, DateTime dateTo, CancellationToken cancellationToken)
    {
        if (!CentralConfigured || databases.Count == 0)
            return [];
        // One Monthly Collection load at a time. A later filter change waits until the
        // active Firebird reads finish, then starts its own load.
        await CollectionLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = new List<(RegisteredDatabase Db, List<MomCollectionSourceRow>? Lines, IReadOnlyList<string> MissingColumns)>(databases.Count);
            for (var index = 0; index < databases.Count; index += CompanyBatchSize)
            {
                var batch = databases.Skip(index).Take(CompanyBatchSize).ToArray();
                var batchRows = await Task.WhenAll(batch.Select(async db =>
                {
                    try
                    {
                        var lines = await _companyRead.ListMomCollectionAsync(db, dateFrom, dateTo, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        return (db, lines, (IReadOnlyList<string>)Array.Empty<string>());
                    }
                    catch (MomCollectionColumnException ex)
                    {
                        return (db, (List<MomCollectionSourceRow>?)null, ex.Columns);
                    }
                })).ConfigureAwait(false);
                loaded.AddRange(batchRows);
            }
            return BuildCollectionRows(loaded);
        }
        finally
        {
            CollectionLoad.Release();
        }
    }

    private static List<DashboardDetailRow> BuildCollectionRows(
        IReadOnlyList<(RegisteredDatabase Db, List<MomCollectionSourceRow>? Lines, IReadOnlyList<string> MissingColumns)> loaded)
    {
        var rows = new List<DashboardDetailRow>();
        foreach (var (db, lines, missingColumns) in loaded)
        {
            var databaseName = (db.DisplayName ?? "").Trim();
            if (databaseName.Length == 0)
                databaseName = (db.DbCode ?? "").Trim();
            if (missingColumns.Count > 0)
            {
                rows.Add(new DashboardDetailRow
                {
                    DatabaseName = databaseName,
                    Role = db.DbRole,
                    LoadFailed = true,
                    LoadErrors = missingColumns
                        .Select(column => $"Column not found: SL_IV.{column} (Database: {databaseName})")
                        .ToList()
                });
                continue;
            }
            if (lines is null)
            {
                rows.Add(new DashboardDetailRow
                {
                    DatabaseName = databaseName,
                    Role = db.DbRole,
                    LoadFailed = true
                });
                continue;
            }
            var collection = GroupCollection(lines);
            decimal total = 0m;
            foreach (var line in collection)
            {
                foreach (var day in line.ReceiptsByMonth)
                {
                    foreach (var receipt in day)
                        total += receipt.Receipts.Sum(item => item.Amount);
                }
            }
            rows.Add(new DashboardDetailRow
            {
                DatabaseName = databaseName,
                Role = db.DbRole,
                CollectionLines = collection,
                TotalAmount = total
            });
        }
        return rows;
    }

    private static List<MomCollectionLine> GroupCollection(IReadOnlyList<MomCollectionSourceRow> lines)
    {
        var groups = new Dictionary<string, MomCollectionLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.DocDate is not DateTime docDate)
                continue;
            var month = docDate.Month;
            if (month is < 1 or > 12)
                continue;
            var key = string.Join('\u001f', line.Entities, line.ProjectCode);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new MomCollectionLine
                {
                    Entities = line.Entities,
                    Properties = line.Properties,
                    Type = line.Type,
                    ProjectCode = line.ProjectCode,
                    LotNumber = line.LotNumber
                };
                groups[key] = group;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(group.Properties) && !string.IsNullOrWhiteSpace(line.Properties))
                    group.Properties = line.Properties;
                if (string.IsNullOrWhiteSpace(group.Type) && !string.IsNullOrWhiteSpace(line.Type))
                    group.Type = line.Type;
                if (string.IsNullOrWhiteSpace(group.LotNumber) && !string.IsNullOrWhiteSpace(line.LotNumber))
                    group.LotNumber = line.LotNumber;
            }
            var date = docDate.Date;
            var days = group.ReceiptsByMonth[month - 1];
            var day = days.Find(item => item.Date == date);
            if (day is null)
            {
                day = new MomCollectionReceiptDay { Date = date };
                days.Add(day);
            }
            day.Receipts.Add(new MomCollectionReceipt
            {
                OrNo = line.OrNo.Trim(),
                InvoiceAmount = line.DocAmount,
                Amount = line.OrAmount
            });
        }
        return groups.Values
            .OrderBy(line => line.Entities, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.ProjectCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
