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

    private static readonly string[] Headers =
    [
        "Database",
        "Company",
        "Properties",
        "Type",
        "Project Code",
        "Date",
        "Month",
        "Invoice",
        "Receipt"
    ];

    public static byte[] Build(IReadOnlyList<DashboardDetailRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Mom Collection");
        for (var column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];

        var header = sheet.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var excelRow = 2;
        var notLoaded = new List<string>();
        foreach (var company in rows)
        {
            if (company.LoadFailed)
            {
                notLoaded.Add(company.DatabaseName);
                continue;
            }

            foreach (var line in company.CollectionLines)
            {
                for (var month = 0; month < Months.Length; month++)
                {
                    var invoices = line.ReceiptsByMonth[month]
                        .OrderBy(day => day.Date)
                        .SelectMany(day => day.Receipts.Select(receipt => (day.Date, receipt.Amount)))
                        .ToList();
                    if (invoices.Count == 0)
                        continue;

                    var receipt = invoices.Sum(item => item.Amount);
                    for (var index = 0; index < invoices.Count; index++)
                    {
                        var invoice = invoices[index];
                        sheet.Cell(excelRow, 1).Value = company.DatabaseName;
                        sheet.Cell(excelRow, 2).Value = line.Entities;
                        sheet.Cell(excelRow, 3).Value = line.Properties;
                        sheet.Cell(excelRow, 4).Value = line.Type;
                        sheet.Cell(excelRow, 5).Value = line.ProjectCode;
                        WriteDate(sheet.Cell(excelRow, 6), invoice.Date);
                        sheet.Cell(excelRow, 7).Value = Months[month];
                        WriteAmount(sheet.Cell(excelRow, 8), invoice.Amount);
                        if (index == 0)
                            WriteAmount(sheet.Cell(excelRow, 9), receipt);
                        excelRow++;
                    }
                }
            }
        }

        sheet.Range(1, 1, Math.Max(excelRow - 1, 1), Headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 40);

        if (notLoaded.Count > 0)
        {
            var notes = workbook.Worksheets.Add("Not loaded");
            notes.Cell(1, 1).Value = "Database";
            notes.Cell(1, 1).Style.Font.Bold = true;
            for (var index = 0; index < notLoaded.Count; index++)
                notes.Cell(index + 2, 1).Value = notLoaded[index];
            notes.Columns().AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteAmount(IXLCell cell, decimal amount)
    {
        cell.Value = amount;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }

    private static void WriteDate(IXLCell cell, DateTime date)
    {
        cell.Value = date.Date;
        cell.Style.DateFormat.Format = "yyyy-mm-dd";
    }
}
