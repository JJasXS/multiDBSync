using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;

namespace SqlAccountSync.Tests;

public class StockItemMapperTests
{
    [Fact]
    public void ToApiPayload_includes_official_create_fields()
    {
        var record = new MasterRecord
        {
            Code = "TEST-ITEM",
            Description = "Test Item",
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["stockgroup"] = "C/P",
                ["uom"] = "PCS",
                ["suom"] = "KGS",
                ["creationdate"] = "2026-05-21",
                ["udf_mtype"] = "AL",
                ["udf_formula"] = "ZF/004",
                ["udf_2uom"] = "KGS",
                ["udf_thickness"] = "0.7",
                ["udf_direct"] = "true"
            }
        };

        var item = StockItemMapper.FromMasterRecord(record);
        item.Uoms = [new StockItemUom { Uom = "PCS", IsBase = true }];
        var payload = StockItemMapper.ToApiPayload(item, "TEST-ITEM", "txn1");

        Assert.Equal(0, payload["dockey"]);
        Assert.Equal(true, payload["dirty"]);
        Assert.Equal(0, payload["lastmodified"]);
        Assert.Equal("2026-05-21", payload["creationdate"]);
        Assert.Equal("0", payload["balsqty"]);
        Assert.Equal("0", payload["balsuomqty"]);
        Assert.Equal("ZF/004", payload["udf_formula"]);
        Assert.Equal("AL", payload["udf_mtype"]);
        Assert.Equal(true, payload["udf_direct"]);

        var sdsuom = Assert.IsType<List<Dictionary<string, object?>>>(payload["sdsuom"]);
        var row = Assert.Single(sdsuom);
        Assert.Equal("0", row["mincost"]);
        Assert.Equal("0", row["maxcost"]);
        Assert.Equal("0", row["minprice"]);
        Assert.Equal("0", row["maxprice"]);

        var sdscategory = Assert.IsType<List<Dictionary<string, object?>>>(payload["sdscategory"]);
        Assert.Equal("AL", sdscategory[0]["category"]);
    }
}
