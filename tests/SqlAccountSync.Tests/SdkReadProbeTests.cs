using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkReadProbeTests
{
    [Fact]
    public void Safety_RequiresExplicitDcfAndFdb()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureExplicitLoginTarget("", "ACC-1.FDB"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureExplicitLoginTarget(@"C:\a\Default.DCF", ""));
        Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureExplicitLoginTarget(@"C:\a\Default.DCF", @"C:\db\ACC-1.FDB"));
    }

    [Theory]
    [InlineData(@"\\192.168.100.201\Share\SQLACC.DCF", true)]
    [InlineData(@"file:///\\192.168.100.201\Share\SQLACC.DCF", true)]
    [InlineData(@"C:\eStream\SQLAccounting\Share\Default.DCF", false)]
    [InlineData(@"\\127.0.0.1\Share\SQLACC.DCF", true)]
    public void Safety_DetectsLanOrUnc(string dcf, bool expected) =>
        Assert.Equal(expected, SqlAccReadPocSafety.IsLanOrUncTarget(dcf));

    [Fact]
    public void Safety_BlocksForbiddenUamat()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureExplicitLoginTarget(
                SqlAccProjectTargets.SharedDcf,
                SqlAccProjectTargets.ForbiddenUamatFdb));
        Assert.Contains("not an authorized project database", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safety_BlocksLanWithoutAuthorization()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                SqlAccProjectTargets.SharedDcf,
                SqlAccProjectTargets.MiracleFdb,
                authorizeLanRead: false,
                authorizedExactTarget: null));
        Assert.Contains("LAN/UNC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safety_BlocksLanWhenAuthorizedTargetMismatch()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                SqlAccProjectTargets.SharedDcf,
                SqlAccProjectTargets.MiracleFdb,
                authorizeLanRead: true,
                authorizedExactTarget: @"\\192.168.100.201\Share\OTHER.DCF|ACC-OTHER.FDB"));
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safety_AllowsLanWhenExactAuthorizationMatches()
    {
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(
            SqlAccProjectTargets.SharedDcf,
            SqlAccProjectTargets.MiracleFdb,
            authorizeLanRead: true,
            authorizedExactTarget: SqlAccProjectTargets.MiracleTargetKey);
    }

    [Fact]
    public void Safety_AllowsLanWhenTargetInSemicolonAllowList()
    {
        SqlAccReadPocSafety.EnsureLiveReadAuthorized(
            SqlAccProjectTargets.SharedDcf,
            SqlAccProjectTargets.OnyatFdb,
            authorizeLanRead: true,
            authorizedExactTarget: SqlAccProjectTargets.AuthorizedBothCompanies);
    }

    [Fact]
    public async Task Probe_ReturnsSafetyBlocked_ForLan_WithoutAuth_WithoutCreatingCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkReadProbe(factory);
        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.MiracleFdb,
            UserName: "ADMIN",
            Password: "x",
            SelectSql: SqlAccReadPocSafety.MinimalMetadataSql,
            AuthorizeLanRead: false));

        Assert.True(result.SafetyBlocked);
        Assert.False(result.Success);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Probe_ReturnsSafetyBlocked_ForForbiddenUamat()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkReadProbe(factory);
        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: SqlAccProjectTargets.SharedDcf,
            DatabaseFileName: SqlAccProjectTargets.ForbiddenUamatFdb,
            UserName: "ADMIN",
            Password: "x",
            SelectSql: SqlAccReadPocSafety.MinimalMetadataSql,
            AuthorizeLanRead: true,
            AuthorizedExactTarget: SqlAccProjectTargets.SharedDcf + "|" + SqlAccProjectTargets.ForbiddenUamatFdb));

        Assert.True(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("not an authorized project database", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_RejectsMutatingSql_WithoutCom()
    {
        var factory = new CountingFactory();
        var probe = new SqlAccSdkReadProbe(factory);
        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
            DatabaseFileName: "ACC-DEMO.FDB",
            UserName: "ADMIN",
            Password: "x",
            SelectSql: "DELETE FROM AR_CUSTOMER"));

        Assert.False(result.Success);
        Assert.False(result.SafetyBlocked);
        Assert.Equal(0, factory.CreateCount);
        Assert.Contains("SELECT/WITH", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_LoginOk_False_WhenIsLoginRemainsFalse()
    {
        // Official SDK: Login() may complete without throw while IsLogin stays false.
        var fake = new FakeComApp { SetIsLoginOnLogin = false };
        var probe = new SqlAccSdkReadProbe(new FixedFactory(fake), new StaWorkQueue());
        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
            DatabaseFileName: "ACC-DEMO.FDB",
            UserName: "ADMIN",
            Password: "secret",
            SelectSql: SqlAccReadPocSafety.MinimalMetadataSql));

        Assert.False(result.Success);
        Assert.False(result.LoginOk);
        Assert.False(result.IsLoginOk);
        Assert.False(result.ReadOk);
        Assert.Contains("IsLogin=false", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData((short)-1, true)]
    [InlineData((short)0, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void CoerceComBool_MatchesOfficialTruthySemantics(object raw, bool expected)
    {
        Assert.Equal(expected, SqlAccComApp.CoerceComBool(raw, out _));
    }

    [Fact]
    public async Task Probe_LoginSelectLogout_WithFakeCom()
    {
        var fake = new FakeComApp();
        var factory = new FixedFactory(fake);
        using var sta = new StaWorkQueue();
        var probe = new SqlAccSdkReadProbe(factory, sta);

        var result = await probe.RunAsync(new SqlAccReadProbeRequest(
            DcfPath: @"C:\eStream\SQLAccounting\Share\Default.DCF",
            DatabaseFileName: "ACC-DEMO.FDB",
            UserName: "ADMIN",
            Password: "secret",
            SelectSql: SqlAccReadPocSafety.MinimalMetadataSql));

        Assert.True(result.Success);
        Assert.True(result.LoginOk);
        Assert.True(result.IsLoginOk);
        Assert.True(result.ReadOk);
        Assert.True(result.LogoutOk);
        Assert.True(result.ComCleanupOk);
        Assert.Equal(1, result.RecordCount);
        Assert.True(fake.LoginCalled);
        Assert.True(fake.QueryCalled);
        Assert.True(fake.LogoutCalled);
        Assert.True(fake.Disposed);
        Assert.Equal(@"C:\eStream\SQLAccounting\Share\Default.DCF", fake.LastDcf);
        Assert.Equal("ACC-DEMO.FDB", fake.LastFdb);
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
        public bool IsLogin { get; private set; }
        public bool SetIsLoginOnLogin { get; init; } = true;
        public bool LoginCalled { get; private set; }
        public bool LogoutCalled { get; private set; }
        public bool QueryCalled { get; private set; }
        public bool Disposed { get; private set; }
        public string? LastDcf { get; private set; }
        public string? LastFdb { get; private set; }

        public void Login(string userName, string password, string dcfPath, string databaseFileName)
        {
            LoginCalled = true;
            LastDcf = dcfPath;
            LastFdb = databaseFileName;
            IsLogin = SetIsLoginOnLogin;
        }

        public void Logout()
        {
            LogoutCalled = true;
            IsLogin = false;
        }

        public int QueryRecordCount(string selectSql)
        {
            QueryCalled = true;
            return 1;
        }

        public bool AgentExists(string code) => false;
        public void AgentCreate(string code, string description) => throw new NotSupportedException();
        public void AgentUpdate(string code, string description) => throw new NotSupportedException();
        public string AgentReadDescription(string code) => throw new NotSupportedException();
        public void AgentDelete(string code) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }
}

