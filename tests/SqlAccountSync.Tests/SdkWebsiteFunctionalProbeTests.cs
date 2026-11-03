using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkWebsiteFunctionalProbeTests
{
    [Fact]
    public async Task WebsiteProbe_BlocksWithoutBackup_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkWebsiteFunctionalProbe(factory);
        var result = await probe.RunAsync(new SqlAccWebsiteFunctionalRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            BackupPath: "",
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies));

        Assert.True(result.SafetyBlocked);
        Assert.False(result.Success);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("backup", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebsiteProbe_AcceptsSupervisorConfirm_StillNeedsLanAuth()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkWebsiteFunctionalProbe(factory);
        var confirm = SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(SqlAccProjectTargets.MiracleFdb);
        var result = await probe.RunAsync(new SqlAccWebsiteFunctionalRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            BackupPath: "",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: "",
            SupervisorManagedBackupConfirm: confirm));

        Assert.True(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("authorize", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CountingFactory : ISqlAccComAppFactory
    {
        public int CreateCount;
        public ISqlAccComApp Create()
        {
            Interlocked.Increment(ref CreateCount);
            throw new InvalidOperationException("COM must not be created in safety-gate tests.");
        }
    }
}
