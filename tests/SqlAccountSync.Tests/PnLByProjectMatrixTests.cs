using ClosedXML.Excel;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class PnLByProjectMatrixTests
{
    [Fact]
    public void Splits_company_columns_into_project_subcolumns()
    {
        var matrix = PnLByProjectMatrix.From(
        [
            Company("Miracle",
                Line("51000", "OTHER INCOME", "Project A", 100m),
                Line("51000", "OTHER INCOME", "Project B", 200m),
                Line("51000", "OTHER INCOME", "Project C", 300m)),
            Company("On Yat",
                Line("51000", "OTHER INCOME", "Project A", 400m),
                Line("51000", "OTHER INCOME", "Project B", 500m))
        ]);

        Assert.Equal(2, matrix.Companies.Count);
        Assert.Equal(new[] { "Project A", "Project B", "Project C" }, matrix.Companies[0].Projects);
        Assert.Equal(new[] { "Project A", "Project B" }, matrix.Companies[1].Projects);

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("51000", row.Code);
        Assert.Equal("OTHER INCOME", row.Description);
        Assert.Equal(new[] { 100m, 200m, 300m, 400m, 500m }, row.Amounts);
        Assert.Equal(1500m, row.Total);
    }

    [Fact]
    public void Keeps_default_project_code_and_does_not_merge_blank()
    {
        var matrix = PnLByProjectMatrix.From(
        [
            Company("Alpha",
                Line("400-100", "Sales", "", 1m),
                Line("400-100", "Sales", "----", 2m),
                Line("400-100", "Sales", "Project A", 3m))
        ]);

        Assert.Equal(new[] { "----", "Project A", "" }, matrix.Companies[0].Projects);
        Assert.Equal(new[] { 2m, 3m, 1m }, matrix.Rows[0].Amounts);
        Assert.Equal(6m, matrix.Rows[0].Total);
    }

    [Fact]
    public void Project_column_sums_match_company_level_totals()
    {
        var companies = new[]
        {
            Company("Miracle",
                Line("51000", "OTHER INCOME", "----", 100m),
                Line("51000", "OTHER INCOME", "Project A", 50m),
                Line("52000", "COST", "----", -20m)),
            Company("On Yat",
                Line("51000", "OTHER INCOME", "----", 40m),
                Line("51000", "OTHER INCOME", "Project B", 10m))
        };

        var companyMatrix = PlByProjectMatrix.From(companies);
        var projectMatrix = PnLByProjectMatrix.From(companies);

        Assert.Equal(companyMatrix.Rows.Count, projectMatrix.Rows.Count);
        for (var i = 0; i < companyMatrix.Rows.Count; i++)
        {
            Assert.Equal(companyMatrix.Rows[i].Code, projectMatrix.Rows[i].Code);
            Assert.Equal(companyMatrix.Rows[i].Total, projectMatrix.Rows[i].Total);
            Assert.Equal(
                companyMatrix.Rows[i].Amounts.Sum(),
                projectMatrix.Rows[i].Amounts.Sum());
        }

        Assert.Equal(
            companyMatrix.Rows.Sum(row => row.Total),
            projectMatrix.Rows.Sum(row => row.Total));
    }

    [Fact]
    public void Excel_export_writes_default_project_and_company_headers()
    {
        var matrix = PnLByProjectMatrix.From(
        [
            Company("Miracle",
                Line("51000", "OTHER INCOME", "----", 100m),
                Line("51000", "OTHER INCOME", "Project B", 200m)),
            Company("On Yat",
                Line("51000", "OTHER INCOME", "----", 400m))
        ]);

        using var workbook = new XLWorkbook(new MemoryStream(PnLByProjectExcel.Build(matrix)));
        var sheet = workbook.Worksheet("P&L by Project");

        Assert.Equal("Miracle", sheet.Cell(1, 3).GetString());
        Assert.Equal("On Yat", sheet.Cell(1, 5).GetString());
        Assert.Equal("----", sheet.Cell(2, 3).GetString());
        Assert.Equal("Project B", sheet.Cell(2, 4).GetString());
        Assert.Equal("----", sheet.Cell(2, 5).GetString());
        Assert.Equal(100m, sheet.Cell(3, 3).GetValue<decimal>());
        Assert.Equal(200m, sheet.Cell(3, 4).GetValue<decimal>());
        Assert.Equal(400m, sheet.Cell(3, 5).GetValue<decimal>());
        Assert.Equal(700m, sheet.Cell(3, 6).GetValue<decimal>());
    }

    [Fact]
    public void Excel_export_separates_company_groups_with_merged_headers_and_borders()
    {
        var matrix = PnLByProjectMatrix.From(
        [
            Company("Miracle",
                Line("51000", "OTHER INCOME", "----", 100m),
                Line("51000", "OTHER INCOME", "Project B", 200m)),
            Company("On Yat",
                Line("51000", "OTHER INCOME", "----", 400m))
        ]);

        using var workbook = new XLWorkbook(new MemoryStream(PnLByProjectExcel.Build(matrix)));
        var sheet = workbook.Worksheet("P&L by Project");

        // Miracle spans columns 3-4; On Yat is column 5; TOTAL is column 6.
        Assert.True(sheet.Cell(1, 3).IsMerged());
        Assert.Equal("Miracle", sheet.Range(1, 3, 1, 4).FirstCell().GetString());
        Assert.Equal("On Yat", sheet.Cell(1, 5).GetString());
        Assert.Equal(XLAlignmentHorizontalValues.Center, sheet.Cell(1, 3).Style.Alignment.Horizontal);
        Assert.Equal(XLAlignmentHorizontalValues.Center, sheet.Cell(2, 3).Style.Alignment.Horizontal);

        // Horizontal border between company and project header rows.
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(1, 3).Style.Border.BottomBorder);
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(2, 3).Style.Border.TopBorder);

        // Vertical border on the right edge of Miracle's last project column (through header + data + TOTAL).
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(1, 4).Style.Border.RightBorder);
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(2, 4).Style.Border.RightBorder);
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(3, 4).Style.Border.RightBorder);
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(4, 4).Style.Border.RightBorder);

        // Vertical border before TOTAL AMOUNT.
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(1, 6).Style.Border.LeftBorder);
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(3, 6).Style.Border.LeftBorder);
        Assert.Equal(XLBorderStyleValues.Medium, sheet.Cell(4, 6).Style.Border.LeftBorder);
    }

    private static DashboardDetailRow Company(string name, params GlTransLine[] lines) => new()
    {
        DatabaseName = name,
        Records = lines
    };

    private static GlTransLine Line(string code, string description, string project, decimal amount) => new()
    {
        AccountCode = code,
        AccountDescription = description,
        Project = project,
        Amount = amount
    };
}
