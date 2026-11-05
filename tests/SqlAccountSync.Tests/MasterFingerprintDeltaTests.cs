using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Tests;

public class MasterFingerprintDeltaTests
{
    [Fact]
    public void Diff_empty_stored_is_seed_only_with_no_jobs()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = "aaa",
            ["C2"] = "bbb"
        };

        var result = MasterFingerprintDelta.Diff(current, new Dictionary<string, string>());

        Assert.True(result.SeedOnly);
        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_unchanged_master_ignores_sub_drift()
    {
        var hash = PartyFieldComparer.ContentHash(Party("C1", "Acme", "300-000", "A"));
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = hash
        };
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = hash
        };

        // Sub may have different fields; fingerprints only care about master hash stability.
        var result = MasterFingerprintDelta.Diff(current, stored);

        Assert.False(result.SeedOnly);
        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_master_hash_change_is_changed()
    {
        var before = PartyFieldComparer.ContentHash(Party("C1", "Acme", "300-000", "A"));
        var after = PartyFieldComparer.ContentHash(Party("C1", "Acme Ltd", "300-000", "A"));
        Assert.NotEqual(before, after);

        var result = MasterFingerprintDelta.Diff(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["C1"] = after },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["C1"] = before });

        Assert.Equal(new[] { "C1" }, result.Changed);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_master_add_is_added()
    {
        var hash = PartyFieldComparer.ContentHash(Party("C2", "NewCo", "300-000", "A"));
        var result = MasterFingerprintDelta.Diff(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["C1"] = "aaa",
                ["C2"] = hash
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["C1"] = "aaa"
            });

        Assert.Equal(new[] { "C2" }, result.Added);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_master_remove_is_removed()
    {
        var result = MasterFingerprintDelta.Diff(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["C1"] = "aaa"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["C1"] = "aaa",
                ["C2"] = "bbb"
            });

        Assert.Equal(new[] { "C2" }, result.Removed);
        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void Party_ContentHash_stable_for_same_fields()
    {
        var a = PartyFieldComparer.ContentHash(Party("C1", "Acme", "300-000", "A"));
        var b = PartyFieldComparer.ContentHash(Party("C1", "Acme", "300-000", "A"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Gl_ContentHash_includes_parent()
    {
        var row = new GlAccountRow
        {
            DocKey = 1,
            Parent = 0,
            Code = "100-000",
            Description = "Cash",
            AccType = "A"
        };
        var withParent = GlAccountComparer.ContentHash(row, "ASSETS");
        var withoutParent = GlAccountComparer.ContentHash(row, "");
        Assert.NotEqual(withParent, withoutParent);
    }

    [Fact]
    public void Gl_BuildHashes_indexes_by_code()
    {
        var chart = new List<GlAccountRow>
        {
            new() { DocKey = 1, Parent = 0, Code = "100-000", Description = "Cash" },
            new() { DocKey = 2, Parent = 1, Code = "100-001", Description = "Petty" }
        };

        var hashes = GlAccountComparer.BuildHashes(chart);
        Assert.Equal(2, hashes.Count);
        Assert.True(hashes.ContainsKey("100-000"));
        Assert.True(hashes.ContainsKey("100-001"));
    }

    [Fact]
    public void AutoPushJobRules_recognizes_system_users()
    {
        Assert.True(AutoPushJobRules.IsAutoPushUser(AutoPushJobRules.PartyUser));
        Assert.True(AutoPushJobRules.IsAutoPushUser(AutoPushJobRules.AccountUser));
        Assert.False(AutoPushJobRules.IsAutoPushUser("admin"));
        Assert.False(AutoPushJobRules.IsAutoPushUser(null));
    }

    private static MasterRecord Party(string code, string name, string control, string status) =>
        new()
        {
            Code = code,
            Description = name,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["companyname"] = name,
                ["controlaccount"] = control,
                ["status"] = status
            }
        };
}
