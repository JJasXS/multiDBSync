using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Tests;

public class GlAccountMapperTests
{
    [Fact]
    public void ToApiPayload_uses_dest_parent_and_zero_dockey()
    {
        var row = new GlAccountRow
        {
            DocKey = 99,
            Parent = 1,
            Code = "300-000",
            Description = "TRADE DEBTORS",
            AccType = "CA",
            SpecialAccType = "DC",
            CashFlowType = 0
        };

        var payload = GlAccountMapper.ToApiPayload(row, destParentDocKey: 42);
        // Create payload omits dockey (SDK New); updates set dockey via ToUpdatePayload.
        Assert.False(payload.ContainsKey("dockey"));
        Assert.Equal(42, payload["parent"]);
        Assert.Equal("300-000", payload["code"]);
        Assert.Equal("TRADE DEBTORS", payload["description"]);
        Assert.Equal("CA", payload["acctype"]);
        Assert.Equal("DC", payload["specialacctype"]);

        var root = GlAccountMapper.ToApiPayload(row, destParentDocKey: 0);
        Assert.Equal(-1, root["parent"]);
    }

    [Fact]
    public void ResolveDestParentDocKey_matches_by_code()
    {
        var dest = new List<GlAccountRow>
        {
            new() { DocKey = 10, Code = "_CA_", Description = "CURRENT ASSETS" },
            new() { DocKey = 11, Code = "300-000", Description = "TRADE DEBTORS", Parent = 10 }
        };

        Assert.Equal(10, GlAccountMapper.ResolveDestParentDocKey("_CA_", dest));
        Assert.Equal(0, GlAccountMapper.ResolveDestParentDocKey("", dest));
        Assert.Equal(0, GlAccountMapper.ResolveDestParentDocKey("MISSING", dest));
    }

    [Fact]
    public void OrderCodesParentsFirst_sorts_by_level()
    {
        var chart = new List<GlAccountRow>
        {
            new() { DocKey = 1, Code = "_CA_", Description = "CURRENT ASSETS", Level = 0 },
            new() { DocKey = 2, Code = "300-000", Description = "TRADE DEBTORS", Parent = 1, Level = 1 },
            new() { DocKey = 3, Code = "300-C0001", Description = "Customer", Parent = 2, Level = 2 }
        };

        var ordered = GlAccountMapper.OrderCodesParentsFirst(
            ["300-C0001", "_CA_", "300-000"], chart);

        Assert.Equal(["_CA_", "300-000", "300-C0001"], ordered);
    }

    [Fact]
    public void ToMasterRecord_round_trips_parent_code()
    {
        var chart = new List<GlAccountRow>
        {
            new() { DocKey = 1, Code = "_CA_", Description = "CURRENT ASSETS", Level = 0 },
            new() { DocKey = 2, Code = "300-000", Description = "TRADE DEBTORS", Parent = 1, Level = 1, AccType = "CA" }
        };
        var byKey = chart.ToDictionary(a => a.DocKey);
        var master = GlAccountMapper.ToMasterRecord(chart[1], byKey);
        Assert.Equal("300-000", master.Code);
        Assert.Equal("_CA_", GlAccountMapper.ParentCodeOf(master));

        var back = GlAccountMapper.FromMasterRecord(master);
        Assert.Equal("300-000", back.Code);
        Assert.Equal("CA", back.AccType);
    }
}
