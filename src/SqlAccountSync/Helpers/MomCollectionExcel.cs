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
        var headers = new List<string> { "Project", "Entities", "Properties", "Type", "Project Code" };
        foreach (var month in Months)
        {
            headers.Add($"{month} Invoice");
            headers.Add($"{month} Receipt");
        }

        for (var column = 0; column < headers.Count; column++)
            sheet.Cell(1, column + 1).Value = headers[column];

        var header = sheet.Range(1, 1, 1, headers.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var excelRow = 2;
        foreach (var company in rows)
        {
            if (company.LoadFailed)
            {
                sheet.Cell(excelRow, 1).Value = company.DatabaseName;
                sheet.Cell(excelRow, 2).Value = "Could not load this company.";
                excelRow++;
                continue;
            }

            foreach (var line in company.CollectionLines)
            {
                sheet.Cell(excelRow, 1).Value = company.DatabaseName;
                sheet.Cell(excelRow, 2).Value = line.Entities;
                sheet.Cell(excelRow, 3).Value = line.Properties;
                sheet.Cell(excelRow, 4).Value = line.Type;
                sheet.Cell(excelRow, 5).Value = line.ProjectCode;
                for (var month = 0; month < Months.Length; month++)
                {
                    var entries = line.ReceiptsByMonth[month].SelectMany(day => day.Receipts).ToList();
                    if (entries.Count == 0)
                        continue;
                    WriteAmount(sheet.Cell(excelRow, 6 + (month * 2)), entries.Sum(item => item.InvoiceAmount));
                    WriteAmount(sheet.Cell(excelRow, 7 + (month * 2)), entries.Sum(item => item.Amount));
                }
                excelRow++;
            }
        }

        sheet.Range(1, 1, Math.Max(excelRow - 1, 1), headers.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 40);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteAmount(IXLCell cell, decimal amount)
    {
        cell.Value = amount;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }
}
