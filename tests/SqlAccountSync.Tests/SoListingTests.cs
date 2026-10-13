using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Pages.Reports;

namespace SqlAccountSync.Tests;

public class SoListingTests
{
    [Fact]
    public void Excel_totals_the_seven_amount_fields()
    {
        using var workbook = Open(SoListingExcel.Build(
        [
            Row(security: 1m, utilities: 2m, fitOut: 3m, access: 4m, booking: 5m, rental: 6m, service: 7m)
        ]));
        var sheet = workbook.Worksheet("SO Listing");

        Assert.Equal(1m, sheet.Cell(2, 6).GetValue<decimal>());
        Assert.Equal(2m, sheet.Cell(2, 7).GetValue<decimal>());
        Assert.Equal(3m, sheet.Cell(2, 8).GetValue<decimal>());
        Assert.Equal(4m, sheet.Cell(2, 9).GetValue<decimal>());
        Assert.Equal(5m, sheet.Cell(2, 10).GetValue<decimal>());
        Assert.Equal(6m, sheet.Cell(2, 22).GetValue<decimal>());
        Assert.Equal(7m, sheet.Cell(2, 23).GetValue<decimal>());
        Assert.Equal(28m, sheet.Cell(2, 24).GetValue<decimal>());
        Assert.Equal("TOTAL", sheet.Cell(3, 1).GetString());
        Assert.Equal(1m, sheet.Cell(3, 6).GetValue<decimal>());
        Assert.Equal(7m, sheet.Cell(3, 23).GetValue<decimal>());
        Assert.Equal(28m, sheet.Cell(3, 24).GetValue<decimal>());
    }

    [Fact]
    public void Excel_treats_null_amounts_as_zero()
    {
        using var workbook = Open(SoListingExcel.Build([Row(rental: 10m)]));
        var sheet = workbook.Worksheet("SO Listing");

        Assert.True(sheet.Cell(2, 6).IsEmpty());
        Assert.Equal(10m, sheet.Cell(2, 22).GetValue<decimal>());
        Assert.Equal(10m, sheet.Cell(2, 24).GetValue<decimal>());
        Assert.Equal(0m, sheet.Cell(3, 6).GetValue<decimal>());
        Assert.Equal(10m, sheet.Cell(3, 22).GetValue<decimal>());
        Assert.Equal(10m, sheet.Cell(3, 24).GetValue<decimal>());
    }

    [Fact]
    public void Excel_adds_every_sales_order_row()
    {
        using var workbook = Open(SoListingExcel.Build(
        [
            Row(project: "P1", soNo: "SO-1", security: 10m, rental: 100m),
            Row(project: "P1", soNo: "SO-2", security: 20m, rental: 50m)
        ]));
        var sheet = workbook.Worksheet("SO Listing");

        Assert.Equal("SO-1", sheet.Cell(2, 5).GetString());
        Assert.Equal("SO-2", sheet.Cell(3, 5).GetString());
        Assert.Equal(30m, sheet.Cell(4, 6).GetValue<decimal>());
        Assert.Equal(150m, sheet.Cell(4, 22).GetValue<decimal>());
        Assert.Equal(180m, sheet.Cell(4, 24).GetValue<decimal>());
    }

    [Fact]
    public async Task Export_uses_the_selected_company_or_every_active_sub()
    {
        var hq = Company(1, "HQ", "MASTER");
        var first = Company(2, "S1", "SUB");
        var second = Company(3, "S2", "SUB");
        var both = Company(4, "BOTH1", "BOTH");
        var adapter = new ReportReadAdapter
        {
            SalesOrders = db => db.DbId switch
            {
                2 => [Row(project: "P1", soNo: "SO-S1", security: 10m)],
                3 => [Row(project: "P2", soNo: "SO-S2", security: 20m)],
                _ => throw new InvalidOperationException($"Report read was not limited to Sub companies: {db.DbCode}")
            }
        };
        var registry = new ReportRegistry(hq, first, second, both);

        var selected = new SoListingModel(registry, adapter) { CompanyId = 2 };
        var selectedFile = Assert.IsType<FileContentResult>(await selected.OnGetExportCompanyAsync());
        using var selectedBook = Open(selectedFile.FileContents);
        var selectedSheet = selectedBook.Worksheet("SO Listing");
        Assert.Equal("S1", selectedSheet.Cell(2, 1).GetString());
        Assert.Equal("SO-S1", selectedSheet.Cell(2, 5).GetString());
        Assert.Equal("TOTAL", selectedSheet.Cell(3, 1).GetString());
        Assert.Equal(10m, selectedSheet.Cell(3, 6).GetValue<decimal>());
        Assert.Equal(3, selectedSheet.LastRowUsed()!.RowNumber());

        var all = new SoListingModel(registry, adapter) { CompanyId = 0 };
        var allFile = Assert.IsType<FileContentResult>(await all.OnGetExportAllAsync());
        using var allBook = Open(allFile.FileContents);
        var allSheet = allBook.Worksheet("SO Listing");
        Assert.Equal(new[] { "S1", "S2" }, new[] { allSheet.Cell(2, 1).GetString(), allSheet.Cell(3, 1).GetString() });
        Assert.Equal(30m, allSheet.Cell(4, 6).GetValue<decimal>());
    }

    private static RegisteredDatabase Company(int id, string code, string role) => new()
    {
        DbId = id,
        DbCode = code,
        DisplayName = code,
        DbRole = role
    };

    private static SoListingRow Row(
        string project = "P1",
        string soNo = "SO-1",
        decimal? security = null,
        decimal? utilities = null,
        decimal? fitOut = null,
        decimal? access = null,
        decimal? booking = null,
        decimal? rental = null,
        decimal? service = null) => new()
    {
        ProjectCode = project,
        SoNo = soNo,
        SecurityDeposit = security,
        UtilitiesDeposit = utilities,
        FitOutDeposits = fitOut,
        AccessCard = access,
        BookingDeposits = booking,
        MonthlyRental = rental,
        ServiceCharge = service
    };

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
}
