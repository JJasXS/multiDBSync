using SqlAccountSync.Services.Sdk;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Tests;

public class SdkBundleTests
{
    [Fact]
    public void ParseCompanies_reads_array_by_name()
    {
        var json = """
        {
          "companies": [
            {
              "name": "UAMAT",
              "dcfPath": "C:\\eStream\\SQLAccounting\\Share\\SQLACC.DCF",
              "database": "ACC-UAMAT.FDB",
              "username": "ADMIN",
              "password": "secret"
            },
            {
              "name": "NCC",
              "dcf": "C:\\eStream\\SQLAccounting\\Share\\SQLACC.DCF",
              "db": "ACC-NCC.FDB",
              "user": "ADMIN",
              "pwd": "x"
            }
          ]
        }
        """;

        var map = SdkConnectionBundle.ParseCompanies(json);
        Assert.Equal(2, map.Count);
        Assert.Equal("ACC-UAMAT.FDB", map["UAMAT"].Database);
        Assert.Equal("ADMIN", map["UAMAT"].Username);
        Assert.Equal("secret", map["UAMAT"].Password);
        Assert.Equal("ACC-NCC.FDB", map["NCC"].Database);
    }

    [Fact]
    public void Sanitize_keeps_alnum_dash_underscore()
    {
        Assert.Equal("ACC-UAMAT", SdkConnectionBundle.Sanitize("ACC-UAMAT"));
        Assert.Equal("HQ_01", SdkConnectionBundle.Sanitize("HQ_01"));
        Assert.Equal("A-B", SdkConnectionBundle.Sanitize("A B"));
    }

    [Fact]
    public void ObjectMap_maps_common_entities()
    {
        Assert.Equal("AR_CUSTOMER", SdkObjectMap.MasterBizObject("CUSTOMER"));
        Assert.Equal("PH_PI", SdkObjectMap.DocumentBizObject("/purchaseinvoice"));
        Assert.Equal("ST_XF", SdkObjectMap.DocumentBizObject("/stocktransfer"));
        Assert.Equal("CODE", SdkObjectMap.ToDatasetField("code"));
        Assert.Equal("COMPANYNAME", SdkObjectMap.ToDatasetField("companyname"));
    }
}

public class SdkPayloadApplierTests
{
    [Fact]
    public void ApplyMain_sets_known_fields_on_fake_dataset()
    {
        var ds = new FakeDataSet();
        var applied = SdkPayloadApplier.ApplyMain(ds, new Dictionary<string, object?>
        {
            ["code"] = "C001",
            ["companyname"] = "Acme",
            ["creditlimit"] = "1000",
            ["sdsbranch"] = new List<object>(),
            ["allowexceedcreditlimit"] = true
        });

        Assert.True(applied >= 3);
        Assert.Equal("C001", ds.Values["CODE"]);
        Assert.Equal("Acme", ds.Values["COMPANYNAME"]);
        Assert.Equal(true, ds.Values["ALLOWEXCEEDCREDITLIMIT"]);
    }

    public sealed class FakeDataSet
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeField FindField(string name) => new(this, name);
    }

    public sealed class FakeField(FakeDataSet owner, string name)
    {
        public string AsString
        {
            get => Convert.ToString(owner.Values.GetValueOrDefault(name)) ?? "";
            set => owner.Values[name] = value;
        }

        public bool AsBoolean
        {
            get => owner.Values.TryGetValue(name, out var v) && v is true;
            set => owner.Values[name] = value;
        }

        public int AsInteger
        {
            get => owner.Values.TryGetValue(name, out var v) && v is int i ? i : 0;
            set => owner.Values[name] = value;
        }

        public double AsFloat
        {
            get => owner.Values.TryGetValue(name, out var v) && v is double d ? d : 0;
            set => owner.Values[name] = value;
        }

        public object? Value
        {
            get => owner.Values.GetValueOrDefault(name);
            set => owner.Values[name] = value;
        }

        public void Clear() => owner.Values[name] = "";
    }
}
