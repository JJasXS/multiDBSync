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

public sealed class MomCollectionSourceRow
{
    public int DocKey { get; init; }
    public string Entities { get; init; } = "";
    public string Properties { get; init; } = "";
    public string Type { get; init; } = "";
    public string ProjectCode { get; init; } = "";
    public DateTime? OrDate { get; init; }
    public string OrNo { get; init; } = "";
    public decimal OrAmount { get; init; }
}

public sealed class MomCollectionReceipt
{
    public string OrNo { get; init; } = "";
    public decimal Amount { get; init; }
}

public sealed class MomCollectionReceiptDay
{
    public DateTime Date { get; init; }
    public List<MomCollectionReceipt> Receipts { get; init; } = [];
}

public sealed class MomCollectionLine
{
    public string Entities { get; init; } = "";
    public string Properties { get; init; } = "";
    public string Type { get; init; } = "";
    public string ProjectCode { get; init; } = "";
    public List<MomCollectionReceiptDay>[] ReceiptsByMonth { get; init; } = CreateReceiptMonths();

    private static List<MomCollectionReceiptDay>[] CreateReceiptMonths()
    {
        var months = new List<MomCollectionReceiptDay>[12];
        for (var i = 0; i < months.Length; i++)
            months[i] = [];
        return months;
    }
}

public sealed class DashboardDetailRow
{
    public string DatabaseName { get; init; } = "";
    public string Role { get; init; } = "";
    public IReadOnlyList<GlTransLine> Records { get; init; } = [];
    public IReadOnlyList<MomCollectionLine> CollectionLines { get; init; } = [];
    public bool LoadFailed { get; init; }
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
