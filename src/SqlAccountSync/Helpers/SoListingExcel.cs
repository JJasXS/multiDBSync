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
        "Service Charge",
        "TOTAL AMOUNT"
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
            WriteAmount(sheet.Cell(excelRow, 24), RowAmount(row));
        }

        if (rows.Count > 0)
        {
            var totalRow = rows.Count + 2;
            var totals = new decimal[7];
            foreach (var row in rows)
            {
                totals[0] += Amount(row.SecurityDeposit);
                totals[1] += Amount(row.UtilitiesDeposit);
                totals[2] += Amount(row.FitOutDeposits);
                totals[3] += Amount(row.AccessCard);
                totals[4] += Amount(row.BookingDeposits);
                totals[5] += Amount(row.MonthlyRental);
                totals[6] += Amount(row.ServiceCharge);
            }

            var label = sheet.Cell(totalRow, 1);
            label.Value = "TOTAL";
            label.Style.Font.Bold = true;
            WriteTotal(sheet.Cell(totalRow, 6), totals[0]);
            WriteTotal(sheet.Cell(totalRow, 7), totals[1]);
            WriteTotal(sheet.Cell(totalRow, 8), totals[2]);
            WriteTotal(sheet.Cell(totalRow, 9), totals[3]);
            WriteTotal(sheet.Cell(totalRow, 10), totals[4]);
            WriteTotal(sheet.Cell(totalRow, 22), totals[5]);
            WriteTotal(sheet.Cell(totalRow, 23), totals[6]);
            WriteTotal(sheet.Cell(totalRow, 24), totals.Sum());
        }

        sheet.Columns().AdjustToContents(1, 40);
        sheet.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static decimal Amount(decimal? amount) => amount ?? 0m;

    private static decimal RowAmount(SoListingRow row) =>
        Amount(row.SecurityDeposit)
        + Amount(row.UtilitiesDeposit)
        + Amount(row.FitOutDeposits)
        + Amount(row.AccessCard)
        + Amount(row.BookingDeposits)
        + Amount(row.MonthlyRental)
        + Amount(row.ServiceCharge);

    private static void WriteAmount(IXLCell cell, decimal? amount)
    {
        if (amount is not decimal value)
            return;
        WriteAmount(cell, value);
    }

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

    private static void WriteDate(IXLCell cell, DateTime? value)
    {
        if (value is not DateTime date)
            return;
        cell.Value = date.Date;
        cell.Style.NumberFormat.Format = "dd-MM-yyyy";
    }
}
