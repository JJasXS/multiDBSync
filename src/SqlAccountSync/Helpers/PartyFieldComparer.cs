using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Compares syncable customer/supplier fields for auto-update detection.</summary>
public static class PartyFieldComparer
{
    private static readonly string[] CompareKeys =
    [
        "companyname", "controlaccount", "companycategory", "area", "agent",
        "creditterm", "creditlimit", "overduelimit", "currencycode", "status",
        "phone1", "phone", "email", "address1", "attention"
    ];

    public static bool Differs(MasterRecord? source, MasterRecord? dest)
    {
        if (source is null || dest is null)
            return source is not null || dest is not null;

        foreach (var key in CompareKeys)
        {
            if (!Field(source, key).Equals(Field(dest, key), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static List<string> CollectDifferingCodes(
        IReadOnlyDictionary<string, MasterRecord> sourceByCode,
        IReadOnlyDictionary<string, MasterRecord> destByCode)
    {
        var list = new List<string>();
        foreach (var (code, src) in sourceByCode)
        {
            if (!destByCode.TryGetValue(code, out var dest))
                continue;
            if (Differs(src, dest))
                list.Add(code);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static string Field(MasterRecord record, string key)
    {
        if (record.Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim();
        if (key.Equals("companyname", StringComparison.OrdinalIgnoreCase))
            return (record.Description ?? "").Trim();
        return "";
    }
}
