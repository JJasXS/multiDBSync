using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkAgentWriteProbeTests
{
    [Fact]
    public async Task WriteProbe_BlocksWithoutBackup_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkAgentWriteProbe(factory);
        var result = await probe.RunAsync(new SqlAccAgentWriteRequest(
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
    public async Task WriteProbe_BlocksMissingBackupFile_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkAgentWriteProbe(factory);
        var result = await probe.RunAsync(new SqlAccAgentWriteRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            BackupPath: @"C:\does-not-exist\ACC-MIRACLE.fbk",
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies));

        Assert.True(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task WriteProbe_BlocksForbiddenUamat_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkAgentWriteProbe(factory);
        var backup = Path.Combine(Path.GetTempPath(), "acc-miracle-poc-test.fbk");
        await File.WriteAllTextAsync(backup, "fake-backup-marker");
        try
        {
            var result = await probe.RunAsync(new SqlAccAgentWriteRequest(
                DcfPath: SqlAccProjectTargets.SharedDcf,
                DatabaseFileName: SqlAccProjectTargets.ForbiddenUamatFdb,
                UserName: "ADMIN",
                Password: "x",
                BackupPath: backup,
                AuthorizeLanRead: true,
                AuthorizedExactTarget: SqlAccProjectTargets.SharedDcf + "|" + SqlAccProjectTargets.ForbiddenUamatFdb));

            Assert.True(result.SafetyBlocked);
            Assert.Equal(0, factory.CreateCount);
            Assert.Contains("not an authorized project", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public async Task WriteProbe_FullCrud_WithFakeCom_WhenBackupExists()
    {
        var backup = Path.Combine(Path.GetTempPath(), "acc-uamat-poc-test.fbk");
        await File.WriteAllTextAsync(backup, "fake-backup-marker");
        try
        {
            var fake = new FakeAgentCom();
            var probe = new SqlAccSdkAgentWriteProbe(new FixedFactory(fake), new StaWorkQueue());
            var code = "SDKPOCUNIT01";
            var result = await probe.RunAsync(new SqlAccAgentWriteRequest(
                DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
                DatabaseFileName: "ACC-DEMO.FDB",
                UserName: "ADMIN",
                Password: "x",
                BackupPath: backup,
                AuthorizeLanRead: false,
                AuthorizedExactTarget: "",
                AgentCode: code,
                CreateDescription: "create-desc",
                UpdateDescription: "update-desc"));

            Assert.True(result.Success, result.Message);
            Assert.True(result.CreateOk);
            Assert.True(result.ReadAfterCreateOk);
            Assert.True(result.UpdateOk);
            Assert.True(result.ReadAfterUpdateOk);
            Assert.True(result.DeleteOk);
            Assert.True(result.FinalGoneOk);
            Assert.True(result.LogoutOk);
            Assert.True(result.ComCleanupOk);
            Assert.False(fake.Agents.ContainsKey(code));
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void NewUniqueAgentCode_HasPocPrefix()
    {
        var code = SqlAccWritePocSafety.NewUniqueAgentCode();
        Assert.StartsWith("SDKPOC", code, StringComparison.OrdinalIgnoreCase);
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

    private sealed class FixedFactory(ISqlAccComApp app) : ISqlAccComAppFactory
    {
        public ISqlAccComApp Create() => app;
    }

    private sealed class FakeAgentCom : ISqlAccComApp
    {
        public Dictionary<string, string> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsLogin { get; private set; }

        public void Login(string userName, string password, string dcfPath, string databaseFileName) => IsLogin = true;
        public void Logout() => IsLogin = false;
        public int QueryRecordCount(string selectSql) => 0;
        public bool AgentExists(string code) => Agents.ContainsKey(code);
        public void AgentCreate(string code, string description) => Agents[code] = description;
        public void AgentUpdate(string code, string description) => Agents[code] = description;
        public string AgentReadDescription(string code) => Agents[code];
        public void AgentDelete(string code) => Agents.Remove(code);
        public void Dispose() { }
    }
}
