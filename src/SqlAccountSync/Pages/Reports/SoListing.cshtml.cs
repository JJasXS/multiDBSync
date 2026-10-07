using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Pages.Reports;

public class SoListingModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountReadAdapter _companyRead;

    public SoListingModel(IDatabaseRegistry registry, ISqlAccountReadAdapter companyRead)
    {
        _registry = registry;
        _companyRead = companyRead;
    }

    public SoListingDetails Details { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public int CompanyId { get; set; }
    [BindProperty(SupportsGet = true)] public string FilterColumn { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string FilterValue { get; set; } = "";

    public async Task OnGetAsync()
    {
        Details = await LoadAsync(CompanyId, FilterColumn, FilterValue, CancellationToken.None);
    }

    public async Task<IActionResult> OnGetDetailsAsync()
    {
        var details = await LoadAsync(CompanyId, FilterColumn, FilterValue, CancellationToken.None);
        if (!details.DatabaseAvailable)
            return Plain(StatusCodes.Status503ServiceUnavailable, details.ErrorMessage);
        return Partial("_SoListingResults", details);
    }

    public async Task<IActionResult> OnGetExportCompanyAsync()
    {
        if (CompanyId <= 0)
            return Plain(StatusCodes.Status400BadRequest, "Select a company before exporting that company.");
        var details = await LoadAsync(CompanyId, "", "", CancellationToken.None);
        if (!details.DatabaseAvailable)
            return Plain(StatusCodes.Status503ServiceUnavailable, details.ErrorMessage);
        var label = details.Companies.FirstOrDefault(company => company.DbId == CompanyId)?.Label ?? "Company";
        return File(SoListingExcel.Build(details.Rows), SoListingExcel.ContentType, $"SO-Listing-{FileToken(label)}.xlsx");
    }

    public async Task<IActionResult> OnGetExportAllAsync()
    {
        var details = await LoadAsync(0, "", "", CancellationToken.None);
        if (!details.DatabaseAvailable)
            return Plain(StatusCodes.Status503ServiceUnavailable, details.ErrorMessage);
        return File(SoListingExcel.Build(details.Rows), SoListingExcel.ContentType, "SO-Listing-All.xlsx");
    }

    private async Task<SoListingDetails> LoadAsync(
        int companyId, string? filterColumn, string? filterValue, CancellationToken cancellationToken)
    {
        var column = (filterColumn ?? "").Trim();
        var value = (filterValue ?? "").Trim();
        var registry = await _registry.ReadDashboardRegistryAsync(cancellationToken);
        var subs = registry.Databases
            .Where(db => string.Equals(db.DbRole, "SUB", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var companies = subs
            .Select(db => new SoListingCompanyOption { DbId = db.DbId, Label = CompanyLabel(db) })
            .ToList();

        SoListingDetails Unavailable(string message, int selectedCompanyId = 0) => new()
        {
            CompanyId = selectedCompanyId,
            Companies = companies,
            FilterColumn = column,
            FilterValue = value,
            ErrorMessage = message
        };

        if (!registry.Configured)
            return Unavailable("The company registry is not available.");
        if (companies.Count == 0)
            return Unavailable("No active sub companies are in the registry.");

        IReadOnlyList<RegisteredDatabase> targets;
        if (companyId > 0)
        {
            var database = subs.FirstOrDefault(db => db.DbId == companyId);
            if (database is null)
                return Unavailable("The selected company is not an active sub company.", companyId);
            targets = [database];
        }
        else
        {
            targets = subs;
        }

        var (rows, failures) = await ReadCompaniesAsync(targets, column, value, cancellationToken);
        if (failures.Count == targets.Count)
            return Unavailable(string.Join(" ", failures), companyId);

        return new SoListingDetails
        {
            Rows = rows,
            CompanyId = companyId > 0 ? companyId : 0,
            Companies = companies,
            FilterColumn = column,
            FilterValue = value,
            DatabaseAvailable = true,
            ReadFailures = failures
        };
    }

    private async Task<(List<SoListingRow> Rows, List<string> Failures)> ReadCompaniesAsync(
        IReadOnlyList<RegisteredDatabase> databases,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        var loaded = await Task.WhenAll(databases.Select(async db =>
        {
            var rows = await _companyRead.ListSoListingAsync(db, column, value, cancellationToken);
            return (db, rows);
        }));
        var rows = new List<SoListingRow>();
        var failures = new List<string>();
        foreach (var (db, result) in loaded)
        {
            if (result is null)
            {
                failures.Add($"{CompanyLabel(db)} could not be read.");
                continue;
            }
            var label = CompanyLabel(db);
            foreach (var row in result)
                row.Company = label;
            rows.AddRange(result);
        }
        return (rows, failures);
    }

    private static ContentResult Plain(int statusCode, string? message) => new()
    {
        StatusCode = statusCode,
        Content = string.IsNullOrWhiteSpace(message) ? "SO listing data could not be loaded." : message,
        ContentType = "text/plain; charset=utf-8"
    };

    private static string CompanyLabel(RegisteredDatabase db)
    {
        if (!string.IsNullOrWhiteSpace(db.DisplayName))
            return ReportCompanyName.Label(db.DisplayName);
        if (!string.IsNullOrWhiteSpace(db.CompanyName))
            return ReportCompanyName.Label(db.CompanyName);
        if (!string.IsNullOrWhiteSpace(db.DbCode))
            return db.DbCode.Trim();
        return "Selected company";
    }

    private static string FileToken(string value)
    {
        var cleaned = new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        return cleaned.Length == 0 ? "Company" : cleaned;
    }
}
