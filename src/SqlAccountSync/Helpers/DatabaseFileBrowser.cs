namespace SqlAccountSync.Helpers;

public sealed record DatabaseBrowseResult(
    string Path,
    string? Parent,
    string Error,
    IReadOnlyList<DatabaseBrowseEntry> Entries);

public sealed record DatabaseBrowseEntry(string Name, string Path, bool IsFile);

public static class DatabaseFileBrowser
{
    private const int MaxEntries = 400;
    private static readonly string[] Preferred =
    [
        @"C:\eStream\SQLAccounting\DB",
        @"C:\SQLData"
    ];

    public static DatabaseBrowseResult Start()
    {
        foreach (var candidate in Preferred)
        {
            if (Directory.Exists(candidate))
                return ReadDirectory(candidate);
        }
        return Roots();
    }

    public static DatabaseBrowseResult List(string? path)
    {
        var full = Normalize(path);
        if (full is null)
            return Roots();
        if (IsDatabaseFile(full) && File.Exists(full))
            full = System.IO.Path.GetDirectoryName(full) ?? full;
        if (!Directory.Exists(full))
            return Roots();
        return ReadDirectory(full);
    }

    private static DatabaseBrowseResult ReadDirectory(string full)
    {
        try
        {
            var entries = ReadEntries(full);
            return new DatabaseBrowseResult(Display(full), ParentOf(full), "", entries);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new DatabaseBrowseResult(Display(full), ParentOf(full), "That folder cannot be opened.", []);
        }
    }

    private static List<DatabaseBrowseEntry> ReadEntries(string full)
    {
        var dirs = Directory.EnumerateDirectories(full)
            .Where(CanShow)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .Select(dir => new DatabaseBrowseEntry(System.IO.Path.GetFileName(dir) ?? dir, Display(dir), false));
        var files = Directory.EnumerateFiles(full)
            .Where(IsDatabaseFile)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .Select(file => new DatabaseBrowseEntry(System.IO.Path.GetFileName(file) ?? file, Display(file), true));
        return dirs.Concat(files).Take(MaxEntries).ToList();
    }

    private static DatabaseBrowseResult Roots()
    {
        var entries = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DatabaseBrowseEntry(drive.Name.TrimEnd('\\'), Display(drive.Name), false))
            .ToList();
        return new DatabaseBrowseResult("", null, "", entries);
    }

    private static string? Normalize(string? path)
    {
        var raw = (path ?? "").Trim().Trim('"');
        if (raw.Length == 0 || raw.Contains('\0'))
            return null;
        try
        {
            var full = System.IO.Path.GetFullPath(raw);
            return System.IO.Path.IsPathRooted(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string ParentOf(string full)
    {
        var trimmed = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? "" : Display(parent);
    }

    private static bool CanShow(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsDatabaseFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".fdb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gdb", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(string path) => path.Replace('\\', '/');
}
