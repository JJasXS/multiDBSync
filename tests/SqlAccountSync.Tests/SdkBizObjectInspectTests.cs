using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkBizObjectInspectTests
{
    [Fact]
    public void ResolveInspectList_IncludesFocusAndRelated()
    {
        var all = new[] { "Agent", "GL_Acc", "PH_PI", "ST_ITEM", "ZZ_OTHER", "ST_GROUP", "PH_GR" };
        var list = SqlAccBizObjectInspectTargets.ResolveInspectList(all);
        Assert.Contains(list, n => n.Equals("GL_Acc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("PH_GR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_GROUP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_ITEM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list, n => n.Equals("ZZ_OTHER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveTargetsOnlyList_ExcludesBroadNoise_AndLabelsStRc()
    {
        var all = new[]
        {
            "Agent", "GL_Acc", "GL_CB", "PH_PI", "PH_GR", "ST_ITEM", "ST_GROUP", "ST_CATEGORY",
            "ST_RC", "ST_UOM", "ZZ_OTHER", "Maintain Account", "Goods Received Note"
        };
        var list = SqlAccBizObjectInspectTargets.ResolveTargetsOnlyList(all);

        Assert.Contains(list, n => n.Equals("GL_Acc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("PH_GR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_GROUP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_CATEGORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_ITEM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_RC", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("ST_UOM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, n => n.Equals("Goods Received Note", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list, n => n.Equals("ZZ_OTHER", StringComparison.OrdinalIgnoreCase));
        // Full related mode would include PH_PI / GL_CB; targets-only should not unless they match candidates.
        Assert.DoesNotContain(list, n => n.Equals("PH_PI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list, n => n.Equals("GL_CB", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list, n => n.Equals("Agent", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("NOT Purchase GR", SqlAccBizObjectInspectTargets.ClassifyTargetRole("ST_RC"), StringComparison.OrdinalIgnoreCase);
        Assert.False(SqlAccBizObjectInspectTargets.IsGoodsReceivedCandidate("ST_RC"));
        Assert.True(SqlAccBizObjectInspectTargets.IsGoodsReceivedCandidate("PH_GR"));
        Assert.True(SqlAccBizObjectInspectTargets.IsStockGroupCandidate("ST_CATEGORY"));
        Assert.True(SqlAccBizObjectInspectTargets.IsUomCandidate("ST_UOM"));
    }

    [Fact]
    public void FormatCompactReport_OmitsCatalogAndXml_AndCapsFields()
    {
        var fields = Enumerable.Range(1, 20)
            .Select(i => new SqlAccBizObjectFieldInfo($"F{i}", "String", "10"))
            .ToList();
        var report = new SqlAccBizObjectInspectReport(
            Success: true,
            SafetyBlocked: false,
            Message: "ok",
            AllBizObjectNames: Enumerable.Range(1, 500).Select(i => $"OBJ{i}").ToList(),
            Inspected:
            [
                new SqlAccBizObjectMetadata(
                    "GL_Acc", true, null,
                    [new SqlAccBizObjectDataSetInfo("MainDataSet", fields)],
                    "<huge xml should not appear>"),
                new SqlAccBizObjectMetadata("PH_GR", false, "Find returned null.", [], ""),
            ],
            TargetsOnly: true);

        var text = SqlAccBizObjectInspectTargets.FormatCompactReport(report);
        Assert.Contains("Found: True", text);
        Assert.Contains("Found: False", text);
        Assert.Contains("CUD Support: Unverified", text);
        Assert.Contains("=== Summary ===", text);
        Assert.Contains("| GL_Acc |", text);
        Assert.DoesNotContain("<huge xml", text);
        Assert.DoesNotContain("OBJ1", text); // catalog not dumped
        Assert.Contains("+5 more omitted", text); // 20 - 15
        Assert.Contains("Catalog size (not listed): 500", text);
    }

    [Fact]
    public void FormatFullFieldMap_IncludesAllFieldsAndXml()
    {
        var fields = Enumerable.Range(1, 20)
            .Select(i => new SqlAccBizObjectFieldInfo($"F{i}", "String", "10"))
            .ToList();
        var report = new SqlAccBizObjectInspectReport(
            Success: true,
            SafetyBlocked: false,
            Message: "ok",
            DcfPath: @"\\share\SQLACC.DCF",
            DatabaseFileName: "ACC-MIRACLE.FDB",
            Inspected:
            [
                new SqlAccBizObjectMetadata(
                    "GL_Acc", true, null,
                    [new SqlAccBizObjectDataSetInfo("MainDataSet", fields)],
                    "<fields><f name=\"CODE\"/></fields>"),
            ],
            TargetsOnly: true);

        var text = SqlAccBizObjectInspectTargets.FormatFullFieldMap(report);
        Assert.Contains("## GL_Acc", text);
        Assert.Contains("| F20 |", text);
        Assert.Contains("<fields><f name=\"CODE\"/></fields>", text);
        Assert.Contains("CUD is NOT verified", text);
        Assert.DoesNotContain("+5 more omitted", text);
    }

    [Fact]
    public async Task InspectProbe_SafetyGate_BlocksWithoutAuthorize()
    {
        var probe = new SqlAccSdkBizObjectInspectProbe(new FixedFactory(new FakeInspectCom()));
        var result = await probe.RunAsync(new SqlAccBizObjectInspectRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: null));
        Assert.True(result.SafetyBlocked);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task InspectProbe_TargetsOnly_DoesNotInspectNoise()
    {
        var fake = new FakeInspectCom();
        var probe = new SqlAccSdkBizObjectInspectProbe(new FixedFactory(fake));
        var result = await probe.RunAsync(new SqlAccBizObjectInspectRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies,
            TargetsOnly: true));

        Assert.True(result.Success);
        Assert.True(result.TargetsOnly);
        Assert.Contains(result.Inspected!, m => m.Name == "GL_Acc" && m.Found);
        Assert.Contains(result.Inspected!, m => m.Name == "PH_GR" && !m.Found);
        Assert.Contains(result.Inspected!, m => m.Name == "ST_GROUP");
        Assert.Contains(result.Inspected!, m => m.Name == "ST_CATEGORY");
        Assert.DoesNotContain(result.Inspected!, m => m.Name == "PH_PI");
        Assert.DoesNotContain(result.Inspected!, m => m.Name == "Agent");
        Assert.Equal(0, fake.CudCalls);
    }

    [Fact]
    public async Task InspectProbe_ListsAndInspects_WithoutCud()
    {
        var fake = new FakeInspectCom();
        var probe = new SqlAccSdkBizObjectInspectProbe(new FixedFactory(fake));
        var result = await probe.RunAsync(new SqlAccBizObjectInspectRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies));

        Assert.True(result.Success);
        Assert.True(result.LoginOk);
        Assert.True(result.IsLoginOk);
        Assert.True(result.LogoutOk);
        Assert.NotNull(result.AllBizObjectNames);
        Assert.Contains(result.AllBizObjectNames!, n => n == "GL_Acc");
        Assert.Contains(result.Inspected!, m => m.Name == "GL_Acc" && m.Found);
        Assert.Contains(result.Inspected!, m => m.Name == "PH_GR" && !m.Found);
        Assert.Equal(0, fake.CudCalls);
    }

    private sealed class FixedFactory(ISqlAccComApp app) : ISqlAccComAppFactory
    {
        public ISqlAccComApp Create() => app;
    }

    private sealed class FakeInspectCom : ISqlAccComApp
    {
        public int CudCalls { get; private set; }
        public bool IsLogin { get; private set; }

        public void Login(string userName, string password, string dcfPath, string databaseFileName) =>
            IsLogin = true;

        public void Logout() => IsLogin = false;

        public int QueryRecordCount(string selectSql) => 0;

        public IReadOnlyList<string> ListBizObjectNames() =>
        [
            "Agent", "AR_CUSTOMER", "GL_Acc", "GL_CB", "PH_PI", "ST_ITEM", "ST_GROUP", "ST_CATEGORY", "ST_RC"
        ];

        public SqlAccBizObjectMetadata InspectBizObject(string name)
        {
            if (string.Equals(name, "PH_GR", StringComparison.OrdinalIgnoreCase))
                return new SqlAccBizObjectMetadata(name, false, "Find returned null.", [], "");

            if (string.Equals(name, "GL_Acc", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlAccBizObjectMetadata(
                    name, true, null,
                    [
                        new SqlAccBizObjectDataSetInfo("MainDataSet",
                        [
                            new SqlAccBizObjectFieldInfo("CODE", "String", "10"),
                            new SqlAccBizObjectFieldInfo("DESCRIPTION", "String", "100"),
                        ])
                    ],
                    "<fields/>");
            }

            return new SqlAccBizObjectMetadata(
                name, true, null,
                [new SqlAccBizObjectDataSetInfo("MainDataSet", [new SqlAccBizObjectFieldInfo("CODE", "String", "10")])],
                "");
        }

        public bool AgentExists(string code) => false;
        public void AgentCreate(string code, string description) => CudCalls++;
        public void AgentUpdate(string code, string description) => CudCalls++;
        public string AgentReadDescription(string code) => "";
        public void AgentDelete(string code) => CudCalls++;
        public void Dispose() { }
    }
}
