using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SqlAccSdkPasswordStatusTests
{
    [Fact]
    public void IsConfigured_False_When_Env_Missing()
    {
        var name = "SQLACC_SDK_PASSWORD_UNIT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);

        var ok = SqlAccSdkPasswordStatus.IsConfigured("env:" + name, out var detail);

        Assert.False(ok);
        Assert.Contains(name, detail, StringComparison.Ordinal);
        Assert.Contains("not set", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsConfigured_True_When_Process_Env_Set()
    {
        var name = "SQLACC_SDK_PASSWORD_UNIT_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(name, "unit-test-placeholder", EnvironmentVariableTarget.Process);

            var ok = SqlAccSdkPasswordStatus.IsConfigured("env:" + name, out var detail);

            Assert.True(ok);
            Assert.Contains("Process", detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unit-test-placeholder", detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void IsConfigured_True_For_NonEnv_Ref_Without_Reading_Secret()
    {
        var ok = SqlAccSdkPasswordStatus.IsConfigured("sm:sqlacc/sdk/password", out var detail);
        Assert.True(ok);
        Assert.Contains("ISecretResolver", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderStatus_Uses_Password_Helper()
    {
        Environment.SetEnvironmentVariable("SQLACC_SDK_PASSWORD", null, EnvironmentVariableTarget.Process);
        var diag = new SqlAccSessionDiagnostics(
            new BlockingSessions(),
            Microsoft.Extensions.Options.Options.Create(new Options.SqlAccountingWriteOptions
            {
                Provider = Options.SqlAccountingWriteProviders.Sdk
            }),
            Microsoft.Extensions.Options.Options.Create(new Options.SqlAccSdkOptions
            {
                AllowLiveCom = true,
                DefaultDcfPath = @"\\192.168.100.201\Share\SQLACC.DCF",
                UserName = "ADMIN",
                PasswordSecretRef = "env:SQLACC_SDK_PASSWORD"
            }),
            Microsoft.Extensions.Options.Options.Create(new Options.SyncOptions
            {
                EnableBackgroundWriteWorkers = false
            }));

        var status = diag.GetProviderStatus();
        Assert.False(status.PasswordConfigured);
        Assert.Contains("SQLACC_SDK_PASSWORD", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BlockingSessions : ISqlAccSessionManager
    {
        public Task<IDisposable> UseCompanyAsync(Models.RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test must not open COM.");

        public Task<T> RunAsync<T>(Models.RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test must not open COM.");
    }
}
