using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class PartyFieldComparerTests
{
    [Fact]
    public void Differs_false_when_compared_fields_match()
    {
        var a = Party("C1", "Acme", "300-000", "A");
        var b = Party("C1", "Acme", "300-000", "A");
        Assert.False(PartyFieldComparer.Differs(a, b));
    }

    [Fact]
    public void Differs_true_when_companyname_changes()
    {
        var a = Party("C1", "Acme", "300-000", "A");
        var b = Party("C1", "Acme Ltd", "300-000", "A");
        Assert.True(PartyFieldComparer.Differs(a, b));
    }

    [Fact]
    public void Differs_uses_description_fallback_for_companyname()
    {
        var a = new MasterRecord
        {
            Code = "C1",
            Description = "FromDesc",
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controlaccount"] = "300-000",
                ["status"] = "A"
            }
        };
        var b = Party("C1", "FromDesc", "300-000", "A");
        Assert.False(PartyFieldComparer.Differs(a, b));
    }

    [Fact]
    public void CollectDifferingCodes_only_shared_codes()
    {
        var source = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = Party("C1", "A", "300-000", "A"),
            ["C2"] = Party("C2", "B", "300-000", "A"),
            ["C3"] = Party("C3", "OnlyMaster", "300-000", "A")
        };
        var dest = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = Party("C1", "A", "300-000", "A"),
            ["C2"] = Party("C2", "B-changed", "300-000", "A")
        };

        var changed = PartyFieldComparer.CollectDifferingCodes(source, dest);
        Assert.Equal(new[] { "C2" }, changed);
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
