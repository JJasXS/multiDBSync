namespace SqlAccountSync.Helpers;

/// <summary>
/// Pure rules for which Firebird MON$ATTACHMENTS rows may be killed.
/// Own attachment is never included (Firebird also rejects deleting CURRENT_CONNECTION).
/// </summary>
public static class FirebirdAttachmentKickRules
{
    public static IReadOnlyList<long> IdsToKick(IEnumerable<long> attachmentIds, long ownAttachmentId)
    {
        return attachmentIds
            .Where(id => id > 0 && id != ownAttachmentId)
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
