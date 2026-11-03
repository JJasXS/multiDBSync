namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Compatibility status: local SQLSDK.chm and/or official wiki SDK Live samples.
/// </summary>
public enum SdkDocStatus
{
    /// <summary>Documented with working sample in local SQLSDK.chm.</summary>
    VerifiedInLocalChm,

    /// <summary>Official wiki SDK Live sample documents BizObject + fields (used for write adapter).</summary>
    VerifiedInOfficialWiki,

    /// <summary>BizObject name appears on wiki / CHM narrative, but required fields are not in a create/edit sample.</summary>
    NameOnlyUnverifiedFields,

    /// <summary>Not found in local CHM or wiki write samples; do not implement.</summary>
    NotInLocalChm
}

public sealed record SdkCapability(
    string SyncFeature,
    string? RestPathOrEntity,
    string? LikelyBizObject,
    SdkDocStatus Status,
    string Notes);

public static class SdkBizObjectCatalog
{
    public const string Agent = "Agent";
    public const string SalesInvoice = "SL_IV";
    public const string CustomerPayment = "AR_PM";
    public const string GlReceiptVoucher = "GL_CB";
    public const string Customer = "AR_CUSTOMER";
    public const string Supplier = "AP_SUPPLIER";
    public const string StockItem = "ST_ITEM";
    public const string PurchaseInvoice = "PH_PI";
    public const string PurchaseReturn = "PH_SC";
    public const string StockTransfer = "ST_XF";
    public const string SalesCreditNote = "SL_CN";
    public const string SalesDebitNote = "SL_DN";

    public static IReadOnlyList<SdkCapability> Matrix { get; } =
    [
        new("Customer sync", "CUSTOMER /customer", Customer, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki AR_CUSTOMER New/Edit/Delete + cdsBranch; CHM reads CompanyName via Params+Open."),
        new("Supplier sync", "SUPPLIER /supplier", Supplier, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki linking name AP_SUPPLIER; field pattern mirrors AR_CUSTOMER maintain supplier docs."),
        new("GL Account sync", "ACCOUNT /account", "GL_ACC", SdkDocStatus.VerifiedInOfficialWiki,
            "COM metadata confirms GL_ACC MainDataSet fields (DOCKEY/PARENT/CODE/DESCRIPTION/…). " +
            "Sdk adapter implements New/Edit/Save/Delete using destination Parent DocKey. Live CUD lab still required before Provider=Sdk."),
        new("Stock Item", "ITEM /stockitem", StockItem, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki ST_ITEM Add/Edit/Delete with cdsUOM (+ optional cdsBarcode)."),
        new("Stock Group", "/stockgroup", "ST_GROUP", SdkDocStatus.VerifiedInOfficialWiki,
            "COM metadata confirms ST_GROUP MainDataSet (not ST_CATEGORY). Sdk adapter creates missing groups for ItemPrerequisiteService."),
        new("SI → PI (dest write)", "/purchaseinvoice", PurchaseInvoice, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki PH_PI PostData with MainDataSet + cdsDocDetail."),
        new("DO → GR (dest write)", "/goodsreceived", "PH_GR", SdkDocStatus.VerifiedInOfficialWiki,
            "COM metadata confirms PH_GR MainDataSet + cdsDocDetail + cdsSerialNumber (ReceiveQty/FROM*). " +
            "Sdk adapter creates GR; does not reuse source-company DocKeys for FROM* links. Live CUD lab still required."),
        new("ST → ST (dest write)", "/stocktransfer", StockTransfer, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki ST_XF Add/Edit/Delete with FromLocation/ToLocation detail fields."),
        new("CN → PR (dest write)", "/purchasereturned", PurchaseReturn, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki linking: PH_SC = Purchase Return; document shape follows PH_PI."),
        new("CN → DN (dest write)", "/salesdebitnote", SalesDebitNote, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki linking: SL_DN; document shape follows SL_IV/PH_PI detail pattern."),
        new("DN → CN (dest write)", "/salescreditnote", SalesCreditNote, SdkDocStatus.VerifiedInOfficialWiki,
            "Wiki linking + samples: SL_CN."),
        new("COM Login", "n/a", "SQLAcc.BizApp", SdkDocStatus.VerifiedInLocalChm,
            "Login(UserName, Password, DCF Path, File Name); IsLogin; CreateObject SQLAcc.BizApp."),
        new("COM Logout", "n/a", "SQLAcc.BizApp", SdkDocStatus.VerifiedInOfficialWiki,
            "Logout on official wiki samples — used for multi-company switch."),
        new("Agent (sample only)", "n/a", Agent, SdkDocStatus.VerifiedInLocalChm,
            "Full New/Edit/Delete/Save/Close sample in local CHM (POC only)."),
        new("Sales Invoice sample", "n/a", SalesInvoice, SdkDocStatus.VerifiedInLocalChm,
            "Full add/edit/delete in local CHM — not the dest document for SI→PI sync."),
        new("Customer Payment sample", "n/a", CustomerPayment, SdkDocStatus.VerifiedInLocalChm,
            "Local CHM AR_PM samples."),
        new("GL Cash Book sample", "n/a", GlReceiptVoucher, SdkDocStatus.VerifiedInLocalChm,
            "Local CHM GL_CB samples."),
    ];

    public static string UnsupportedMessage(string operation, string detail) =>
        $"SDK write '{operation}' is not enabled: {detail} " +
        "REST remains available via SqlAccountingWrite:Provider=Rest until this mapping is verified.";
}
