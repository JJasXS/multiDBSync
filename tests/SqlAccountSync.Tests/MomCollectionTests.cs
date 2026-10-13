using System.Reflection;
using ClosedXML.Excel;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Pages.Reports;

namespace SqlAccountSync.Tests;

public class MomCollectionTests
{
    [Fact]
    public async Task Groups_rows_by_entity_and_project_code()
    {
        var lines = await Load(
            Source("Acme", "P1", 10m, 1m, new DateTime(2026, 1, 5)),
            Source("Acme", "P1", 20m, 2m, new DateTime(2026, 1, 6)),
            Source("Acme", "P2", 5m, 0m, new DateTime(2026, 2, 1)));

        Assert.Equal(2, lines.Count);
        Assert.Equal(("Acme", "P1"), (lines[0].Entities, lines[0].ProjectCode));
        Assert.Equal(("Acme", "P2"), (lines[1].Entities, lines[1].ProjectCode));
        Assert.Equal(30m, MonthInvoice(lines[0], 1));
        Assert.Equal(3m, MonthReceipt(lines[0], 1));
    }

    [Fact]
    public async Task Keeps_the_first_non_blank_property_type_and_lot()
    {
        var lines = await Load(
            Source("Acme", "P1", 1m, 0m, new DateTime(2026, 1, 1), properties: "", type: "", lot: ""),
            Source("Acme", "P1", 1m, 0m, new DateTime(2026, 1, 2), properties: "Tower A", type: "Office", lot: "L-01"),
            Source("Acme", "P1", 1m, 0m, new DateTime(2026, 1, 3), properties: "Tower B", type: "Retail", lot: "L-02"));

        var line = Assert.Single(lines);
        Assert.Equal("Tower A", line.Properties);
        Assert.Equal("Office", line.Type);
        Assert.Equal("L-01", line.LotNumber);
    }

    [Fact]
    public async Task Allocates_invoice_and_receipt_by_docdate_month()
    {
        var lines = await Load(
            Source("Acme", "P1", 100m, 40m, new DateTime(2026, 1, 15)),
            Source("Acme", "P1", 7m, 1m, new DateTime(2026, 3, 2)));

        var line = Assert.Single(lines);
        Assert.Equal(100m, MonthInvoice(line, 1));
        Assert.Equal(40m, MonthReceipt(line, 1));
        Assert.Equal(0m, MonthInvoice(line, 2));
        Assert.Equal(7m, MonthInvoice(line, 3));
        Assert.Equal(1m, MonthReceipt(line, 3));
        Assert.Equal(0m, MonthReceipt(line, 12));
    }

    [Fact]
    public async Task Skips_rows_with_no_docdate()
    {
        var lines = await Load(
            Source("Acme", "P1", 999m, 999m, docDate: null),
            Source("Acme", "P2", 8m, 2m, new DateTime(2026, 4, 9)));

        var line = Assert.Single(lines);
        Assert.Equal("P2", line.ProjectCode);
        Assert.Equal(8m, MonthInvoice(line, 4));
        Assert.Equal(2m, MonthReceipt(line, 4));
    }

    [Fact]
    public void Excel_writes_january_to_december_totals()
    {
        var rows = new List<DashboardDetailRow> { GroupedCompany("Steppes") };

        using var workbook = Open(MomCollectionExcel.Build(rows));
        var sheet = workbook.Worksheet("Collection Report");

        Assert.Equal("Jan", sheet.Cell(1, 7).GetString());
        Assert.Equal("Dec", sheet.Cell(1, 29).GetString());
        Assert.Equal(100m, sheet.Cell(3, 7).GetValue<decimal>());
        Assert.Equal(40m, sheet.Cell(3, 8).GetValue<decimal>());
        Assert.Equal(7m, sheet.Cell(3, 29).GetValue<decimal>());
        Assert.Equal(1m, sheet.Cell(3, 30).GetValue<decimal>());
        Assert.Equal(107m, sheet.Cell(3, 31).GetValue<decimal>());
        Assert.Equal(41m, sheet.Cell(3, 32).GetValue<decimal>());
        Assert.Equal("TOTAL", sheet.Cell(4, 1).GetString());
        Assert.Equal(107m, sheet.Cell(4, 31).GetValue<decimal>());
        Assert.Equal(41m, sheet.Cell(4, 32).GetValue<decimal>());
    }

    [Fact]
    public void Excel_omits_companies_that_failed_to_load()
    {
        var rows = new List<DashboardDetailRow>
        {
            new() { DatabaseName = "Missing", LoadFailed = true, LoadErrors = ["Column not found: SL_IV.UDF_ORAMT"] },
            GroupedCompany("Steppes")
        };

        using var workbook = Open(MomCollectionExcel.Build(rows));
        var sheet = workbook.Worksheet("Collection Report");
        var used = sheet.RangeUsed()!.CellsUsed().Select(cell => cell.GetString());

        Assert.DoesNotContain("Missing", used);
        Assert.Equal("Steppes", sheet.Cell(3, 1).GetString());
        Assert.Equal(107m, sheet.Cell(4, 31).GetValue<decimal>());
    }

    private static Task<IReadOnlyList<MomCollectionLine>> Load(params MomCollectionSourceRow[] sources)
    {
        var group = typeof(MomCollectionModel).GetMethod("GroupCollection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(group);
        var lines = Assert.IsAssignableFrom<IReadOnlyList<MomCollectionLine>>(group.Invoke(null, [sources]));
        return Task.FromResult(lines);
    }

    private static MomCollectionSourceRow Source(
        string entity, string project, decimal invoice, decimal receipt, DateTime? docDate,
        string properties = "", string type = "", string lot = "") => new()
    {
        Entities = entity,
        ProjectCode = project,
        Properties = properties,
        Type = type,
        LotNumber = lot,
        DocAmount = invoice,
        OrAmount = receipt,
        DocDate = docDate
    };

    private static DashboardDetailRow GroupedCompany(string name)
    {
        var line = new MomCollectionLine { Entities = "Acme", ProjectCode = "P1", Properties = "Tower A" };
        line.ReceiptsByMonth[0].Add(Day(new DateTime(2026, 1, 15), 100m, 40m));
        line.ReceiptsByMonth[11].Add(Day(new DateTime(2026, 12, 20), 7m, 1m));
        return new DashboardDetailRow { DatabaseName = name, CollectionLines = [line] };
    }

    private static MomCollectionReceiptDay Day(DateTime date, decimal invoice, decimal receipt) => new()
    {
        Date = date,
        Receipts = [new MomCollectionReceipt { InvoiceAmount = invoice, Amount = receipt }]
    };

    private static decimal MonthInvoice(MomCollectionLine line, int month) =>
        line.ReceiptsByMonth[month - 1].SelectMany(day => day.Receipts).Sum(item => item.InvoiceAmount);

    private static decimal MonthReceipt(MomCollectionLine line, int month) =>
        line.ReceiptsByMonth[month - 1].SelectMany(day => day.Receipts).Sum(item => item.Amount);

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
}
