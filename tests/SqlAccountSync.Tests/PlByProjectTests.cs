using ClosedXML.Excel;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class PlByProjectTests
{
    [Fact]
    public void Groups_lines_by_account_code_and_keeps_the_first_description()
    {
        var matrix = PlByProjectMatrix.From(
        [
            Company("Alpha",
                Line("400-100", "   ", 1m),
                Line("400-100", "Sales", 2m),
                Line("400-100", "Later description", 3m))
        ]);

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("400-100", row.Code);
        Assert.Equal("Sales", row.Description);
        Assert.Equal(6m, row.Amounts[0]);
    }

    [Fact]
    public void Sums_row_company_and_grand_totals()
    {
        var matrix = PlByProjectMatrix.From(
        [
            Company("Alpha", Line("400-100", "Sales", 10m), Line("500-100", "Cost", -3m)),
            Company("Beta", Line("400-100", "Sales", 4m))
        ]);

        Assert.Equal(new[] { "400-100", "500-100" }, matrix.Rows.Select(row => row.Code).ToArray());
        Assert.Equal(14m, matrix.Rows[0].Total);
        Assert.Equal(-3m, matrix.Rows[1].Total);
        Assert.Equal(7m, matrix.Rows.Sum(row => row.Amounts[0]));
        Assert.Equal(4m, matrix.Rows.Sum(row => row.Amounts[1]));
        Assert.Equal(11m, matrix.Rows.Sum(row => row.Total));
    }

    [Fact]
    public void Excel_export_keeps_zero_company_columns_and_totals()
    {
        var matrix = PlByProjectMatrix.From(
        [
            Company("Alpha", Line("400-100", "Sales", 10m), Line("500-100", "Cost", -3m)),
            Company("Beta", Line("400-100", "Sales", 0m))
        ]);

        using var workbook = Open(PlByProjectExcel.Build(matrix));
        var sheet = workbook.Worksheet("PL by Project");

        Assert.Equal("Beta", sheet.Cell(1, 4).GetString());
        Assert.Equal(10m, sheet.Cell(2, 3).GetValue<decimal>());
        Assert.Equal(0m, sheet.Cell(2, 4).GetValue<decimal>());
        Assert.Equal(10m, sheet.Cell(2, 5).GetValue<decimal>());
        Assert.Equal(-3m, sheet.Cell(3, 3).GetValue<decimal>());
        Assert.Equal(0m, sheet.Cell(3, 4).GetValue<decimal>());
        Assert.Equal(-3m, sheet.Cell(3, 5).GetValue<decimal>());
        Assert.Equal("TOTAL", sheet.Cell(4, 1).GetString());
        Assert.Equal(7m, sheet.Cell(4, 3).GetValue<decimal>());
        Assert.Equal(0m, sheet.Cell(4, 4).GetValue<decimal>());
        Assert.Equal(7m, sheet.Cell(4, 5).GetValue<decimal>());
    }

    private static DashboardDetailRow Company(string name, params GlTransLine[] lines) => new()
    {
        DatabaseName = name,
        Records = lines
    };

    private static GlTransLine Line(string code, string description, decimal amount) => new()
    {
        AccountCode = code,
        Project = description,
        Amount = amount
    };

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));
}
