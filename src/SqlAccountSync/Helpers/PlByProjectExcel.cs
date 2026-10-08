using ClosedXML.Excel;

namespace SqlAccountSync.Helpers;

public static class PlByProjectExcel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Build(PlByProjectMatrix matrix)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("PL by Project");
        var companyCount = matrix.Companies.Count;
        var totalColumn = 3 + companyCount;

        sheet.Cell(1, 1).Value = "CODE";
        sheet.Cell(1, 2).Value = "ACCOUNT DESCRIPTION";
        for (var companyIndex = 0; companyIndex < companyCount; companyIndex++)
            sheet.Cell(1, 3 + companyIndex).Value = matrix.Companies[companyIndex];
        sheet.Cell(1, totalColumn).Value = "TOTAL AMOUNT";

        var header = sheet.Range(1, 1, 1, totalColumn);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        for (var rowIndex = 0; rowIndex < matrix.Rows.Count; rowIndex++)
        {
            var row = matrix.Rows[rowIndex];
            var excelRow = rowIndex + 2;
            sheet.Cell(excelRow, 1).Value = row.Code;
            sheet.Cell(excelRow, 2).Value = row.Description;
            for (var companyIndex = 0; companyIndex < companyCount; companyIndex++)
                WriteAmount(sheet.Cell(excelRow, 3 + companyIndex), row.Amounts[companyIndex]);
            WriteAmount(sheet.Cell(excelRow, totalColumn), row.Total);
        }

        if (matrix.Rows.Count > 0)
        {
            var columnTotals = new decimal[companyCount];
            decimal grandTotal = 0m;
            foreach (var row in matrix.Rows)
            {
                for (var companyIndex = 0; companyIndex < companyCount; companyIndex++)
                    columnTotals[companyIndex] += row.Amounts[companyIndex];
                grandTotal += row.Total;
            }

            var totalRow = matrix.Rows.Count + 2;
            var totalLabel = sheet.Cell(totalRow, 1);
            totalLabel.Value = "TOTAL";
            totalLabel.Style.Font.Bold = true;
            totalLabel.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            for (var companyIndex = 0; companyIndex < companyCount; companyIndex++)
                WriteTotal(sheet.Cell(totalRow, 3 + companyIndex), columnTotals[companyIndex]);
            WriteTotal(sheet.Cell(totalRow, totalColumn), grandTotal);
        }

        sheet.Column(1).Width = 16;
        sheet.Column(2).Width = 36;
        for (var column = 3; column <= totalColumn; column++)
            sheet.Column(column).Width = 18;
        sheet.SheetView.FreezeRows(1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
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
