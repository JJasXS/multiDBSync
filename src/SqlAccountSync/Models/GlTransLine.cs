namespace SqlAccountSync.Models;

public sealed class GlTransLine
{
    public DateTime? DocDate { get; init; }
    public string Ref1 { get; init; } = "";
    public string Project { get; init; } = "";
    public string AccountDescription { get; init; } = "";
    public decimal Amount { get; init; }
    public string AccountCode { get; init; } = "";
}

public sealed class DashboardDetailRow
{
    public string DatabaseName { get; init; } = "";
    public string Role { get; init; } = "";
    public IReadOnlyList<GlTransLine> Records { get; init; } = [];
    public decimal TotalAmount { get; init; }
}

public sealed class DashboardDetailsModel
{
    public IReadOnlyList<DashboardDetailRow> Rows { get; init; } = [];
    public IReadOnlyList<RegisteredDatabase> Databases { get; init; } = [];
    public IReadOnlySet<string> SelectedDbCodes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool AllDatabases { get; init; } = true;
    public DateTime DateFrom { get; init; }
    public DateTime DateTo { get; init; }
    public DateTime DefaultDateFrom { get; init; }
    public DateTime DefaultDateTo { get; init; }
    public string Customer { get; init; } = "";
}
