namespace SqlAccountSync.Helpers;

public static class DatabasePlanLimit
{
    public static string? NewDatabaseMessage(int? max, int activeCount)
    {
        if (max is not int cap || activeCount < cap)
            return null;
        return ActiveMessage(cap);
    }

    public static string? NewLinksMessage(int? max, IEnumerable<int> activeLinkedIds, int masterId, IEnumerable<int> subIds)
    {
        if (max is not int cap)
            return null;
        var involved = new HashSet<int>(activeLinkedIds) { masterId };
        foreach (var id in subIds)
            involved.Add(id);
        return involved.Count <= cap ? null : ActiveMessage(cap);
    }

    public static string ActiveMessage(int cap) => $"The plan allows {cap} active databases.";
}
