namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Paid SQL Accounting REST (api.sql.my / SigV4) call sites that must stay isolated when
/// Provider=Sdk. Keep in sync with code; covered by unit tests.
/// </summary>
public static class RestDependencyCatalog
{
    public sealed record RestDependency(
        string Id,
        string CallSite,
        string RestPathOrAction,
        string WhyStillRequired,
        string EliminationGate);

    public static IReadOnlyList<RestDependency> All { get; } =
    [
        new("write-adapter-default",
            "Program.cs → SqlAccountApiWriteAdapter (Provider=Rest)",
            "POST/PUT/DELETE /customer|/supplier|/stockitem|/account + document paths",
            "Production default write path until Provider=Sdk is approved. Refuses calls when Provider=Sdk.",
            "Keep as rollback; Development uses Provider=Sdk → SqlAccountSdkWriteAdapter."),

        new("accounts-controller-create",
            "AccountsController.CreateAsync → ISqlAccountWriteAdapter",
            "POST /account (Rest) or GL_ACC (Sdk)",
            "Converted: routed through write adapter.",
            "No further change; Sdk adapter handles GL_ACC."),

        new("accounts-controller-list-api",
            "AccountsController.ListAsync?source=api",
            "GET /account/*",
            "Not required by Website: no Pages/wwwroot callers. Chart UI/Sync/Push use Firebird GetGlChartAsync. Isolated/refused when Provider=Sdk.",
            "Keep endpoint for Rest diagnostics only; Website does not depend on source=api."),

        new("item-prereq-stockgroup",
            "ItemPrerequisiteService.Ensure* → ISqlAccountWriteAdapter.CreateStockGroupAsync",
            "POST /stockgroup (Rest) or ST_GROUP (Sdk)",
            "Converted: Sdk uses Firebird RO existence + ST_GROUP (no REST list when Provider=Sdk).",
            "No further change."),

        new("item-prereq-uom",
            "ItemPrerequisiteService.Ensure*",
            "GET/POST /uom (Rest only)",
            "Not required by Website: item push/doc ensure create ST_ITEM with cdsUOM. Rest /uom was best-effort and often unavailable. Sdk skips REST UOM.",
            "No standalone UOM BizObject; cdsUOM on item create is the Website path."),

        new("txn-goodsreceived",
            "TransactionEngine → CreateDocumentAsync",
            "POST document paths (Rest) or PH_GR/PH_PI/ST_XF/PH_SC/SL_CN/SL_DN (Sdk)",
            "Converted: dest document writes go through ISqlAccountWriteAdapter.",
            "No further change for supported six transfer types."),

        new("api-write-account-party-item-docs",
            "SqlAccountApiWriteAdapter (when Provider=Rest)",
            "All mapped master/document REST paths",
            "Rollback path; registered alongside Sdk. Refuses when Provider=Sdk.",
            "Keep until cutover; unused under Sdk."),

        new("api-client-rest-reads",
            "SqlAccountApiClient (ItemPrerequisite Rest UOM; AccountsController source=api)",
            "GET/POST paid api.sql.my",
            "Isolated: SendAsync refuses when Provider=Sdk (no silent REST).",
            "No further change."),

        new("fb-timeout-fallback",
            "SyncOrchestrator.WriteWithFirebirdFallbackAsync",
            "Not REST — Firebird DML after API timeout",
            "Isolated: disabled automatically when Provider=Sdk.",
            "Leave enabled for Rest production."),

        new("apicred-rest-only-gates",
            "PartnerAutoPushTargets / Accounts partners create / company form ApiCred fields",
            "ApiBaseUrl + ApiCredRef",
            "Isolated: Sdk write readiness uses FbPath only. Cred fields retained for Rest rollback.",
            "Provider=Sdk: IsWriteReady(db) checks FbPath; ApiCredRef not required for Sync/Push/AutoPush/Docs."),
    ];

    public static IReadOnlyList<string> VendorSupportQuestions { get; } =
    [
        "Is Maintain Account CUD supported via BizObjects.Find('GL_Acc') with New/Edit/Save/Delete (wiki today only shows Select for list)? If yes, provide a complete sample setting Parent (dockey), Code, Description, Description2, AccType, SpecialAccType, Tax, CashFlowType, Sic — matching POST /account.",
        "For GL_Acc Update: is Params key DocKey or Code? Must Parent be re-resolved by code on the destination company (Master→Sub)? Any restriction deleting accounts with children or GL_TRANS activity?",
        "Is Goods Received Note create supported via BizObjects.Find('PH_GR') with MainDataSet + cdsDocDetail (+ cdsSerialNumber)? Provide full create sample. Confirm whether detail uses Qty, ReceiveQty, or both; how DocNo is allocated; and the supported DO→GR transfer path (Transfer Document vs standalone New).",
        "Do not treat PH_PI fields as PH_GR — please list PH_GR-required header/detail fields and any FROMDOCTYPE/FROMDOCKEY/FROMDTLKEY link fields for DO transfer.",
        "Is Maintain Stock Group create supported via BizObjects.Find('ST_GROUP') New/Save? Provide field sample for Code, Description, Sales, SalesReturned, CashSales, Purchase, CashPurchase, PurchaseReturned, BalanceStock, CostingMethod, IsActive. Confirm ST_CATEGORY is unrelated to /stockgroup.",
        "Is standalone Maintain UOM create required before ST_ITEM, or is writing UOM/Rate/RefCost/RefPrice/ISBASE on ST_ITEM.cdsUOM sufficient when the UOM code is new to the company?",
        "Under Windows Service / IIS app-pool identity: confirm Login(User, Password, DCF, FDB) + IsLogin + Logout requirements (TCP Firebird, DCOM launch permissions, shared DCF UNC).",
    ];
}
