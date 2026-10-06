namespace SqlAccountSync.Models;

public sealed class SoListingRow
{
    public string Company { get; set; } = "";
    public string Properties { get; init; } = "";
    public string Type { get; init; } = "";
    public string ProjectCode { get; init; } = "";
    public string SoNo { get; init; } = "";
    public decimal? SecurityDeposit { get; init; }
    public decimal? UtilitiesDeposit { get; init; }
    public decimal? FitOutDeposits { get; init; }
    public decimal? AccessCard { get; init; }
    public decimal? BookingDeposits { get; init; }
    public string TenantName { get; init; } = "";
    public string TradeName { get; init; } = "";
    public string RegistrationNo { get; init; } = "";
    public string Tin { get; init; } = "";
    public string Address { get; init; } = "";
    public string PostalCode { get; init; } = "";
    public string City { get; init; } = "";
    public string Email { get; init; } = "";
    public string ContactNo { get; init; } = "";
    public DateTime? TenantStart { get; init; }
    public DateTime? TenantEnd { get; init; }
    public decimal? MonthlyRental { get; init; }
    public decimal? ServiceCharge { get; init; }
}

public sealed class SoListingCompanyOption
{
    public int DbId { get; init; }
    public string Label { get; init; } = "";
}

public sealed class SoListingDetails
{
    public IReadOnlyList<SoListingRow> Rows { get; init; } = [];
    public int CompanyId { get; init; }
    public IReadOnlyList<SoListingCompanyOption> Companies { get; init; } = [];
    public string FilterColumn { get; init; } = "";
    public string FilterValue { get; init; } = "";
    public bool DatabaseAvailable { get; init; }
    public string ErrorMessage { get; init; } = "";
    public IReadOnlyList<string> ReadFailures { get; init; } = [];
}
