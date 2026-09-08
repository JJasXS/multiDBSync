using System.Globalization;
using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Accounts;

/// <summary>Maps Maintain Account rows to SQL Acc POST /account and MasterRecord fields.</summary>
public static class GlAccountMapper
{
    public const string FieldParentCode = "parentcode";
    public const string FieldDestParent = "destparent";
    public const string FieldAccType = "acctype";
    public const string FieldSpecialAccType = "specialacctype";
    public const string FieldDescription2 = "description2";
    public const string FieldTax = "tax";
    public const string FieldCashFlowType = "cashflowtype";
    public const string FieldSic = "sic";
    public const string FieldLevel = "level";
    public const string FieldIndent = "indent";

    public static MasterRecord ToMasterRecord(GlAccountRow row, IReadOnlyDictionary<int, GlAccountRow>? byDocKey = null)
    {
        var parentCode = "";
        if (row.Parent != 0 && byDocKey is not null && byDocKey.TryGetValue(row.Parent, out var parent))
            parentCode = parent.Code ?? "";

        return new MasterRecord
        {
            Code = row.Code ?? "",
                Description = string.IsNullOrWhiteSpace(row.Description) ? (row.Code ?? "") : row.Description,
            Key = string.IsNullOrWhiteSpace(row.Code) ? row.DocKey.ToString(CultureInfo.InvariantCulture) : row.Code,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dockey"] = row.DocKey.ToString(CultureInfo.InvariantCulture),
                ["parent"] = row.Parent.ToString(CultureInfo.InvariantCulture),
                [FieldParentCode] = parentCode,
                [FieldDescription2] = row.Description2 ?? "",
                [FieldAccType] = row.AccType ?? "",
                [FieldSpecialAccType] = row.SpecialAccType ?? "",
                [FieldTax] = row.Tax ?? "",
                [FieldCashFlowType] = row.CashFlowType.ToString(CultureInfo.InvariantCulture),
                [FieldSic] = row.Sic ?? "",
                [FieldLevel] = row.Level.ToString(CultureInfo.InvariantCulture),
                [FieldIndent] = row.Indent ?? ""
            }
        };
    }

    public static List<MasterRecord> ToMasterRecords(IReadOnlyList<GlAccountRow> chart)
    {
        var byKey = chart.ToDictionary(a => a.DocKey);
        return chart.Select(r => ToMasterRecord(r, byKey)).ToList();
    }

    public static GlAccountRow FromMasterRecord(MasterRecord record)
    {
        var f = record.Fields;
        return new GlAccountRow
        {
            DocKey = IntField(f, "dockey"),
            Parent = IntField(f, "parent"),
            Code = (record.Code ?? "").Trim(),
            Description = (record.Description ?? "").Trim(),
            Description2 = NullIfEmpty(Field(f, FieldDescription2)),
            AccType = NullIfEmpty(Field(f, FieldAccType)),
            SpecialAccType = NullIfEmpty(Field(f, FieldSpecialAccType)),
            Tax = NullIfEmpty(Field(f, FieldTax)),
            CashFlowType = IntField(f, FieldCashFlowType),
            Sic = NullIfEmpty(Field(f, FieldSic)),
            Level = IntField(f, FieldLevel),
            Indent = Field(f, FieldIndent)
        };
    }

    public static string ParentCodeOf(MasterRecord record) =>
        Field(record.Fields, FieldParentCode).Trim();

    public static Dictionary<string, object?> ToApiPayload(GlAccountRow row, int destParentDocKey)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["dockey"] = 0,
            ["parent"] = destParentDocKey,
            ["code"] = (row.Code ?? "").Trim(),
            ["description"] = (row.Description ?? "").Trim(),
            // SQL Acc rejects JSON null for these OleStr fields ("Could not convert variant of type (Null)").
            ["description2"] = (row.Description2 ?? "").Trim(),
            ["acctype"] = (row.AccType ?? "").Trim(),
            ["specialacctype"] = (row.SpecialAccType ?? "").Trim(),
            ["tax"] = (row.Tax ?? "").Trim(),
            ["cashflowtype"] = row.CashFlowType,
            ["sic"] = (row.Sic ?? "").Trim()
        };
    }

    public static Dictionary<string, object?> ToUpdatePayload(GlAccountRow row, int destParentDocKey, int destDocKey)
    {
        var payload = ToApiPayload(row, destParentDocKey);
        payload["dockey"] = destDocKey;
        return payload;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    /// <summary>Find Sub dockey for parent code; 0 if root / missing.</summary>
    public static int ResolveDestParentDocKey(string? parentCode, IEnumerable<GlAccountRow> destChart)
    {
        var code = (parentCode ?? "").Trim();
        if (code.Length == 0)
            return 0;
        var hit = destChart.FirstOrDefault(a =>
            string.Equals((a.Code ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));
        return hit?.DocKey ?? 0;
    }

    /// <summary>Order selected codes parents-before-children using chart levels.</summary>
    public static List<string> OrderCodesParentsFirst(IEnumerable<string> selectedCodes, IReadOnlyList<GlAccountRow> chart)
    {
        var levelByCode = chart.ToDictionary(
            a => (a.Code ?? "").Trim(),
            a => a.Level,
            StringComparer.OrdinalIgnoreCase);

        return selectedCodes
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => levelByCode.TryGetValue(c, out var lvl) ? lvl : 999)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Field(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) ? v ?? "" : "";

    private static int IntField(Dictionary<string, string> fields, string key) =>
        int.TryParse(Field(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
