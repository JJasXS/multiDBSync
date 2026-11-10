using ClosedXML.Excel;

namespace SqlAccountSync.Helpers;

public static class PnLByProjectExcel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Build(PnLByProjectMatrix matrix)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("P&L by Project");
        var projectCount = matrix.ProjectColumnCount;
        var totalColumn = 3 + projectCount;
        var lastRow = matrix.Rows.Count > 0 ? matrix.Rows.Count + 3 : 2;

        sheet.Cell(1, 1).Value = "CODE";
        sheet.Cell(1, 2).Value = "ACCOUNT DESCRIPTION";
        sheet.Range(1, 1, 2, 1).Merge();
        sheet.Range(1, 2, 2, 2).Merge();

        // Track each company's first/last project column so group borders stay dynamic.
        var companyRanges = new List<(int FirstColumn, int LastColumn)>(matrix.Companies.Count);
        var column = 3;
        foreach (var company in matrix.Companies)
        {
            var span = Math.Max(1, company.Projects.Count);
            var firstColumn = column;
            var lastColumn = column + span - 1;
            companyRanges.Add((firstColumn, lastColumn));

            sheet.Cell(1, firstColumn).Value = company.Name;
            sheet.Range(1, firstColumn, 1, lastColumn).Merge();
            for (var projectIndex = 0; projectIndex < company.Projects.Count; projectIndex++)
                sheet.Cell(2, firstColumn + projectIndex).Value = company.Projects[projectIndex];

            column += span;
        }

        sheet.Cell(1, totalColumn).Value = "TOTAL AMOUNT";
        sheet.Range(1, totalColumn, 2, totalColumn).Merge();

        var header = sheet.Range(1, 1, 2, totalColumn);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        // Horizontal rule between company names (row 1) and project names (row 2).
        sheet.Range(1, 1, 1, totalColumn).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        sheet.Range(1, 1, 1, totalColumn).Style.Border.BottomBorderColor = GroupBorder;
        sheet.Range(2, 1, 2, totalColumn).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        sheet.Range(2, 1, 2, totalColumn).Style.Border.TopBorderColor = GroupBorder;

        for (var rowIndex = 0; rowIndex < matrix.Rows.Count; rowIndex++)
        {
            var row = matrix.Rows[rowIndex];
            var excelRow = rowIndex + 3;
            sheet.Cell(excelRow, 1).Value = row.Code;
            sheet.Cell(excelRow, 2).Value = row.Description;
            for (var amountIndex = 0; amountIndex < projectCount; amountIndex++)
                WriteAmount(sheet.Cell(excelRow, 3 + amountIndex), row.Amounts[amountIndex]);
            WriteAmount(sheet.Cell(excelRow, totalColumn), row.Total);
        }

        if (matrix.Rows.Count > 0)
        {
            var columnTotals = new decimal[projectCount];
            decimal grandTotal = 0m;
            foreach (var row in matrix.Rows)
            {
                for (var amountIndex = 0; amountIndex < projectCount; amountIndex++)
                    columnTotals[amountIndex] += row.Amounts[amountIndex];
                grandTotal += row.Total;
            }

            var totalRow = matrix.Rows.Count + 3;
            var totalLabel = sheet.Cell(totalRow, 1);
            totalLabel.Value = "TOTAL";
            totalLabel.Style.Font.Bold = true;
            totalLabel.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Range(totalRow, 1, totalRow, 2).Merge();
            for (var amountIndex = 0; amountIndex < projectCount; amountIndex++)
                WriteTotal(sheet.Cell(totalRow, 3 + amountIndex), columnTotals[amountIndex]);
            WriteTotal(sheet.Cell(totalRow, totalColumn), grandTotal);
            var lastDataRow = totalRow - 1;
            sheet.Range(3, totalColumn, lastDataRow, totalColumn).Style.Fill.BackgroundColor = RowTotalFill;
            var totalRange = sheet.Range(totalRow, 1, totalRow, totalColumn);
            totalRange.Style.Fill.BackgroundColor = BottomTotalFill;
            totalRange.Style.Font.Bold = true;
            totalRange.Style.Border.TopBorder = XLBorderStyleValues.Medium;
            totalRange.Style.Border.TopBorderColor = TotalBorder;
            var grand = sheet.Cell(totalRow, totalColumn);
            grand.Style.Fill.BackgroundColor = GrandTotalFill;
            grand.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            grand.Style.Border.OutsideBorderColor = TotalBorder;
        }

        ApplyCompanyGroupBorders(sheet, companyRanges, lastRow, totalColumn);

        sheet.Column(1).Width = 16;
        sheet.Column(2).Width = 36;
        for (var excelColumn = 3; excelColumn <= totalColumn; excelColumn++)
            sheet.Column(excelColumn).Width = 14;
        sheet.SheetView.FreezeRows(2);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static readonly XLColor RowTotalFill = XLColor.FromHtml("#F4F8FB");
    private static readonly XLColor BottomTotalFill = XLColor.FromHtml("#E6EEF5");
    private static readonly XLColor GrandTotalFill = XLColor.FromHtml("#D5E4F0");
    private static readonly XLColor TotalBorder = XLColor.FromHtml("#8AA8BC");
    private static readonly XLColor GroupBorder = XLColor.FromHtml("#8AA8BC");

    /// <summary>
    /// Draws a medium vertical border on the right edge of each company group
    /// (and before TOTAL AMOUNT), through header, data, and TOTAL rows.
    /// </summary>
    private static void ApplyCompanyGroupBorders(
        IXLWorksheet sheet,
        IReadOnlyList<(int FirstColumn, int LastColumn)> companyRanges,
        int lastRow,
        int totalColumn)
    {
        for (var i = 0; i < companyRanges.Count; i++)
        {
            var lastColumn = companyRanges[i].LastColumn;
            var edge = sheet.Range(1, lastColumn, lastRow, lastColumn);
            edge.Style.Border.RightBorder = XLBorderStyleValues.Medium;
            edge.Style.Border.RightBorderColor = GroupBorder;
        }

        // Separate the last company group from TOTAL AMOUNT.
        if (totalColumn > 3)
        {
            var beforeTotal = sheet.Range(1, totalColumn, lastRow, totalColumn);
            beforeTotal.Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            beforeTotal.Style.Border.LeftBorderColor = GroupBorder;
        }
    }

    private static void WriteAmount(IXLCell cell, decimal amount)
    {
        cell.Value = amount;
        cell.Style.NumberFormat.Format = "#,##0.00";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void WriteTotal(IXLCell cell, decimal amount)
    {
        WriteAmount(cell, amount);
        cell.Style.Font.Bold = true;
    }
}
