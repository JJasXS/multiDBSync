namespace SqlAccountSync.Models;

public static class JobStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Retrying = "RETRYING";
    public const string Cancelled = "CANCELLED";
    public const string PartiallyCompleted = "PARTIALLY_COMPLETED";
    public const string WaitingForMapping = "WAITING_FOR_MAPPING";
    public const string Conflict = "CONFLICT";
}

public static class MappingStatuses
{
    public const string Mapped = "MAPPED";
    public const string Unmapped = "UNMAPPED";
    public const string Conflict = "CONFLICT";
    public const string Disabled = "DISABLED";
}

public static class DatabaseRoles
{
    public const string Master = "MASTER";
    public const string Sub = "SUB";
    public const string Both = "BOTH";
}

public static class AppEnvironments
{
    public const string Development = "DEVELOPMENT";
    public const string Testing = "TESTING";
    public const string Production = "PRODUCTION";
}

public static class ConnectionResults
{
    public const string Online = "ONLINE";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string DatabaseNotFound = "DATABASE_NOT_FOUND";
    public const string NetworkError = "NETWORK_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string UnsupportedSchema = "UNSUPPORTED_SCHEMA";
    public const string UnknownError = "UNKNOWN_ERROR";
    public const string Offline = "OFFLINE";
    public const string Disabled = "DISABLED";
}

public static class EntityCodes
{
    public const string Item = "ITEM";
    public const string Customer = "CUSTOMER";
    public const string Supplier = "SUPPLIER";
    public const string Account = "ACCOUNT";
    public const string TaxCode = "TAXCODE";
    public const string Warehouse = "WAREHOUSE";
    public const string Uom = "UOM";
    public const string ItemCategory = "ITEMCATEGORY";
    public const string Pricing = "PRICING";
}

public static class TxnTypeCodes
{
    public const string SalesToPurchase = "SI_TO_PI";
    public const string DeliveryToGr = "DO_TO_GR";
    public const string StockTransfer = "ST_TO_ST";
    public const string SalesReturnToPr = "SR_TO_PR";
    public const string CreditToPurchaseReturn = "CN_TO_PR";
    public const string CreditToDebit = "CN_TO_DN";
    public const string DebitToCredit = "DN_TO_CN";

    /// <summary>Transfer types with a real destination mapper (not stub).</summary>
    public static bool IsImplemented(string? code) =>
        string.Equals(code, SalesToPurchase, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, DeliveryToGr, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, StockTransfer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, CreditToPurchaseReturn, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, CreditToDebit, StringComparison.OrdinalIgnoreCase)
        || string.Equals(code, DebitToCredit, StringComparison.OrdinalIgnoreCase);
}
