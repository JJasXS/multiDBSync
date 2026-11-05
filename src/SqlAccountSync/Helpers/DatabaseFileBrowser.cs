using Microsoft.Win32;

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
    private static readonly string[] PreferredDcfNames = ["SQLACC.DCF", "Default.DCF"];
    private static readonly string FallbackDcfPath = @"C:\eStream\SQLAccounting\Share\SQLACC.DCF";
    private static readonly string[] SqlAccountRegistryRoots =
    [
        @"Software\eStream\SQL Account",
        @"Software\WOW6432Node\eStream\SQL Account"
    ];
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "AppData", "WinSxS",
        "node_modules", ".git"
    };

    /// <summary>
    /// Prefers an existing SQLACC.DCF, then Default.DCF.
    /// Uses SQL Account registry Share/DB dirs when the install path varies, then scans known roots.
    /// </summary>
    public static string FindPreferredDcfPath()
    {
        foreach (var name in PreferredDcfNames)
        {
            foreach (var dir in DiscoverShareDirs())
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return Display(candidate);
            }
        }

        var registryDcfs = DiscoverRegistryDefaultDcfs()
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var registryPick = registryDcfs.FirstOrDefault(IsSqlAccDcf)
            ?? registryDcfs.FirstOrDefault(IsDefaultDcf);
        if (registryPick is not null)
            return Display(registryPick);

        foreach (var name in PreferredDcfNames)
        {
            var matches = FindByName(name);
            if (matches.Count > 0)
                return matches[0];
        }

        return Display(FallbackDcfPath);
    }

    public static IReadOnlyList<string> FindByName(string? fileName)
    {
        var name = SafeName(fileName);
        if (name is null)
            return [];

        // Only approved database roots — never scan entire fixed drives (path disclosure / DoS).
        var state = new SearchState(name, DateTime.UtcNow + SearchLimit);
        foreach (var root in SearchRoots().Where(Directory.Exists))
            Search(root, state);
        return state.Found;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DiscoverShareDirs().Concat(Preferred))
        {
            if (!seen.Add(root)) continue;
            yield return root;
        }
    }

    private static IEnumerable<string> DiscoverShareDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var root in SqlAccountRegistryRoots)
            {
                foreach (var dir in ReadSqlAccountDirs(hive, root))
                {
                    if (!seen.Add(dir)) continue;
                    yield return dir;
                }
            }
        }

        foreach (var root in Preferred)
        {
            if (!seen.Add(root)) continue;
            yield return root;
        }
    }

    private static IEnumerable<string> ReadSqlAccountDirs(RegistryKey hive, string rootPath)
    {
        using var root = hive.OpenSubKey(rootPath);
        if (root is null) yield break;

        foreach (var version in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(version);
            if (key is null) continue;

            foreach (var valueName in new[] { "Dir.Share", "Dir.DB" })
            {
                var dir = NormalizeDir(key.GetValue(valueName) as string);
                if (dir is not null)
                    yield return dir;
            }
        }
    }

    private static IEnumerable<string> DiscoverRegistryDefaultDcfs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var rootPath in SqlAccountRegistryRoots)
            {
                using var root = hive.OpenSubKey(rootPath);
                if (root is null) continue;

                foreach (var version in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(version);
                    if (key is null) continue;

                    foreach (var valueName in new[] { "Connection.Default", "Connection.Pool" })
                    {
                        foreach (var raw in ReadRegistryStrings(key.GetValue(valueName)))
                        {
                            var path = ParseDcfReference(raw);
                            if (path is null || !seen.Add(path)) continue;
                            yield return path;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ReadRegistryStrings(object? value)
    {
        if (value is string text)
        {
            yield return text;
            yield break;
        }

        if (value is string[] multi)
        {
            foreach (var item in multi)
                yield return item;
        }
    }

    private static string? ParseDcfReference(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
        if (text.Length == 0) return null;

        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            text = Uri.UnescapeDataString(text["file:///".Length..].Replace('/', '\\'));
        else if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            text = Uri.UnescapeDataString(text["file://".Length..].Replace('/', '\\'));

        text = text.Trim();
        if (!text.EndsWith(".dcf", StringComparison.OrdinalIgnoreCase))
            return null;
        if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        var name = Path.GetFileName(text);
        if (!PreferredDcfNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return null;
        return Path.GetFullPath(text);
    }

    private static string? NormalizeDir(string? raw)
    {
        var dir = (raw ?? "").Trim().Trim('"');
        if (dir.Length == 0) return null;
        if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try
        {
            return Path.GetFullPath(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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

    private static bool IsSqlAccDcf(string path) =>
        Path.GetFileName(path).Equals("SQLACC.DCF", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefaultDcf(string path) =>
        Path.GetFileName(path).Equals("Default.DCF", StringComparison.OrdinalIgnoreCase);

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
