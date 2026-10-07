using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Reports;

public class PlByProjectModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _companyRead;

    public PlByProjectModel(IDatabaseRegistry registry, ISqlAccountReadAdapter companyRead)
    {
        _registry = registry;
        _companyRead = companyRead;
    }

    public List<RegisteredDatabase> Databases { get; set; } = [];
    public bool CentralConfigured { get; set; }
    public DashboardDetailsModel Details { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public DateTime? DateFrom { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? DateTo { get; set; }
    [BindProperty(SupportsGet = true)] public List<string> Db { get; set; } = [];
    [BindProperty(SupportsGet = true)] public bool DbFilter { get; set; }

    public async Task OnGetAsync()
    {
        await LoadRegistryAsync(CancellationToken.None);
        var dateFrom = (DateFrom ?? DefaultDateFrom).Date;
        var dateTo = (DateTo ?? DefaultDateTo).Date;
        var subs = DetailDatabases();
        var selected = SelectedDatabases();
        var rows = await LoadRowsAsync(selected, dateFrom, dateTo, "", CancellationToken.None);
        var selectedCodes = selected.Select(db => db.DbCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Details = new DashboardDetailsModel
        {
            Rows = rows,
            Databases = subs,
            SelectedDbCodes = selectedCodes,
            AllDatabases = !DbFilter || (subs.Count > 0 && selected.Count == subs.Count),
            DateFrom = dateFrom,
            DateTo = dateTo,
            DefaultDateFrom = DefaultDateFrom,
            DefaultDateTo = DefaultDateTo
        };
    }

    public async Task<IActionResult> OnGetDetailsAsync()
    {
        await LoadRegistryAsync(CancellationToken.None);
        var dateFrom = (DateFrom ?? DefaultDateFrom).Date;
        var dateTo = (DateTo ?? DefaultDateTo).Date;
        var rows = await LoadRowsAsync(SelectedDatabases(), dateFrom, dateTo, "", CancellationToken.None);
        return Partial("_PlByProjectResults", rows);
    }

    public async Task<IActionResult> OnGetExportExcelAsync()
    {
        await LoadRegistryAsync(CancellationToken.None);
        var dateFrom = (DateFrom ?? DefaultDateFrom).Date;
        var dateTo = (DateTo ?? DefaultDateTo).Date;
        var rows = await LoadRowsAsync(SelectedDatabases(), dateFrom, dateTo, "", CancellationToken.None);
        var bytes = PlByProjectExcel.Build(PlByProjectMatrix.From(rows));
        return File(bytes, PlByProjectExcel.ContentType, $"PL-by-Project-{dateFrom:yyyy-MM-dd}-to-{dateTo:yyyy-MM-dd}.xlsx");
    }

    private async Task LoadRegistryAsync(CancellationToken cancellationToken)
    {
        var registry = await _registry.ReadDashboardRegistryAsync(cancellationToken);
        CentralConfigured = registry.Configured;
        Databases = registry.Databases;
    }

    private DateTime DefaultDateFrom => new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private DateTime DefaultDateTo => DefaultDateFrom.AddMonths(1).AddDays(-1);

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

    private async Task<List<DashboardDetailRow>> LoadRowsAsync(
        IReadOnlyList<RegisteredDatabase> databases,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? customer,
        CancellationToken cancellationToken)
    {
        if (!CentralConfigured || databases.Count == 0)
            return [];
        var loaded = await Task.WhenAll(databases.Select(async db =>
        {
            var lines = await _companyRead.ListPlByProjectLinesAsync(db, dateFrom, dateTo, customer, cancellationToken);
            return (db, lines);
        }));
        var rows = new List<DashboardDetailRow>();
        foreach (var (db, lines) in loaded)
        {
            if (lines is null)
                continue;
            rows.Add(new DashboardDetailRow
            {
                DatabaseName = ReportCompanyName.Label(db.DisplayName),
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
