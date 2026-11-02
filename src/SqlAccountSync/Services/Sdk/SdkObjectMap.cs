using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// Pinned BizObject / dataset / field names for SDK Live writes.
/// Discovered against ACC-UAMAT and kept here so write paths stay stable.
/// </summary>
public static class SdkObjectMap
{
    public const string MainDataSet = "MainDataSet";

    public static string MasterBizObject(string entityCode) => entityCode.ToUpperInvariant() switch
    {
        EntityCodes.Customer => "AR_CUSTOMER",
        EntityCodes.Supplier => "AP_SUPPLIER",
        EntityCodes.Account => "GL_ACC",
        EntityCodes.Item => "ST_ITEM",
        _ => ""
    };

    public static string StockGroupBizObject => "ST_GROUP";
    public static string UomBizObject => "ST_UOM";

    /// <summary>Maps SQL Acc API path (or transfer rule path) to a BizObject name.</summary>
    public static string DocumentBizObject(string apiPath)
    {
        var path = (apiPath ?? "").Trim().Trim('/').ToLowerInvariant();
        if (path.Contains("stocktransfer")) return "ST_XF";
        if (path.Contains("salesdebitnote") || path.Contains("debitnote")) return "SL_DN";
        if (path.Contains("salescreditnote") || path.Contains("creditnote")) return "SL_CN";
        if (path.Contains("purchasereturned") || path.Contains("purchasereturn")) return "PH_CN";
        if (path.Contains("goodsreceived")) return "PH_GR";
        if (path.Contains("customerinvoice") || path.Contains("salesinvoice")) return "SL_IV";
        if (path.Contains("purchaseinvoice") || path.Contains("purchase")) return "PH_PI";
        if (path.Contains("deliveryorder")) return "SL_DO";
        return "PH_PI";
    }

    public static string DetailDataSetName(string bizObject) => bizObject.ToUpperInvariant() switch
    {
        // Official wiki samples use cdsDocDetail (not cdsDocumentDetail).
        "ST_XF" => "cdsDocDetail",
        "SL_IV" or "SL_DN" or "SL_CN" or "SL_DO" or "SL_CS" or "SL_SO" => "cdsDocDetail",
        "PH_PI" or "PH_GR" or "PH_CN" => "cdsDocDetail",
        _ => "cdsDocDetail"
    };

    /// <summary>
    /// Maps lowercase API payload keys onto typical SQL Account dataset field names.
    /// Unknown keys are uppercased as-is (SQL Acc datasets are usually UPPERCASE).
    /// </summary>
    public static string ToDatasetField(string apiKey)
    {
        var key = (apiKey ?? "").Trim();
        if (key.Length == 0) return "";
        return key.ToLowerInvariant() switch
        {
            "code" => "CODE",
            "companyname" => "COMPANYNAME",
            "companyname2" => "COMPANYNAME2",
            "controlaccount" => "CONTROLACCOUNT",
            "companycategory" => "COMPANYCATEGORY",
            "area" => "AREA",
            "agent" => "AGENT",
            "biznature" => "BIZNATURE",
            "creditterm" => "CREDITTERM",
            "creditlimit" => "CREDITLIMIT",
            "overduelimit" => "OVERDUELIMIT",
            "statementtype" => "STATEMENTTYPE",
            "currencycode" => "CURRENCYCODE",
            "allowexceedcreditlimit" => "ALLOWEXCEEDCREDITLIMIT",
            "addpdctocrlimit" => "ADDPDCTOCRLIMIT",
            "agingon" => "AGINGON",
            "pricetag" => "PRICETAG",
            "tax" => "TAX",
            "taxexemptno" => "TAXEXEMPTNO",
            "brn" => "BRN",
            "brn2" => "BRN2",
            "gstno" => "GSTNO",
            "salestaxno" => "SALESTAXNO",
            "servicetaxno" => "SERVICETAXNO",
            "tin" => "TIN",
            "idtype" => "IDTYPE",
            "idno" => "IDNO",
            "sic" => "SIC",
            "submissiontype" => "SUBMISSIONTYPE",
            "irbm_classification" => "IRBM_CLASSIFICATION",
            "businessunit" => "BUSINESSUNIT",
            "taxarea" => "TAXAREA",
            "remark" => "REMARK",
            "note" => "NOTE",
            // AR_CUSTOMER/AP_SUPPLIER use STATUS ('A'/'I'); ST_ITEM uses ISACTIVE.
            "status" => "STATUS",
            "isactive" => "ISACTIVE",
            "description" => "DESCRIPTION",
            "description2" => "DESCRIPTION2",
            "description3" => "DESCRIPTION3",
            "stockgroup" => "STOCKGROUP",
            "stockcontrol" => "STOCKCONTROL",
            "costingmethod" => "COSTINGMETHOD",
            "uom" => "UOM",
            "refcost" => "REFCOST",
            "refprice" => "REFPRICE",
            "parent" => "PARENT",
            "dockey" => "DOCKEY",
            "acctype" => "ACCTYPE",
            "specialacctype" => "SPECIALACCTYPE",
            "cashflowtype" => "CASHFLOWTYPE",
            "docno" => "DOCNO",
            "docdate" => "DOCDATE",
            "postdate" => "POSTDATE",
            "taxdate" => "TAXDATE",
            "docamt" => "DOCAMT",
            "companyname_doc" => "COMPANYNAME",
            "itemcode" => "ITEMCODE",
            "qty" => "QTY",
            "unitprice" => "UNITPRICE",
            "amount" => "AMOUNT",
            "location" => "LOCATION",
            "project" => "PROJECT",
            "seq" => "SEQ",
            "fromlocation" => "FROMLOCATION",
            "tolocation" => "TOLOCATION",
            _ when key.StartsWith("udf_", StringComparison.OrdinalIgnoreCase) => key.ToUpperInvariant(),
            _ => key.ToUpperInvariant()
        };
    }

    public static readonly HashSet<string> SkipPayloadKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sdsbranch", "sdsdocdetail", "sdsitemuom", "sdscategory",
        "lastmodified", "dirty", "outstanding", "dtlkey", "fromdocno"
    };
}
