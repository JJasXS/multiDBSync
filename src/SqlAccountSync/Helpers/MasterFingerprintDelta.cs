namespace SqlAccountSync.Helpers;

/// <summary>
/// Diffs current master content hashes against stored fingerprints.
/// Empty stored set means seed-only (no jobs).
/// </summary>
public static class MasterFingerprintDelta
{
    public sealed record Result(
        bool SeedOnly,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Changed,
        IReadOnlyList<string> Removed);

    public static Result Diff(
        IReadOnlyDictionary<string, string> currentHashes,
        IReadOnlyDictionary<string, string> storedHashes)
    {
        if (storedHashes.Count == 0)
            return new Result(true, [], [], []);

        var added = new List<string>();
        var changed = new List<string>();
        foreach (var (code, hash) in currentHashes)
        {
            if (!storedHashes.TryGetValue(code, out var prev))
            {
                added.Add(code);
                continue;
            }

            if (!string.Equals(prev, hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(code);
        }

        var removed = storedHashes.Keys
            .Where(code => !currentHashes.ContainsKey(code))
            .ToList();

        added.Sort(StringComparer.OrdinalIgnoreCase);
        changed.Sort(StringComparer.OrdinalIgnoreCase);
        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return new Result(false, added, changed, removed);
    }
}
