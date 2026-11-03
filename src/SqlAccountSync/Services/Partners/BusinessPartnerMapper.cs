using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Partners;

/// <summary>Normalized business partner used for Customer↔Supplier sync.</summary>
public sealed class BusinessPartner
{
    public string SourceDbCode { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceCode { get; set; } = "";
    public string ControlAccount { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string CompanyName2 { get; set; } = "";
    public string CompanyCategory { get; set; } = "";
    public string Area { get; set; } = "";
    public string Agent { get; set; } = "";
    public string BizNature { get; set; } = "";
    public string CreditTerm { get; set; } = "";
    public string CreditLimit { get; set; } = "0";
    public string OverdueLimit { get; set; } = "0";
    public string StatementType { get; set; } = "O";
    public string CurrencyCode { get; set; } = "----";
    public bool AllowExceedCreditLimit { get; set; } = true;
    public bool AddPdcToCrLimit { get; set; } = true;
    public string AgingOn { get; set; } = "I";
    public string PriceTag { get; set; } = "";
    public string Tax { get; set; } = "";
    public string TaxExemptNo { get; set; } = "";
    public string Brn { get; set; } = "";
    public string Brn2 { get; set; } = "";
    public string GstNo { get; set; } = "";
    public string SalesTaxNo { get; set; } = "";
    public string ServiceTaxNo { get; set; } = "";
    public string Tin { get; set; } = "";
    public int IdType { get; set; }
    public string IdNo { get; set; } = "";
    public string Sic { get; set; } = "00000";
    public int SubmissionType { get; set; }
    public string IrbmClassification { get; set; } = "";
    public string BusinessUnit { get; set; } = "";
    public string TaxArea { get; set; } = "";
    public string Remark { get; set; } = "";
    public string Note { get; set; } = "";
    public string Status { get; set; } = "A";
    public string UdfEmail01 { get; set; } = "";
    public string UdfLocation { get; set; } = "";
    public string UdfDepartment01 { get; set; } = "";
    public List<PartnerBranch> Branches { get; set; } = [];
}

public sealed class PartnerBranch
{
    public string BranchType { get; set; } = "B";
    public string BranchName { get; set; } = "BILLING";
    public string Address1 { get; set; } = "";
    public string Address2 { get; set; } = "";
    public string Address3 { get; set; } = "";
    public string Address4 { get; set; } = "";
    public string Postcode { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "MY";
    public string Attention { get; set; } = "";
    public string Phone1 { get; set; } = "";
    public string Phone2 { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Fax1 { get; set; } = "";
    public string Fax2 { get; set; } = "";
    public string Email { get; set; } = "";
}

public static class BusinessPartnerMapper
{
    public static BusinessPartner FromMasterRecord(MasterRecord record, RegisteredDatabase source, string sourceEntity)
    {
        var f = record.Fields;
        var partner = new BusinessPartner
        {
            SourceDbCode = source.DbCode,
            SourceType = sourceEntity,
            SourceCode = record.Code,
            ControlAccount = Field(f, "controlaccount", "controlAccount"),
            CompanyName = First(f, record.Description, "companyname", "companyName", "name"),
            CompanyName2 = Field(f, "companyname2", "companyName2"),
            CompanyCategory = Field(f, "companycategory", "companyCategory") is { Length: > 0 } c ? c : "----",
            Area = Field(f, "area") is { Length: > 0 } a ? a : "----",
            Agent = Field(f, "agent") is { Length: > 0 } ag ? ag : "----",
            BizNature = Field(f, "biznature", "bizNature"),
            CreditTerm = Field(f, "creditterm", "creditTerm") is { Length: > 0 } ct ? ct : "30 Days",
            CreditLimit = Field(f, "creditlimit", "creditLimit") is { Length: > 0 } cl ? cl : "0",
            OverdueLimit = Field(f, "overduelimit", "overdueLimit") is { Length: > 0 } ol ? ol : "0",
            StatementType = Field(f, "statementtype", "statementType") is { Length: > 0 } st ? st : "O",
            CurrencyCode = Field(f, "currencycode", "currencyCode") is { Length: > 0 } cc ? cc : "----",
            AllowExceedCreditLimit = Bool(f, true, "allowexceedcreditlimit", "allowExceedCreditLimit"),
            AddPdcToCrLimit = Bool(f, true, "addpdctocrlimit", "addPdcToCrLimit"),
            AgingOn = Field(f, "agingon", "agingOn") is { Length: > 0 } ao ? ao : "I",
            PriceTag = Field(f, "pricetag", "priceTag"),
            Tax = Field(f, "tax"),
            TaxExemptNo = Field(f, "taxexemptno", "taxExemptNo"),
            Brn = Field(f, "brn"),
            Brn2 = Field(f, "brn2"),
            GstNo = Field(f, "gstno", "gstNo"),
            SalesTaxNo = Field(f, "salestaxno", "salesTaxNo", "taxId"),
            ServiceTaxNo = Field(f, "servicetaxno", "serviceTaxNo"),
            Tin = Field(f, "tin"),
            IdType = Int(f, 0, "idtype", "idType"),
            IdNo = Field(f, "idno", "idNo"),
            Sic = Field(f, "sic") is { Length: > 0 } sic ? sic : "00000",
            SubmissionType = Int(f, 0, "submissiontype", "submissionType"),
            IrbmClassification = Field(f, "irbm_classification", "irbmClassification"),
            BusinessUnit = Field(f, "businessunit", "businessUnit"),
            TaxArea = Field(f, "taxarea", "taxArea"),
            Remark = Field(f, "remark"),
            Note = Field(f, "note"),
            Status = Field(f, "status") is { Length: > 0 } s ? s : "A",
            UdfEmail01 = Field(f, "udf_email01"),
            UdfLocation = Field(f, "udf_location"),
            UdfDepartment01 = Field(f, "udf_department01")
        };

        if (TryParseBranches(f, out var branches) && branches.Count > 0)
        {
            partner.Branches = branches;
        }
        else
        {
            partner.Branches =
            [
                new PartnerBranch
                {
                    BranchType = "B",
                    BranchName = "BILLING",
                    Address1 = Field(f, "address1", "address"),
                    Address2 = Field(f, "address2"),
                    Address3 = Field(f, "address3"),
                    Address4 = Field(f, "address4"),
                    Postcode = Field(f, "postcode"),
                    City = Field(f, "city"),
                    State = Field(f, "state"),
                    Country = Field(f, "country") is { Length: > 0 } country ? country : "MY",
                    Attention = Field(f, "attention"),
                    Phone1 = Field(f, "phone1", "phone"),
                    Phone2 = Field(f, "phone2"),
                    Mobile = Field(f, "mobile"),
                    Fax1 = Field(f, "fax1"),
                    Fax2 = Field(f, "fax2"),
                    Email = Field(f, "email", "udf_email01")
                }
            ];
        }

        return partner;
    }

    /// <summary>
    /// Builds SQL Accounting POST /customer or /supplier body (lowercase field names, nested sdsbranch).
    /// </summary>
    public static Dictionary<string, object?> ToApiPayload(
        BusinessPartner partner, string targetEntityCode, string suggestedCode, string txnStamp)
    {
        var isCustomer = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase);
        var code = (suggestedCode ?? "").Trim();
        if (code.Length == 0)
            code = partner.SourceCode;

        // Reciprocal Customer↔Supplier always uses destination control account.
        // Same-type Master→Sub keeps source control account when present.
        var controlAccount = isCustomer ? "300-000" : "400-000";
        if (string.Equals(partner.SourceType, targetEntityCode, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(partner.ControlAccount))
            controlAccount = partner.ControlAccount.Trim();

        var remark = string.IsNullOrWhiteSpace(partner.Remark) ? "" : partner.Remark;
        if (!string.IsNullOrWhiteSpace(txnStamp))
            remark = string.IsNullOrWhiteSpace(remark) ? $"SYNC|{txnStamp}" : $"{remark} | SYNC|{txnStamp}";

        var branches = partner.Branches.Count > 0 ? partner.Branches : [new PartnerBranch()];
        var sdsbranch = branches.Select(b => new Dictionary<string, object?>
        {
            ["code"] = code,
            ["branchtype"] = NullIfEmpty(b.BranchType) ?? "B",
            ["branchname"] = NullIfEmpty(b.BranchName) ?? "BILLING",
            ["address1"] = NullIfEmpty(b.Address1),
            ["address2"] = NullIfEmpty(b.Address2),
            ["address3"] = NullIfEmpty(b.Address3),
            ["address4"] = NullIfEmpty(b.Address4),
            ["postcode"] = NullIfEmpty(b.Postcode),
            ["city"] = NullIfEmpty(b.City),
            ["state"] = NullIfEmpty(b.State),
            ["country"] = NullIfEmpty(b.Country) ?? "MY",
            ["attention"] = NullIfEmpty(b.Attention),
            ["phone1"] = NullIfEmpty(b.Phone1),
            ["phone2"] = NullIfEmpty(b.Phone2),
            ["mobile"] = NullIfEmpty(b.Mobile),
            ["fax1"] = NullIfEmpty(b.Fax1),
            ["fax2"] = NullIfEmpty(b.Fax2),
            ["email"] = NullIfEmpty(b.Email)
        }).ToList();

        // Create payload: omit read-only / server-owned fields (outstanding, lastmodified, dirty, dtlkey).
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = code,
            ["controlaccount"] = controlAccount,
            ["companyname"] = partner.CompanyName,
            ["companyname2"] = NullIfEmpty(partner.CompanyName2),
            ["companycategory"] = NullIfEmpty(partner.CompanyCategory) ?? "----",
            ["area"] = NullIfEmpty(partner.Area) ?? "----",
            ["agent"] = NullIfEmpty(partner.Agent) ?? "----",
            ["biznature"] = NullIfEmpty(partner.BizNature),
            ["creditterm"] = NullIfEmpty(partner.CreditTerm) ?? "30 Days",
            ["creditlimit"] = partner.CreditLimit is { Length: > 0 } ? partner.CreditLimit : "0",
            ["overduelimit"] = partner.OverdueLimit is { Length: > 0 } ? partner.OverdueLimit : "0",
            ["statementtype"] = NullIfEmpty(partner.StatementType) ?? "O",
            ["currencycode"] = NullIfEmpty(partner.CurrencyCode) ?? "----",
            ["allowexceedcreditlimit"] = partner.AllowExceedCreditLimit,
            ["addpdctocrlimit"] = partner.AddPdcToCrLimit,
            ["agingon"] = NullIfEmpty(partner.AgingOn) ?? "I",
            ["pricetag"] = NullIfEmpty(partner.PriceTag),
            ["tax"] = NullIfEmpty(partner.Tax),
            ["taxexemptno"] = NullIfEmpty(partner.TaxExemptNo),
            ["brn"] = NullIfEmpty(partner.Brn),
            ["brn2"] = NullIfEmpty(partner.Brn2),
            ["gstno"] = NullIfEmpty(partner.GstNo),
            ["salestaxno"] = NullIfEmpty(partner.SalesTaxNo),
            ["servicetaxno"] = NullIfEmpty(partner.ServiceTaxNo),
            ["tin"] = NullIfEmpty(partner.Tin),
            ["idtype"] = partner.IdType,
            ["idno"] = NullIfEmpty(partner.IdNo),
            ["sic"] = NullIfEmpty(partner.Sic) ?? "00000",
            ["submissiontype"] = partner.SubmissionType,
            ["irbm_classification"] = NullIfEmpty(partner.IrbmClassification)
                ?? (isCustomer ? "022" : "036"),
            ["businessunit"] = NullIfEmpty(partner.BusinessUnit),
            ["taxarea"] = NullIfEmpty(partner.TaxArea),
            ["remark"] = NullIfEmpty(remark),
            ["note"] = NullIfEmpty(partner.Note),
            ["status"] = NullIfEmpty(partner.Status) ?? "A",
            ["sdsbranch"] = sdsbranch,
            ["sdscreditcontrol"] = Array.Empty<object>(),
            ["sdsbankacc"] = Array.Empty<object>(),
            ["sdstariff"] = Array.Empty<object>(),
            ["udf_email01"] = NullIfEmpty(partner.UdfEmail01),
            ["udf_department01"] = NullIfEmpty(partner.UdfDepartment01)
        };

        if (isCustomer)
            payload["udf_location"] = NullIfEmpty(partner.UdfLocation);

        return payload;
    }

    /// <summary>
    /// Slim payload for SQL Acc SDK Live. Full HTTP API payload sets many optionals that
    /// leave Null variants and crash Save with "Could not convert Null into Integer".
    /// </summary>
    public static Dictionary<string, object?> ToSdkPayload(
        BusinessPartner partner, string targetEntityCode, string suggestedCode, string txnStamp)
    {
        var isCustomer = string.Equals(targetEntityCode, EntityCodes.Customer, StringComparison.OrdinalIgnoreCase);
        var code = (suggestedCode ?? "").Trim();
        if (code.Length == 0)
            code = partner.SourceCode;

        var controlAccount = isCustomer ? "300-000" : "400-000";
        if (string.Equals(partner.SourceType, targetEntityCode, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(partner.ControlAccount))
            controlAccount = partner.ControlAccount.Trim();

        var remark = string.IsNullOrWhiteSpace(partner.Remark) ? "" : partner.Remark;
        if (!string.IsNullOrWhiteSpace(txnStamp))
            remark = string.IsNullOrWhiteSpace(remark) ? $"SYNC|{txnStamp}" : $"{remark} | SYNC|{txnStamp}";

        var branch = partner.Branches.Count > 0 ? partner.Branches[0] : new PartnerBranch();
        // Keep this close to the eStream wiki sample (CODE + CompanyName + cdsBranch.Edit).
        // Extra numeric optionals on some company DBs crash Save with Null→Integer.
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = code,
            ["companyname"] = partner.CompanyName,
            ["companyname2"] = NullIfEmpty(partner.CompanyName2),
            ["remark"] = NullIfEmpty(remark),
            ["note"] = NullIfEmpty(partner.Note),
            ["sdsbranch"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["branchname"] = NullIfEmpty(branch.BranchName) ?? "BILLING",
                    ["address1"] = NullIfEmpty(branch.Address1),
                    ["address2"] = NullIfEmpty(branch.Address2),
                    ["address3"] = NullIfEmpty(branch.Address3),
                    ["address4"] = NullIfEmpty(branch.Address4),
                    ["attention"] = NullIfEmpty(branch.Attention),
                    ["phone1"] = NullIfEmpty(branch.Phone1),
                    ["fax1"] = NullIfEmpty(branch.Fax1),
                    ["email"] = NullIfEmpty(branch.Email)
                }
            }
        };
        // Do not force CONTROLACCOUNT — missing codes (e.g. 300-000) crash some DBs with Null→Integer.
        // SQL Acc assigns the default debtor/creditor control account on New().
        _ = controlAccount;
        return payload;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static bool TryParseBranches(Dictionary<string, string> fields, out List<PartnerBranch> branches)
    {
        branches = [];
        if (!fields.TryGetValue("sdsbranch_json", out var json) || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var list = JsonSerializer.Deserialize<List<PartnerBranch>>(json);
            if (list is null || list.Count == 0)
                return false;
            branches = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string First(Dictionary<string, string> fields, string? primary, params string[] keys)
    {
        if (!string.IsNullOrWhiteSpace(primary))
            return primary.Trim();
        return Field(fields, keys);
    }

    private static string Field(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }

    private static bool Bool(Dictionary<string, string> fields, bool fallback, params string[] keys)
    {
        var s = Field(fields, keys);
        if (s.Length == 0) return fallback;
        if (bool.TryParse(s, out var b)) return b;
        if (s is "1" or "Y" or "T") return true;
        if (s is "0" or "N" or "F") return false;
        return fallback;
    }

    private static int Int(Dictionary<string, string> fields, int fallback, params string[] keys)
    {
        var s = Field(fields, keys);
        return int.TryParse(s, out var n) ? n : fallback;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
