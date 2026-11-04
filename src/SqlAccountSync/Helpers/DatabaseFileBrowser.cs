namespace SqlAccountSync.Helpers;

public static class DatabaseFileBrowser
{
    private const int MaxMatches = 20;
    private const int MaxDirs = 8000;
    private static readonly TimeSpan SearchLimit = TimeSpan.FromSeconds(8);
    private static readonly string[] Preferred =
    [
        @"C:\eStream\SQLAccounting\Share",
        @"C:\eStream\SQLAccounting\DB",
        @"C:\SQLData"
    ];
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "AppData", "WinSxS",
        "node_modules", ".git"
    };

    public static IReadOnlyList<string> FindByName(string? fileName)
    {
        var name = SafeName(fileName);
        if (name is null)
            return [];

        // Only approved database roots — never scan entire fixed drives (path disclosure / DoS).
        var state = new SearchState(name, DateTime.UtcNow + SearchLimit);
        foreach (var root in Preferred.Where(Directory.Exists))
            Search(root, state);
        return state.Found;
    }

    private static void Search(string dir, SearchState state)
    {
        if (state.Stop) return;
        state.Visited++;
        foreach (var file in SafeFiles(dir, state.Name))
        {
            state.Found.Add(Display(file));
            if (state.Found.Count >= MaxMatches) return;
        }
        foreach (var child in SafeDirs(dir))
        {
            if (state.Stop) return;
            if (!CanEnter(child)) continue;
            Search(child, state);
        }
    }

    private static string? SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return IsDatabaseFile(name) ? name : null;
    }

    private static List<string> SafeFiles(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir, name).Take(MaxMatches).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static List<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool CanEnter(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name) || SkipNames.Contains(name))
            return false;
        try
        {
            var attr = File.GetAttributes(path);
            var blocked = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
            return (attr & blocked) == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsDatabaseFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".fdb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gdb", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dcf", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(string path) => path.Replace('\\', '/');

    private sealed class SearchState
    {
        public SearchState(string name, DateTime deadline)
        {
            Name = name;
            Deadline = deadline;
        }

        public string Name { get; }
        public DateTime Deadline { get; }
        public List<string> Found { get; } = [];
        public int Visited { get; set; }
        public bool Stop => Found.Count >= MaxMatches || Visited >= MaxDirs || DateTime.UtcNow >= Deadline;
    }
}
