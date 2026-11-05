using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.SqlAccount.Sdk;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SqlAccountSync.Tests;

public class SdkSessionDiagnosticsTests
{
    [Fact]
    public void ProviderStatus_Reports_Sdk_And_WorkersOff()
    {
        var diag = BuildDiagnostics(
            writeProvider: SqlAccountingWriteProviders.Sdk,
            allowLiveCom: true,
            passwordEnvSet: false,
            workersEnabled: false);

        var status = diag.GetProviderStatus();
        Assert.True(status.SdkSelected);
        Assert.Equal(SqlAccountingWriteProviders.Sdk, status.WriteProvider);
        Assert.True(status.AllowLiveCom);
        Assert.False(status.BackgroundWriteWorkersEnabled);
        Assert.False(status.PasswordConfigured);
        Assert.Contains("SQLACC_SDK_PASSWORD_UNITTEST_ABSENT", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderStatus_Reports_Rest_When_Not_Sdk()
    {
        var diag = BuildDiagnostics(
            writeProvider: SqlAccountingWriteProviders.Rest,
            allowLiveCom: false,
            passwordEnvSet: false,
            workersEnabled: true);

        var status = diag.GetProviderStatus();
        Assert.False(status.SdkSelected);
        Assert.Equal(SqlAccountingWriteProviders.Rest, status.WriteProvider);
        Assert.Contains("Rest", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_Refuses_When_Provider_Not_Sdk()
    {
        var diag = BuildDiagnostics(
            writeProvider: SqlAccountingWriteProviders.Rest,
            allowLiveCom: true,
            passwordEnvSet: true,
            workersEnabled: false);

        var result = await diag.ProbeLoginReadLogoutAsync(new RegisteredDatabase
        {
            DbCode = "MIRACLE",
            FbPath = @"C:/eStream/SQLAccounting/DB/ACC-MIRACLE.FDB"
        });

        Assert.False(result.Success);
        Assert.False(result.Wrote);
        Assert.Contains("not Sdk", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlAccSessionDiagnostics BuildDiagnostics(
        string writeProvider, bool allowLiveCom, bool passwordEnvSet, bool workersEnabled)
    {
        if (passwordEnvSet)
            Environment.SetEnvironmentVariable("SQLACC_SDK_PASSWORD", "unit-test-placeholder");
        else
            Environment.SetEnvironmentVariable("SQLACC_SDK_PASSWORD", null);

        return new SqlAccSessionDiagnostics(
            new BlockingSessions(),
            MsOptions.Create(new SqlAccountingWriteOptions { Provider = writeProvider }),
            MsOptions.Create(new SqlAccSdkOptions
            {
                AllowLiveCom = allowLiveCom,
                DefaultDcfPath = @"\\192.168.100.201\Share\SQLACC.DCF",
                UserName = "ADMIN",
                // Unique env name so a developer User-level SQLACC_SDK_PASSWORD cannot flip PasswordConfigured.
                PasswordSecretRef = passwordEnvSet
                    ? "env:SQLACC_SDK_PASSWORD"
                    : "env:SQLACC_SDK_PASSWORD_UNITTEST_ABSENT"
            }),
            MsOptions.Create(new SyncOptions { EnableBackgroundWriteWorkers = workersEnabled }));
    }

    private sealed class BlockingSessions : ISqlAccSessionManager
    {
        public Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test must not open COM.");

        public Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test must not open COM.");

        public SqlAccSessionStatus GetStatus() => new(false, null, false);
    }
}
