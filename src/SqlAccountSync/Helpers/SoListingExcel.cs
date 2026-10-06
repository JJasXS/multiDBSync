using ClosedXML.Excel;
using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

public static class SoListingExcel
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Headers =
    [
        "COMPANY",
        "Properties",
        "Type",
        "Project Code",
        "SO No",
        "Security Deposit",
        "Utilities Deposit",
        "Fit Out Deposits",
        "Access Card",
        "Booking Deposits",
        "Tenant Name",
        "Trade Name",
        "Registration No",
        "TIN",
        "Address",
        "Postal Code",
        "City",
        "Email",
        "Contact No",
        "Tenant Start",
        "Tenant End",
        "Monthly Rental",
        "Service Charge"
    ];

    public static byte[] Build(IReadOnlyList<SoListingRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("SO Listing");
        for (var column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];

        var header = sheet.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EEF5");
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var excelRow = rowIndex + 2;
            sheet.Cell(excelRow, 1).Value = row.Company;
            sheet.Cell(excelRow, 2).Value = row.Properties;
            sheet.Cell(excelRow, 3).Value = row.Type;
            sheet.Cell(excelRow, 4).Value = row.ProjectCode;
            sheet.Cell(excelRow, 5).Value = row.SoNo;
            WriteAmount(sheet.Cell(excelRow, 6), row.SecurityDeposit);
            WriteAmount(sheet.Cell(excelRow, 7), row.UtilitiesDeposit);
            WriteAmount(sheet.Cell(excelRow, 8), row.FitOutDeposits);
            WriteAmount(sheet.Cell(excelRow, 9), row.AccessCard);
            WriteAmount(sheet.Cell(excelRow, 10), row.BookingDeposits);
            sheet.Cell(excelRow, 11).Value = row.TenantName;
            sheet.Cell(excelRow, 12).Value = row.TradeName;
            sheet.Cell(excelRow, 13).Value = row.RegistrationNo;
            sheet.Cell(excelRow, 14).Value = row.Tin;
            sheet.Cell(excelRow, 15).Value = row.Address;
            sheet.Cell(excelRow, 16).Value = row.PostalCode;
            sheet.Cell(excelRow, 17).Value = row.City;
            sheet.Cell(excelRow, 18).Value = row.Email;
            sheet.Cell(excelRow, 19).Value = row.ContactNo;
            WriteDate(sheet.Cell(excelRow, 20), row.TenantStart);
            WriteDate(sheet.Cell(excelRow, 21), row.TenantEnd);
            WriteAmount(sheet.Cell(excelRow, 22), row.MonthlyRental);
            WriteAmount(sheet.Cell(excelRow, 23), row.ServiceCharge);
        }

        sheet.Columns().AdjustToContents(1, 40);
        sheet.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteAmount(IXLCell cell, decimal? amount)
    {
        if (amount is not decimal value)
            return;
        cell.Value = value;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }

    private static void WriteDate(IXLCell cell, DateTime? value)
    {
        if (value is not DateTime date)
            return;
        cell.Value = date.Date;
        cell.Style.NumberFormat.Format = "dd-MM-yyyy";
    }
}
