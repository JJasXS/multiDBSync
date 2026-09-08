using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class PartnerAutoPushTargetsTests
{
    [Fact]
    public void Resolve_master_with_links_returns_only_linked_subs_with_api()
    {
        var hq = Db(6, "HQ", DatabaseRoles.Master, api: true);
        var s1 = Db(7, "S1", DatabaseRoles.Sub, api: true);
        var s2 = Db(8, "S2", DatabaseRoles.Sub, api: true);
        var other = Db(9, "OTHER", DatabaseRoles.Sub, api: true);
        var links = new[]
        {
            Link(6, 7),
            Link(6, 8)
        };

        var result = PartnerAutoPushTargets.Resolve(hq, [hq, s1, s2, other], links);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.DbId == 7);
        Assert.Contains(result, d => d.DbId == 8);
        Assert.DoesNotContain(result, d => d.DbId == 9);
    }

    [Fact]
    public void Resolve_sub_source_returns_empty()
    {
        var hq = Db(6, "HQ", DatabaseRoles.Master, api: true);
        var s1 = Db(7, "S1", DatabaseRoles.Sub, api: true);
        var links = new[] { Link(6, 7) };

        var result = PartnerAutoPushTargets.Resolve(s1, [hq, s1], links);

        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_filters_inactive_databases()
    {
        var hq = Db(6, "HQ", DatabaseRoles.Master, api: true);
        var s1 = Db(7, "S1", DatabaseRoles.Sub, api: true, active: false);
        var s2 = Db(8, "S2", DatabaseRoles.Sub, api: true);
        var links = new[] { Link(6, 7), Link(6, 8) };

        var result = PartnerAutoPushTargets.Resolve(hq, [hq, s1, s2], links);

        Assert.Single(result);
        Assert.Equal(8, result[0].DbId);
    }

    [Fact]
    public void Resolve_excludes_subs_without_api_credentials()
    {
        var hq = Db(6, "HQ", DatabaseRoles.Master, api: true);
        var s1 = Db(7, "S1", DatabaseRoles.Sub, api: false);
        var s2 = Db(8, "S2", DatabaseRoles.Sub, api: true);
        var links = new[] { Link(6, 7), Link(6, 8) };

        var result = PartnerAutoPushTargets.Resolve(hq, [hq, s1, s2], links);
        var skipped = PartnerAutoPushTargets.ResolveSkippedMissingApi(hq, [hq, s1, s2], links);

        Assert.Single(result);
        Assert.Equal(8, result[0].DbId);
        Assert.Single(skipped);
        Assert.Equal(7, skipped[0].DbId);
    }

    [Fact]
    public void Resolve_both_role_also_pushes_to_linked_subs()
    {
        var both = Db(1, "BOTH", DatabaseRoles.Both, api: true);
        var sub = Db(2, "SUB", DatabaseRoles.Sub, api: true);
        var links = new[] { Link(1, 2) };

        var result = PartnerAutoPushTargets.Resolve(both, [both, sub], links);

        Assert.Single(result);
        Assert.Equal(2, result[0].DbId);
    }

    [Fact]
    public void Resolve_ignores_inactive_links()
    {
        var hq = Db(6, "HQ", DatabaseRoles.Master, api: true);
        var s1 = Db(7, "S1", DatabaseRoles.Sub, api: true);
        var links = new[] { new DatabaseLink { MasterDbId = 6, SubDbId = 7, IsActive = false } };

        Assert.Empty(PartnerAutoPushTargets.Resolve(hq, [hq, s1], links));
    }

    private static RegisteredDatabase Db(int id, string code, string role, bool api, bool active = true) =>
        new()
        {
            DbId = id,
            DbCode = code,
            DisplayName = code,
            DbRole = role,
            IsActive = active,
            ApiBaseUrl = api ? "https://api.example" : "",
            ApiCredRef = api ? "sm:creds" : ""
        };

    private static DatabaseLink Link(int masterId, int subId) =>
        new() { MasterDbId = masterId, SubDbId = subId, IsActive = true };
}
