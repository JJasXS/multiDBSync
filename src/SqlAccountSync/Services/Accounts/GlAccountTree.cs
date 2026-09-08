using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlAccountSync.Services.Accounts;

/// <summary>GL account row from SQL Accounting Maintain Account (GET /account/*).</summary>
public sealed class GlAccountRow
{
    public int DocKey { get; set; }
    public int Parent { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Description2 { get; set; }
    public string? AccType { get; set; }
    public string? SpecialAccType { get; set; }
    public string? Tax { get; set; }
    public int CashFlowType { get; set; }
    public string? Sic { get; set; }

    /// <summary>0 = top, 1 = sub, 2 = sub-sub, … from parent/dockey tree.</summary>
    public int Level { get; set; }

    /// <summary>Leading spaces for table indent (2 spaces per level).</summary>
    public string Indent { get; set; } = "";

    public string DisplayName => Indent + Description;
}

public static class GlAccountTree
{
    public static List<GlAccountRow> ParseAndOrder(string jsonBody)
    {
        var flat = Parse(jsonBody);
        return OrderTree(flat);
    }

    public static List<GlAccountRow> Parse(string jsonBody)
    {
        var list = new List<GlAccountRow>();
        if (string.IsNullOrWhiteSpace(jsonBody))
            return list;

        using var doc = JsonDocument.Parse(jsonBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var row in data.EnumerateArray())
        {
            list.Add(new GlAccountRow
            {
                DocKey = GetInt(row, "dockey"),
                Parent = GetInt(row, "parent"),
                Code = GetString(row, "code"),
                Description = GetString(row, "description"),
                Description2 = GetStringOrNull(row, "description2"),
                AccType = GetStringOrNull(row, "acctype"),
                SpecialAccType = GetStringOrNull(row, "specialacctype"),
                Tax = GetStringOrNull(row, "tax"),
                CashFlowType = GetInt(row, "cashflowtype"),
                Sic = GetStringOrNull(row, "sic")
            });
        }

        return list;
    }

    /// <summary>Depth-first under each parent; assigns Level / Indent.</summary>
    public static List<GlAccountRow> OrderTree(IReadOnlyList<GlAccountRow> flat)
    {
        var byParent = flat
            .GroupBy(a => a.Parent)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Code, StringComparer.OrdinalIgnoreCase).ToList());

        var byKey = flat.ToDictionary(a => a.DocKey);
        var roots = flat
            .Where(a => a.Parent == 0 || !byKey.ContainsKey(a.Parent))
            .OrderBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ordered = new List<GlAccountRow>(flat.Count);
        var visited = new HashSet<int>();

        foreach (var root in roots)
            Walk(root, 0, byParent, ordered, visited);

        foreach (var leftover in flat.Where(a => !visited.Contains(a.DocKey)))
        {
            leftover.Level = 0;
            leftover.Indent = "";
            ordered.Add(leftover);
        }

        return ordered;
    }

    private static void Walk(
        GlAccountRow node,
        int level,
        Dictionary<int, List<GlAccountRow>> byParent,
        List<GlAccountRow> ordered,
        HashSet<int> visited)
    {
        if (!visited.Add(node.DocKey))
            return;

        node.Level = level;
        node.Indent = level <= 0 ? "" : new string(' ', level * 2);
        ordered.Add(node);

        if (!byParent.TryGetValue(node.DocKey, out var children))
            return;

        foreach (var child in children)
            Walk(child, level + 1, byParent, ordered, visited);
    }

    private static int GetInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            ? n
            : 0;

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? "").Trim()
            : "";

    private static string? GetStringOrNull(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (el.ValueKind != JsonValueKind.String)
            return el.ToString();
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}

public sealed class CreateGlAccountRequest
{
    [JsonPropertyName("dockey")] public int DocKey { get; set; }
    [JsonPropertyName("parent")] public int Parent { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("description2")] public string? Description2 { get; set; }
    [JsonPropertyName("acctype")] public string? AccType { get; set; }
    [JsonPropertyName("specialacctype")] public string? SpecialAccType { get; set; }
    [JsonPropertyName("tax")] public string? Tax { get; set; }
    [JsonPropertyName("cashflowtype")] public int CashFlowType { get; set; }
    [JsonPropertyName("sic")] public string? Sic { get; set; }
}
