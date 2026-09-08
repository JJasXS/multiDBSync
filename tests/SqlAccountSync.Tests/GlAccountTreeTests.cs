using SqlAccountSync.Services.Accounts;

namespace SqlAccountSync.Tests;

public class GlAccountTreeTests
{
    [Fact]
    public void ParseAndOrder_assigns_levels_for_parent_child()
    {
        const string json = """
            {
              "data": [
                { "dockey": 1, "parent": 0, "code": "CA", "description": "CURRENT ASSETS", "acctype": "A", "specialacctype": "", "cashflowtype": 0 },
                { "dockey": 2, "parent": 1, "code": "300-000", "description": "TRADE DEBTORS", "acctype": "A", "specialacctype": "CA", "cashflowtype": 0 },
                { "dockey": 3, "parent": 2, "code": "300-001", "description": "SUB DEBTOR", "acctype": "A", "specialacctype": "", "cashflowtype": 0 }
              ]
            }
            """;

        var rows = GlAccountTree.ParseAndOrder(json);
        Assert.Equal(3, rows.Count);
        Assert.Equal("CURRENT ASSETS", rows[0].Description);
        Assert.Equal(0, rows[0].Level);
        Assert.Equal("TRADE DEBTORS", rows[1].Description);
        Assert.Equal(1, rows[1].Level);
        Assert.Equal("  ", rows[1].Indent);
        Assert.Equal("SUB DEBTOR", rows[2].Description);
        Assert.Equal(2, rows[2].Level);
        Assert.Equal("    ", rows[2].Indent);
        Assert.Equal("    SUB DEBTOR", rows[2].DisplayName);
    }
}
