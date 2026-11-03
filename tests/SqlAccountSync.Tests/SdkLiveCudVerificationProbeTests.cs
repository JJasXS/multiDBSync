using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkLiveCudVerificationProbeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData(0, false)]
    [InlineData(0L, false)]
    [InlineData(42, true)]
    [InlineData("ABC", true)]
    public void IsPresentKey_TreatsComMissingSentinelsAsAbsent(object? key, bool expected)
    {
        Assert.Equal(expected, SqlAccBizObjectWriter.IsPresentKey(key));
    }

    [Fact]
    public void IsPresentKey_TreatsDbNullAsAbsent()
    {
        Assert.False(SqlAccBizObjectWriter.IsPresentKey(DBNull.Value));
    }

    [Fact]
    public async Task LiveCud_BlocksWithoutBackup_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkLiveCudVerificationProbe(factory);
        var result = await probe.RunAsync(new SqlAccLiveCudRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            BackupPath: "",
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies,
            Suite: SqlAccLiveCudSuite.GlAccount));

        Assert.True(result.SafetyBlocked);
        Assert.False(result.Success);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("backup", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveCud_AcceptsSupervisorManagedBackupConfirm_StillBlocksUnauthorizedLan()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkLiveCudVerificationProbe(factory);
        var confirm = SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(SqlAccProjectTargets.MiracleFdb);
        var result = await probe.RunAsync(new SqlAccLiveCudRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            BackupPath: "",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: "",
            Suite: SqlAccLiveCudSuite.StockGroup,
            SupervisorManagedBackupConfirm: confirm));

        Assert.True(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("LAN", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupervisorBackupConfirm_RejectsWrongCompanyToken()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlAccWritePocSafety.EnsureRestorableBackup(
                SqlAccProjectTargets.MiracleFdb,
                backupPath: null,
                supervisorManagedBackupConfirm: SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(
                    SqlAccProjectTargets.OnyatFdb)));
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupervisorBackupConfirm_RejectsForbiddenUamat()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlAccWritePocSafety.EnsureRestorableBackup(
                SqlAccProjectTargets.ForbiddenUamatFdb,
                backupPath: null,
                supervisorManagedBackupConfirm:
                    SqlAccWritePocSafety.SupervisorManagedBackupPrefix + SqlAccProjectTargets.ForbiddenUamatFdb));
        Assert.Contains("not an authorized project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupervisorBackupConfirm_AllowsMiracleExactToken()
    {
        SqlAccWritePocSafety.EnsureRestorableBackup(
            SqlAccProjectTargets.MiracleFdb,
            backupPath: null,
            supervisorManagedBackupConfirm: SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(
                SqlAccProjectTargets.MiracleFdb));
    }

    [Fact]
    public void SupervisorBackupConfirm_AllowsSteppesExactToken()
    {
        SqlAccWritePocSafety.EnsureRestorableBackup(
            SqlAccProjectTargets.SteppesFdb,
            backupPath: null,
            supervisorManagedBackupConfirm: SqlAccWritePocSafety.BuildSupervisorManagedBackupConfirm(
                SqlAccProjectTargets.SteppesFdb));
    }

    [Fact]
    public void ProjectTargets_ResolvesSteppesAlias()
    {
        Assert.True(SqlAccProjectTargets.TryResolveCompanyAlias("steppes", out var fdb, out var code));
        Assert.Equal(SqlAccProjectTargets.SteppesFdb, fdb);
        Assert.Equal(SqlAccProjectTargets.SteppesDbCode, code);
        Assert.True(SqlAccProjectTargets.IsAuthorizedProjectDatabaseFileName(fdb));
        Assert.Contains(SqlAccProjectTargets.SteppesTargetKey,
            SqlAccProjectTargets.AuthorizedProjectCompanies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SqlAccProjectTargets.SteppesTargetKey,
            SqlAccProjectTargets.AuthorizedBothCompanies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDocumentBizObject_StillMapsGoodsReceived()
    {
        Assert.Equal(SqlAccBizObjectWriter.GoodsReceivedBiz,
            SqlAccBizObjectWriter.ResolveDocumentBizObject("/goodsreceived"));
        Assert.Equal(SqlAccBizObjectWriter.AccountBiz, "GL_ACC");
        Assert.Equal(SqlAccBizObjectWriter.StockGroupBiz, "ST_GROUP");
    }

    private sealed class CountingFactory : ISqlAccComAppFactory
    {
        public int CreateCount { get; private set; }
        public ISqlAccComApp Create()
        {
            CreateCount++;
            throw new InvalidOperationException("COM should not be created.");
        }
    }
}
