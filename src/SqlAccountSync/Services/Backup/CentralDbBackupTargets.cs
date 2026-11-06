namespace SqlAccountSync.Services.Backup;

public sealed record CentralDbBackupTarget(string Id, string Label, string Path);

/// <summary>Fixed server folders offered on the Backup page.</summary>
public static class CentralDbBackupTargets
{
    public const string EStreamId = "estream";
    public const string AppsId = "apps";
    public const string BesideFdbId = "beside-fdb";

    public const string EStreamPath = @"C:\eStream\SQLAccounting\Backup";
    public const string AppsPath = @"C:\Apps\SqlAccountSync\backup";

    public static string DefaultId => EStreamId;

    public static IReadOnlyList<CentralDbBackupTarget> Resolve(string? centralFdbPath)
    {
        var beside = ResolveBesideFdb(centralFdbPath);
        return
        [
            new CentralDbBackupTarget(EStreamId, "SQL Accounting Backup", EStreamPath),
            new CentralDbBackupTarget(AppsId, "App backup folder", AppsPath),
            new CentralDbBackupTarget(BesideFdbId, "Beside SYNC.FDB", beside)
        ];
    }

    public static string? ResolvePath(string? targetId, string? centralFdbPath)
    {
        var id = (targetId ?? "").Trim();
        if (id.Length == 0)
            id = DefaultId;

        foreach (var t in Resolve(centralFdbPath))
        {
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                return t.Path;
        }

        return null;
    }

    private static string ResolveBesideFdb(string? centralFdbPath)
    {
        var fdb = (centralFdbPath ?? "").Trim();
        if (fdb.Length == 0)
            return Path.Combine(@"C:\SQLData\SqlAccountSync", "backup");

        var dir = Path.GetDirectoryName(fdb.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(dir))
            return Path.Combine(@"C:\SQLData\SqlAccountSync", "backup");

        return Path.Combine(dir, "backup");
    }
}
