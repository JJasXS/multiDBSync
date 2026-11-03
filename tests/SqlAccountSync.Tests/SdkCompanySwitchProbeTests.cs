using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkCompanySwitchProbeTests
{
    [Fact]
    public async Task SwitchProbe_BlocksWithoutLanAuth_NoCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkCompanySwitchProbe(factory);
        var result = await probe.RunAsync(new SqlAccCompanySwitchRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            FirstDatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            SecondDatabaseFileName: SqlAccProjectTargets.OnyatFdb,
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: ""));

        Assert.True(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task SwitchProbe_SucceedsWithFakeCom()
    {
        var fake = new FakeComApp();
        var probe = new SqlAccSdkCompanySwitchProbe(new FixedFactory(fake), new StaWorkQueue());
        var result = await probe.RunAsync(new SqlAccCompanySwitchRequest(
            DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
            FirstDatabaseFileName: "ACC-A.FDB",
            SecondDatabaseFileName: "ACC-B.FDB",
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: ""));

        Assert.True(result.Success, result.Message);
        Assert.True(result.FirstLoginOk);
        Assert.True(result.FirstReadOk);
        Assert.True(result.FirstLogoutOk);
        Assert.True(result.SecondLoginOk);
        Assert.True(result.SecondReadOk);
        Assert.True(result.SecondLogoutOk);
        Assert.Equal(2, fake.LoginCount);
        Assert.Equal(2, fake.LogoutCount);
        Assert.Equal("ACC-B.FDB", fake.LastFdb);
    }

    [Fact]
    public async Task SwitchProbe_RoundTrip_A_B_A_WithFakeCom()
    {
        var fake = new FakeComApp();
        var probe = new SqlAccSdkCompanySwitchProbe(new FixedFactory(fake), new StaWorkQueue());
        var result = await probe.RunAsync(new SqlAccCompanySwitchRequest(
            DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
            FirstDatabaseFileName: "ACC-A.FDB",
            SecondDatabaseFileName: "ACC-B.FDB",
            UserName: "ADMIN",
            Password: "x",
            AuthorizeLanRead: false,
            AuthorizedExactTarget: "",
            ThirdDatabaseFileName: "ACC-A.FDB"));

        Assert.True(result.Success, result.Message);
        Assert.True(result.ThirdLoginOk);
        Assert.True(result.ThirdLogoutOk);
        Assert.Equal(3, fake.LoginCount);
        Assert.Equal(3, fake.LogoutCount);
        Assert.Equal("ACC-A.FDB", fake.LastFdb);
        Assert.Equal(new[] { "ACC-A.FDB", "ACC-B.FDB", "ACC-A.FDB" }, fake.LoginFdbs);
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

    private sealed class FakeComApp : ISqlAccComApp
    {
        public int LoginCount { get; private set; }
        public int LogoutCount { get; private set; }
        public string? LastFdb { get; private set; }
        public List<string> LoginFdbs { get; } = [];
        public bool IsLogin { get; private set; }

        public void Login(string userName, string password, string dcfPath, string databaseFileName)
        {
            LoginCount++;
            LastFdb = databaseFileName;
            LoginFdbs.Add(databaseFileName);
            IsLogin = true;
        }

        public void Logout()
        {
            LogoutCount++;
            IsLogin = false;
        }

        public int QueryRecordCount(string selectSql) => 1;
        public bool AgentExists(string code) => false;
        public void AgentCreate(string code, string description) => throw new NotSupportedException();
        public void AgentUpdate(string code, string description) => throw new NotSupportedException();
        public string AgentReadDescription(string code) => throw new NotSupportedException();
        public void AgentDelete(string code) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
