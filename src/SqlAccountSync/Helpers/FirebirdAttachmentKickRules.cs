namespace SqlAccountSync.Helpers;

/// <summary>
/// Pure rules for which Firebird MON$ATTACHMENTS rows may be killed.
/// System threads (Cache Writer / Garbage Collector) are never kick targets.
/// Own attachment is excluded unless <paramref name="includeOwn"/> is true
/// (caller must delete it from a different Firebird connection).
/// </summary>
public static class FirebirdAttachmentKickRules
{
    public static bool IsSystemAttachment(string? user)
    {
        var u = (user ?? "").Trim();
        return u.Equals("Cache Writer", StringComparison.OrdinalIgnoreCase)
            || u.Equals("Garbage Collector", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<long> IdsToKick(
        IEnumerable<(long Id, string User)> attachments,
        long ownAttachmentId,
        bool includeOwn = false)
    {
        return attachments
            .Where(a => a.Id > 0 && !IsSystemAttachment(a.User))
            .Where(a => includeOwn || a.Id != ownAttachmentId)
            .Select(a => a.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    public static string ShortProcess(string? remoteProcess)
    {
        if (string.IsNullOrWhiteSpace(remoteProcess))
            return "—";
        var trimmed = remoteProcess.Trim().Trim('"');
        var slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
    }

    public static bool LooksLikeSqlAcc(string? remoteProcess)
    {
        var name = ShortProcess(remoteProcess);
        if (name == "—")
            return false;
        // Match SQLACC.exe only — not SqlAccountSync.dll (contains "SqlAcc").
        var stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : Path.GetFileNameWithoutExtension(name);
        return stem.Equals("SQLACC", StringComparison.OrdinalIgnoreCase);
    }
}
