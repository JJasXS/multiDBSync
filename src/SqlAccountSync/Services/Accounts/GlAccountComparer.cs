namespace SqlAccountSync.Services.Accounts;

/// <summary>Compares syncable Maintain Account fields between master and sub charts.</summary>
public static class GlAccountComparer
{
    public static bool Differs(
        GlAccountRow master,
        string masterParentCode,
        GlAccountRow dest,
        string destParentCode)
    {
        if (!Same(master.Description, dest.Description))
            return true;
        if (!Same(master.Description2, dest.Description2))
            return true;
        if (!Same(master.AccType, dest.AccType))
            return true;
        if (!Same(master.SpecialAccType, dest.SpecialAccType))
            return true;
        if (!Same(master.Tax, dest.Tax))
            return true;
        if (master.CashFlowType != dest.CashFlowType)
            return true;
        if (!Same(master.Sic, dest.Sic))
            return true;
        if (!Same(masterParentCode, destParentCode))
            return true;
        return false;
    }

    public static string ParentCodeOf(GlAccountRow row, IReadOnlyDictionary<int, GlAccountRow> byDocKey)
    {
        if (row.Parent == 0)
            return "";
        return byDocKey.TryGetValue(row.Parent, out var parent)
            ? (parent.Code ?? "").Trim()
            : "";
    }

    public static Dictionary<string, GlAccountRow> IndexByCode(IEnumerable<GlAccountRow> chart) =>
        chart
            .Where(a => !string.IsNullOrWhiteSpace(a.Code))
            .GroupBy(a => a.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static Dictionary<int, GlAccountRow> IndexByDocKey(IEnumerable<GlAccountRow> chart) =>
        chart.ToDictionary(a => a.DocKey);

    /// <summary>Codes present on both charts whose syncable fields differ (including parent).</summary>
    public static List<string> CollectDifferingCodes(
        IReadOnlyList<GlAccountRow> masterChart,
        IReadOnlyList<GlAccountRow> destChart)
    {
        var masterByCode = IndexByCode(masterChart);
        var destByCode = IndexByCode(destChart);
        var masterByKey = IndexByDocKey(masterChart);
        var destByKey = IndexByDocKey(destChart);
        var changed = new List<string>();

        foreach (var (code, masterRow) in masterByCode)
        {
            if (!destByCode.TryGetValue(code, out var destRow))
                continue;
            var masterParent = ParentCodeOf(masterRow, masterByKey);
            var destParent = ParentCodeOf(destRow, destByKey);
            if (Differs(masterRow, masterParent, destRow, destParent))
                changed.Add(code);
        }

        return changed;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
