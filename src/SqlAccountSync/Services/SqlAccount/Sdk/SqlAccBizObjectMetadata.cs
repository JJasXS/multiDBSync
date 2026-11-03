using System.Text;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Read-only COM BizObject metadata (CHM application_objects4.html pattern).
/// Presence of an object does <b>not</b> prove New/Edit/Save/Delete is supported.
/// </summary>
public sealed record SqlAccBizObjectFieldInfo(string FieldName, string DataType, string DataSize);

public sealed record SqlAccBizObjectDataSetInfo(
    string Name,
    IReadOnlyList<SqlAccBizObjectFieldInfo> Fields);

public sealed record SqlAccBizObjectMetadata(
    string Name,
    bool Found,
    string? FindError,
    IReadOnlyList<SqlAccBizObjectDataSetInfo> DataSets,
    string XmlFieldDefs);

public sealed record SqlAccBizObjectInspectReport(
    bool Success,
    bool SafetyBlocked,
    string Message,
    bool LoginOk = false,
    bool IsLoginOk = false,
    bool LogoutOk = false,
    bool ComCleanupOk = false,
    string? DcfPath = null,
    string? DatabaseFileName = null,
    IReadOnlyList<string>? AllBizObjectNames = null,
    IReadOnlyList<string>? RelatedNames = null,
    IReadOnlyList<SqlAccBizObjectMetadata>? Inspected = null,
    bool TargetsOnly = false);

/// <summary>Focus names + keyword filters for REST-blocker discovery (read-only).</summary>
public static class SqlAccBizObjectInspectTargets
{
    public const int MaxKeyFieldsPerDataSet = 15;

    /// <summary>Exact names always attempted via Find (full inspect mode + targets mode).</summary>
    public static readonly string[] FocusNames =
    [
        "GL_Acc",
        "PH_GR",
        "ST_GROUP",
        "ST_CATEGORY",
        "ST_ITEM",
        "ST_RC",
        "Agent",
        "GL_CB",
        "PH_PI",
        "AR_CUSTOMER",
        "AP_SUPPLIER",
    ];

    /// <summary>Migration-blocker focus names for <c>--inspect-targets-only</c>.</summary>
    public static readonly string[] TargetFocusNames =
    [
        "GL_Acc",
        "PH_GR",
        "ST_GROUP",
        "ST_CATEGORY",
        "ST_ITEM",
        "ST_RC",
    ];

