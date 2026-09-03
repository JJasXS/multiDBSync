using SqlAccountSync.Models;
using SqlAccountSync.Services.Documents;

namespace SqlAccountSync.Tests;

public class GoodsReceivedMapperTests
{
    [Fact]
    public void ToApiPayload_includes_gr_fields_without_default_udfs()
    {
        var doc = new DocumentHeader
        {
            DocType = "GR",
            DocNo = "DO-001",
            DocDate = new DateTime(2026, 6, 22),
            PartyCode = "400-H0001",
            PartyName = "HQ Supplier",
            Description = "Test GR",
            Currency = "----",
            CurrencyRate = "1",
            Amount = 100m,
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["country"] = "MY",
                ["branchname"] = "BILLING",
                ["dcountry"] = "MY"
            },
            Lines =
            [
                new DocumentLine
                {
                    Seq = 1000,
                    ItemCode = "Dogs",
                    Description = "Dogs",
                    Qty = 5,
                    Uom = "UNIT",
                    Rate = "1",
                    UnitPrice = 20,
                    Amount = 100,
                    Warehouse = "----",
                    Project = "----"
                }
            ]
        };

        var payload = GoodsReceivedMapper.ToApiPayload(doc, "TXN-TEST-001");

        Assert.Equal("2026-06-22", payload["docdate"]);
        Assert.Equal("400-H0001", payload["code"]);
        Assert.Equal(false, payload["cancelled"]);
        Assert.Equal("Test GR", payload["description"]);
        Assert.Contains("SYNC|TXN-TEST-001", payload["note"]?.ToString() ?? "");
        Assert.False(payload.ContainsKey("udf_stdprice"));

        var lines = Assert.IsType<List<Dictionary<string, object?>>>(payload["sdsdocdetail"]);
        var line = Assert.Single(lines);
        Assert.Equal("Dogs", line["itemcode"]);
        Assert.Equal("5", line["receiveqty"]);
        Assert.Equal("5", line["qty"]);
        Assert.Equal("UNIT", line["uom"]);
        Assert.Equal(true, line["changed"]);
    }

    [Fact]
    public void ToApiPayload_includes_line_udfs_when_present()
    {
        var doc = new DocumentHeader
        {
            DocDate = DateTime.Today,
            PartyCode = "SUP01",
            PartyName = "Supplier",
            Lines =
            [
                new DocumentLine
                {
                    ItemCode = "ITEM1",
                    Qty = 1,
                    Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["udf_thickness"] = "10",
                        ["udf_formula"] = "ZF/004"
                    }
                }
            ]
        };

        var payload = GoodsReceivedMapper.ToApiPayload(doc, "");
        var lines = Assert.IsType<List<Dictionary<string, object?>>>(payload["sdsdocdetail"]);
        var line = Assert.Single(lines);
        Assert.Equal("10", line["udf_thickness"]);
        Assert.Equal("ZF/004", line["udf_formula"]);
    }
}
