using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class RestDependencyCatalogTests
{
    [Fact]
    public void Catalog_Lists_BypassAndFallback_Dependencies()
    {
        var ids = RestDependencyCatalog.All.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("accounts-controller-create", ids);
        Assert.Contains("accounts-controller-list-api", ids);
        Assert.Contains("item-prereq-stockgroup", ids);
        Assert.Contains("item-prereq-uom", ids);
        Assert.Contains("txn-goodsreceived", ids);
        Assert.Contains("fb-timeout-fallback", ids);
        Assert.Contains("write-adapter-default", ids);
        Assert.Contains("apicred-rest-only-gates", ids);
        Assert.Contains("api-client-rest-reads", ids);
    }

    [Fact]
    public void VendorQuestions_Cover_Gl_Gr_StockGroup_Uom_Host()
    {
        var text = string.Join('\n', RestDependencyCatalog.VendorSupportQuestions);
        Assert.Contains("GL_Acc", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PH_GR", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stock Group", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UOM", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows Service", text, StringComparison.OrdinalIgnoreCase);
    }
}
