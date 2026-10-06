using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Reports;

public class MomCollectionModel : SyncPageModel
{
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
        GlDetails = await LoadGlDetailsAsync(selected, dateFrom, dateTo, CancellationToken.None);
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
        var rows = await LoadGlDetailsAsync(SelectedDatabases(), dateFrom, dateTo, CancellationToken.None);
        return Partial("_TransactionResults", rows);
    }

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

    // Year selector uses the existing date-range load. No new filter query.
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

    private async Task<List<DashboardDetailRow>> LoadGlDetailsAsync(
        IReadOnlyList<RegisteredDatabase> databases, DateTime dateFrom, DateTime dateTo, CancellationToken cancellationToken)
    {
        if (!CentralConfigured || databases.Count == 0)
            return [];
        var loaded = await Task.WhenAll(databases.Select(async db =>
        {
            var lines = await _companyRead.ListGlTransactionsAsync(db, dateFrom, dateTo, Customer, cancellationToken);
            return (db, lines);
        }));
        var rows = new List<DashboardDetailRow>();
        foreach (var (db, lines) in loaded)
        {
            if (lines is null)
                continue;
            rows.Add(new DashboardDetailRow
            {
                DatabaseName = db.DisplayName,
                Role = db.DbRole,
                Records = lines,
                TotalAmount = SumAmounts(lines)
            });
        }
        return rows;
    }

    private static decimal SumAmounts(IReadOnlyList<GlTransLine> lines)
    {
        decimal total = 0m;
        foreach (var line in lines)
            total += line.Amount;
        return total;
    }
}