    public static bool IsRelatedName(string name)
    {
        var n = name ?? "";
        return n.Contains("ACC", StringComparison.OrdinalIgnoreCase)
               || n.Contains("GR", StringComparison.OrdinalIgnoreCase)
               || n.Contains("GROUP", StringComparison.OrdinalIgnoreCase)
               || n.Contains("UOM", StringComparison.OrdinalIgnoreCase)
               || n.Contains("ITEM", StringComparison.OrdinalIgnoreCase)
               || n.Contains("GOODS", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("PH_", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("ST_", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("GL_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGlAccountCandidate(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (string.Equals(n, "GL_Acc", StringComparison.OrdinalIgnoreCase))
            return true;
        // Chart / maintain-account style — exclude known voucher modules.
        if (IsKnownGlVoucher(n))
            return false;
        return n.Contains("Account", StringComparison.OrdinalIgnoreCase)
               || string.Equals(n, "GL_ACC", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGoodsReceivedCandidate(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        // ST_RC = Stock Received (inventory) — not Purchase Goods Received.
        if (string.Equals(n, "ST_RC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(n, "PH_GR", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Contains("Goods", StringComparison.OrdinalIgnoreCase))
            return true;
        return n.EndsWith("_GR", StringComparison.OrdinalIgnoreCase)
               || n.Equals("GR", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStockGroupCandidate(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (string.Equals(n, "ST_GROUP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, "ST_CATEGORY", StringComparison.OrdinalIgnoreCase))
            return true;
        return n.Contains("GROUP", StringComparison.OrdinalIgnoreCase)
               || n.Contains("CATEGORY", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUomCandidate(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        return n.Contains("UOM", StringComparison.OrdinalIgnoreCase)
               || n.Contains("UnitOfMeasure", StringComparison.OrdinalIgnoreCase)
               || n.Contains("Unit Of Measure", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownGlVoucher(string name) =>
        name.Equals("GL_CB", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GL_JE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GL_PV", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GL_STOCK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Broad related list (full <c>--inspect-bizobjects</c> dump).</summary>
    public static IReadOnlyList<string> ResolveInspectList(IReadOnlyList<string> allNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!set.Add(name.Trim())) return;
            ordered.Add(name.Trim());
        }

        foreach (var focus in FocusNames)
        {
            var match = allNames.FirstOrDefault(n =>
                string.Equals(n, focus, StringComparison.OrdinalIgnoreCase));
            Add(match ?? focus);
        }

        foreach (var n in allNames.Where(IsRelatedName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Add(n);

        return ordered;
    }

    /// <summary>
    /// Narrow list for REST blockers: required finds + discovered candidates only.
    /// Always includes <c>ST_RC</c> as a contrast note (not Purchase GR).
    /// </summary>
    public static IReadOnlyList<string> ResolveTargetsOnlyList(IReadOnlyList<string> allNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!set.Add(name.Trim())) return;
            ordered.Add(name.Trim());
        }

        foreach (var focus in TargetFocusNames)
        {
            var match = allNames.FirstOrDefault(n =>
                string.Equals(n, focus, StringComparison.OrdinalIgnoreCase));
            Add(match ?? focus);
        }

        foreach (var n in allNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (IsGlAccountCandidate(n)
                || IsGoodsReceivedCandidate(n)
                || IsStockGroupCandidate(n)
                || IsUomCandidate(n))
                Add(n);
        }

        return ordered;
    }

    public static string ClassifyTargetRole(string name)
    {
        if (string.Equals(name, "ST_RC", StringComparison.OrdinalIgnoreCase))
            return "Stock Received (NOT Purchase GR)";
        if (string.Equals(name, "ST_ITEM", StringComparison.OrdinalIgnoreCase))
            return "Item (cdsUOM = item-level UOM, not standalone UOM master)";
        if (IsGlAccountCandidate(name))
            return "GL Account / chart candidate";
        if (IsGoodsReceivedCandidate(name))
            return "Goods Received candidate";
        if (IsStockGroupCandidate(name))
            return "Stock Group / Category candidate";
        if (IsUomCandidate(name))
            return "Standalone UOM candidate";
        if (TargetFocusNames.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
            return "Target focus";
        return "Other";
    }

    /// <summary>Compact console report — no full catalog, no XMLFieldDefs dump.</summary>
    public static string FormatCompactReport(SqlAccBizObjectInspectReport report, int maxFields = MaxKeyFieldsPerDataSet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Targeted BizObject inspect (metadata only; CUD NOT verified) ===");
        sb.AppendLine($"LoginOk={report.LoginOk} IsLoginOk={report.IsLoginOk} LogoutOk={report.LogoutOk} Success={report.Success}");
        sb.AppendLine(report.Message);
        sb.AppendLine($"Catalog size (not listed): {report.AllBizObjectNames?.Count ?? 0}");
        sb.AppendLine();

        var inspected = report.Inspected ?? [];
        foreach (var m in inspected)
        {
            sb.AppendLine($"Object: {m.Name}");
            sb.AppendLine($"  Role: {ClassifyTargetRole(m.Name)}");
            sb.AppendLine($"  Found: {(m.Found ? "True" : "False")}");
            if (!string.IsNullOrWhiteSpace(m.FindError))
                sb.AppendLine($"  Error: {m.FindError}");
            sb.AppendLine("  CUD Support: Unverified");
            if (m.Found)
            {
                var dsNames = m.DataSets.Count == 0
                    ? "(none)"
                    : string.Join(", ", m.DataSets.Select(d => d.Name));
                sb.AppendLine($"  DataSets: {dsNames}");
                foreach (var ds in m.DataSets)
                {
                    sb.AppendLine($"  Key fields [{ds.Name}] (max {maxFields}):");
                    foreach (var f in ds.Fields.Take(maxFields))
                        sb.AppendLine($"    - {f.FieldName} ({f.DataType}, size={f.DataSize})");
                    if (ds.Fields.Count > maxFields)
                        sb.AppendLine($"    … +{ds.Fields.Count - maxFields} more omitted");
                    if (string.Equals(m.Name, "ST_ITEM", StringComparison.OrdinalIgnoreCase)
                        && ds.Name.Contains("UOM", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("    note: item-level cdsUOM — not a standalone UOM master BizObject");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("=== Summary ===");
        sb.AppendLine("| Object | Found | DataSets | CUD Support | Notes |");
        sb.AppendLine("|--------|-------|----------|-------------|-------|");
        foreach (var m in inspected)
        {
            var ds = m.Found
                ? string.Join("; ", m.DataSets.Select(d => d.Name))
                : "—";
            if (string.IsNullOrWhiteSpace(ds)) ds = "(none)";
            var notes = ClassifyTargetRole(m.Name);
            if (!string.IsNullOrWhiteSpace(m.FindError))
                notes += "; " + m.FindError;
            sb.AppendLine($"| {m.Name} | {(m.Found ? "True" : "False")} | {ds} | Unverified | {notes} |");
        }

        sb.AppendLine();
        sb.AppendLine("Note: Found=True + field metadata does NOT prove Create/Update/Delete is supported.");
        return sb.ToString();
    }

    /// <summary>
    /// Full field dump for REST↔SDK mapping (all Fields + XMLFieldDefs). Metadata only; CUD unverified.
    /// </summary>
    public static string FormatFullFieldMap(SqlAccBizObjectInspectReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SDK BizObject field map export");
        sb.AppendLine();
        sb.AppendLine("Generated by `--inspect-targets-only --export-field-map` (or full inspect + export).");
        sb.AppendLine("**CUD is NOT verified** by this export. Use for REST↔SDK name matching only.");
        sb.AppendLine();
        sb.AppendLine($"- LoginOk={report.LoginOk} IsLoginOk={report.IsLoginOk} LogoutOk={report.LogoutOk} Success={report.Success}");
        sb.AppendLine($"- DCF={report.DcfPath ?? "(n/a)"}");
        sb.AppendLine($"- FDB={report.DatabaseFileName ?? "(n/a)"}");
        sb.AppendLine($"- Message: {report.Message}");
        sb.AppendLine();

        foreach (var m in report.Inspected ?? [])
        {
            sb.AppendLine($"## {m.Name}");
            sb.AppendLine();
            sb.AppendLine($"- Role: {ClassifyTargetRole(m.Name)}");
            sb.AppendLine($"- Found: {m.Found}");
            if (!string.IsNullOrWhiteSpace(m.FindError))
                sb.AppendLine($"- Error: {m.FindError}");
            sb.AppendLine("- CUD Support: **Unverified**");
            sb.AppendLine();

            if (!m.Found)
            {
                sb.AppendLine("_Object not found — no fields._");
                sb.AppendLine();
                continue;
            }

            foreach (var ds in m.DataSets)
            {
                sb.AppendLine($"### DataSet `{ds.Name}` ({ds.Fields.Count} fields)");
                sb.AppendLine();
                sb.AppendLine("| FieldName | DataType | DataSize |");
                sb.AppendLine("|-----------|----------|----------|");
                foreach (var f in ds.Fields)
                {
                    var name = EscapePipe(f.FieldName);
                    var type = EscapePipe(f.DataType);
                    var size = EscapePipe(f.DataSize);
                    sb.AppendLine($"| {name} | {type} | {size} |");
                }

                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(m.XmlFieldDefs))
            {
                sb.AppendLine("### XMLFieldDefs");
                sb.AppendLine();
                sb.AppendLine("```xml");
                sb.AppendLine(m.XmlFieldDefs);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string EscapePipe(string? value) =>
        (value ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");
}
