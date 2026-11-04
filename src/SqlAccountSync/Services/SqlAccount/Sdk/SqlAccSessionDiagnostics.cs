using Microsoft.Extensions.Options;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccProviderStatus(
    string WriteProvider,
    bool SdkSelected,
    bool AllowLiveCom,
    string DefaultDcfPath,
    string UserName,
    string PasswordSecretRef,
    bool PasswordConfigured,
    bool BackgroundWriteWorkersEnabled,
    string Message);

public sealed record SqlAccSessionProbeResult(
    bool Success,
    string Message,
    string? DbCode = null,
    string? DcfPath = null,
    string? DatabaseFileName = null,
    bool LoginOk = false,
    bool ReadOk = false,
    int? RecordCount = null,
    bool Wrote = false);

public interface ISqlAccSessionDiagnostics
{
    SqlAccProviderStatus GetProviderStatus();
    Task<SqlAccSessionProbeResult> ProbeLoginReadLogoutAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local Website diagnostics: effective Provider + optional Login → SELECT-only → Logout.
/// Never performs BizObject CUD.
/// </summary>
public sealed class SqlAccSessionDiagnostics : ISqlAccSessionDiagnostics
{
    private static readonly string ProbeSql = "SELECT COUNT(*) AS CNT FROM RDB$DATABASE";

    private readonly ISqlAccSessionManager _sessions;
    private readonly SqlAccountingWriteOptions _writeOptions;
    private readonly SqlAccSdkOptions _sdkOptions;
    private readonly SyncOptions _syncOptions;

    public SqlAccSessionDiagnostics(
        ISqlAccSessionManager sessions,
        IOptions<SqlAccountingWriteOptions> writeOptions,
        IOptions<SqlAccSdkOptions> sdkOptions,
        IOptions<SyncOptions> syncOptions)
    {
        _sessions = sessions;
        _writeOptions = writeOptions.Value;
        _sdkOptions = sdkOptions.Value;
        _syncOptions = syncOptions.Value;
    }

    public SqlAccProviderStatus GetProviderStatus()
    {
        var provider = (_writeOptions.Provider ?? "").Trim();
        if (provider.Length == 0)
            provider = SqlAccountingWriteProviders.Rest;
        var sdk = string.Equals(provider, SqlAccountingWriteProviders.Sdk, StringComparison.OrdinalIgnoreCase);
        var pwdRef = (_sdkOptions.PasswordSecretRef ?? "").Trim();
        var pwdConfigured = SqlAccSdkPasswordStatus.IsConfigured(pwdRef, out var pwdDetail);

        string message;
        if (!sdk)
            message = "Write provider is Rest. SDK adapter is registered but not selected.";
        else if (!_sdkOptions.AllowLiveCom)
            message = "Provider=Sdk but AllowLiveCom=false; COM Login is blocked.";
        else if (_writeOptions.BlockRestHttp)
            message =
                "Provider=Sdk + BlockRestHttp; company logins from sdkConnections (fallback DefaultDcfPath/UserName). " +
                pwdDetail;
        else
            message =
                "Provider=Sdk selected; company logins from sdkConnections (fallback DefaultDcfPath/UserName). " +
                pwdDetail;

        return new SqlAccProviderStatus(
            WriteProvider: provider,
            SdkSelected: sdk,
            AllowLiveCom: _sdkOptions.AllowLiveCom,
            DefaultDcfPath: _sdkOptions.DefaultDcfPath ?? "",
            UserName: _sdkOptions.UserName ?? "",
            PasswordSecretRef: pwdRef,
            PasswordConfigured: pwdConfigured,
            BackgroundWriteWorkersEnabled: _syncOptions.EnableBackgroundWriteWorkers,
            Message: message);
    }

    public async Task<SqlAccSessionProbeResult> ProbeLoginReadLogoutAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        var status = GetProviderStatus();
        if (!status.SdkSelected)
        {
            return new SqlAccSessionProbeResult(
                false,
                "Write provider is not Sdk; refusing COM probe.",
                db.DbCode);
        }

        if (!status.AllowLiveCom)
        {
            return new SqlAccSessionProbeResult(
                false,
                "SqlAccSdk:AllowLiveCom is false.",
                db.DbCode);
        }

        SqlGuard.EnsureSelectOnly(ProbeSql);

        try
        {
            var target = SqlAccSessionManager.ResolveCompanyTarget(db, _sdkOptions.DefaultDcfPath);
            string? comDiag = null;
            var count = await _sessions.RunAsync(db, app =>
            {
                if (!app.IsLogin)
                    throw new InvalidOperationException("IsLogin=false after session Login.");
                comDiag = app.LastComDiagnostics;
                return app.QueryRecordCount(ProbeSql);
            }, cancellationToken).ConfigureAwait(false);

            return new SqlAccSessionProbeResult(
                Success: true,
                Message: "Login + SELECT-only read via ISqlAccSessionManager OK (no CUD). comDiag={" + (comDiag ?? "") + "}",
                DbCode: db.DbCode,
                DcfPath: target.DcfPath,
                DatabaseFileName: target.DatabaseFileName,
                LoginOk: true,
                ReadOk: true,
                RecordCount: count,
                Wrote: false);
        }
        catch (Exception ex)
        {
            return new SqlAccSessionProbeResult(
                Success: false,
                Message: "SDK session probe failed: " + ex.GetType().Name + ": " + ex.Message,
                DbCode: db.DbCode,
                Wrote: false);
        }
    }
}
