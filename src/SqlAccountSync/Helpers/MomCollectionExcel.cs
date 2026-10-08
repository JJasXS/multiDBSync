using ClosedXML.Excel;
using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

public static class MomCollectionExcel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Months =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    ];

    public static byte[] Build(IReadOnlyList<DashboardDetailRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Collection Report");
        var columnCount = 5 + (Months.Length * 2) + 2;
        var identity = new[] { "PROJECT", "ENTITIES", "PROPERTIES", "TYPE", "PROJECT CODE" };
        for (var column = 0; column < identity.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = identity[column];
            sheet.Range(1, column + 1, 2, column + 1).Merge();
        }

        for (var month = 0; month < Months.Length; month++)
        {
            var start = 6 + (month * 2);
            sheet.Cell(1, start).Value = Months[month];
            sheet.Range(1, start, 1, start + 1).Merge();
            sheet.Cell(2, start).Value = "Invoice";
            sheet.Cell(2, start + 1).Value = "Receipt";
        }

        var totalStart = 6 + (Months.Length * 2);
        sheet.Cell(1, totalStart).Value = "Total";
        sheet.Range(1, totalStart, 1, totalStart + 1).Merge();
        sheet.Cell(2, totalStart).Value = "Invoice";
        sheet.Cell(2, totalStart + 1).Value = "Receipt";

        var header = sheet.Range(1, 1, 2, columnCount);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        var invoiceTotals = new decimal[Months.Length];
        var receiptTotals = new decimal[Months.Length];
        var excelRow = 3;
        var exportedLines = 0;
        foreach (var company in rows)
        {
            if (company.LoadFailed)
                continue;

            foreach (var line in company.CollectionLines)
            {
                sheet.Cell(excelRow, 1).Value = company.DatabaseName;
                sheet.Cell(excelRow, 2).Value = line.Entities;
                sheet.Cell(excelRow, 3).Value = line.Properties;
                sheet.Cell(excelRow, 4).Value = line.Type;
                sheet.Cell(excelRow, 5).Value = line.ProjectCode;
                decimal rowInvoice = 0m;
                decimal rowReceipt = 0m;
                for (var month = 0; month < Months.Length; month++)
                {
                    var entries = line.ReceiptsByMonth[month].SelectMany(day => day.Receipts).ToList();
                    var invoice = entries.Count > 0 ? entries.Sum(item => item.InvoiceAmount) : 0m;
                    var receipt = entries.Count > 0 ? entries.Sum(item => item.Amount) : 0m;
                    invoiceTotals[month] += invoice;
                    receiptTotals[month] += receipt;
                    rowInvoice += invoice;
                    rowReceipt += receipt;
                    if (entries.Count == 0)
                        continue;
                    WriteAmount(sheet.Cell(excelRow, 6 + (month * 2)), invoice);
                    WriteAmount(sheet.Cell(excelRow, 7 + (month * 2)), receipt);
                }
                WriteAmount(sheet.Cell(excelRow, totalStart), rowInvoice);
                WriteAmount(sheet.Cell(excelRow, totalStart + 1), rowReceipt);
                exportedLines++;
                excelRow++;
            }
        }

        if (exportedLines > 0)
        {
            decimal grandInvoice = 0m;
            decimal grandReceipt = 0m;
            sheet.Cell(excelRow, 1).Value = "TOTAL";
            sheet.Cell(excelRow, 1).Style.Font.Bold = true;
            for (var month = 0; month < Months.Length; month++)
            {
                grandInvoice += invoiceTotals[month];
                grandReceipt += receiptTotals[month];
                WriteTotal(sheet.Cell(excelRow, 6 + (month * 2)), invoiceTotals[month]);
                WriteTotal(sheet.Cell(excelRow, 7 + (month * 2)), receiptTotals[month]);
            }
            WriteTotal(sheet.Cell(excelRow, totalStart), grandInvoice);
            WriteTotal(sheet.Cell(excelRow, totalStart + 1), grandReceipt);
            var lastDataRow = excelRow - 1;
            sheet.Range(3, totalStart, lastDataRow, totalStart + 1).Style.Fill.BackgroundColor = RowTotalFill;
            var totalRange = sheet.Range(excelRow, 1, excelRow, columnCount);
            totalRange.Style.Fill.BackgroundColor = BottomTotalFill;
            totalRange.Style.Font.Bold = true;
            totalRange.Style.Border.TopBorder = XLBorderStyleValues.Medium;
            totalRange.Style.Border.TopBorderColor = TotalBorder;
            var grand = sheet.Range(excelRow, totalStart, excelRow, totalStart + 1);
            grand.Style.Fill.BackgroundColor = GrandTotalFill;
            grand.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            grand.Style.Border.OutsideBorderColor = TotalBorder;
        }

        var lastFilterRow = exportedLines > 0 ? excelRow - 1 : Math.Max(excelRow - 1, 2);
        sheet.Range(2, 1, lastFilterRow, columnCount).SetAutoFilter();
        sheet.SheetView.FreezeRows(2);
        sheet.Columns().AdjustToContents(1, 40);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static readonly XLColor RowTotalFill = XLColor.FromHtml("#F4F8FB");
    private static readonly XLColor BottomTotalFill = XLColor.FromHtml("#E6EEF5");
    private static readonly XLColor GrandTotalFill = XLColor.FromHtml("#D5E4F0");
    private static readonly XLColor TotalBorder = XLColor.FromHtml("#8AA8BC");

    private static void WriteAmount(IXLCell cell, decimal amount)
    {
        cell.Value = amount;
        cell.Style.NumberFormat.Format = "#,##0.00";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void WriteTotal(IXLCell cell, decimal amount)
    {
        WriteAmount(cell, amount);
        cell.Style.Font.Bold = true;
    }
}
