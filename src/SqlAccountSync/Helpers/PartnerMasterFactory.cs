using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Builds / normalizes customer-supplier master payloads for create UI.</summary>
public static class PartnerMasterFactory
{
    public const string DefaultCreditTerm = "30 Days";

    public static string NormalizeEntity(string? entity) =>
        string.Equals(entity, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
            ? EntityCodes.Supplier
            : EntityCodes.Customer;

    public static string EntityTitle(string entityCode) =>
        string.Equals(entityCode, EntityCodes.Supplier, StringComparison.OrdinalIgnoreCase)
            ? "Supplier"
            : "Customer";

    public static string PickCreditTerm(IReadOnlyList<MasterRecord> terms, string? current)
    {
        if (terms.Count == 0)
            return string.IsNullOrWhiteSpace(current) ? DefaultCreditTerm : current.Trim();

        var want = (current ?? "").Trim();
        var match = terms.FirstOrDefault(t =>
            string.Equals(t.Code, want, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match.Code;

        var preferred = terms.FirstOrDefault(t =>
            string.Equals(t.Code, DefaultCreditTerm, StringComparison.OrdinalIgnoreCase));
        return preferred?.Code ?? terms[0].Code;
    }

    public static MasterRecord Build(
        string code,
        string companyName,
        string? creditTerm,
        string? address1 = null,
        string? address2 = null,
        string? address3 = null,
        string? address4 = null,
        string? phone1 = null,
        string? email = null,
        string? remark = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["companyname"] = companyName,
            ["creditterm"] = string.IsNullOrWhiteSpace(creditTerm) ? DefaultCreditTerm : creditTerm.Trim(),
            ["area"] = "----",
            ["agent"] = "----",
            ["companycategory"] = "----",
            ["currencycode"] = "----",
            ["country"] = "MY",
            ["status"] = "A"
        };
        PutIfPresent(fields, "address1", address1);
        PutIfPresent(fields, "address2", address2);
        PutIfPresent(fields, "address3", address3);
        PutIfPresent(fields, "address4", address4);
        PutIfPresent(fields, "phone1", phone1);
        PutIfPresent(fields, "email", email);
        PutIfPresent(fields, "remark", remark);

        return new MasterRecord
        {
            Code = code,
            Description = companyName,
            Key = code,
            Fields = fields
        };
    }

    private static void PutIfPresent(Dictionary<string, string> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[key] = value.Trim();
    }
}
