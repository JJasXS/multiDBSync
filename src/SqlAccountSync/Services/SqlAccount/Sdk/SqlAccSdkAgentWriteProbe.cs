namespace SqlAccountSync.Services.SqlAccount.Sdk;

public sealed record SqlAccAgentWriteRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    string BackupPath,
    bool AuthorizeLanRead,
    string AuthorizedExactTarget,
    string? AgentCode = null,
    string? CreateDescription = null,
    string? UpdateDescription = null,
    string? SupervisorManagedBackupConfirm = null);

public sealed record SqlAccAgentWriteResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    string? AgentCode = null,
    bool LoginOk = false,
    bool CreateOk = false,
    bool ReadAfterCreateOk = false,
    bool UpdateOk = false,
    bool ReadAfterUpdateOk = false,
    bool DeleteOk = false,
    bool FinalGoneOk = false,
    bool LogoutOk = false,
    bool ComCleanupOk = false,
    string? CreateDescriptionRead = null,
    string? UpdateDescriptionRead = null,
    bool OrphanPossible = false);

/// <summary>
/// Isolated Agent CRUD POC using only documented BizObject methods (CHM + wiki Agent samples).
/// No Firebird DML. Stops before Login when backup evidence is missing.
/// </summary>
public sealed class SqlAccSdkAgentWriteProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkAgentWriteProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public Task<SqlAccAgentWriteResult> RunAsync(
        SqlAccAgentWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SqlAccWritePocSafety.EnsureRestorableBackup(
                request.DatabaseFileName,
                request.BackupPath,
                request.SupervisorManagedBackupConfirm);
            SqlAccReadPocSafety.EnsureLiveReadAuthorized(
                request.DcfPath,
                request.DatabaseFileName,
                request.AuthorizeLanRead,
                request.AuthorizedExactTarget);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new SqlAccAgentWriteResult(
                Success: false,
                SafetyBlocked: true,
                Message: ex.Message));
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            return Task.FromResult(new SqlAccAgentWriteResult(false, false, "UserName and Password are required."));

        return _sta.RunAsync(() => ExecuteOnSta(request), cancellationToken);
    }

    private SqlAccAgentWriteResult ExecuteOnSta(SqlAccAgentWriteRequest request)
    {
        var code = string.IsNullOrWhiteSpace(request.AgentCode)
            ? SqlAccWritePocSafety.NewUniqueAgentCode()
            : request.AgentCode.Trim();
        if (!SqlAccWritePocSafety.LooksLikePocAgentCode(code))
        {
            return new SqlAccAgentWriteResult(
                false, true,
                "Refusing Agent code that does not start with SDKPOC (protects existing Agents).",
                AgentCode: code);
        }

        var createDesc = string.IsNullOrWhiteSpace(request.CreateDescription)
            ? $"SDK POC create {code}"
            : request.CreateDescription.Trim();
        var updateDesc = string.IsNullOrWhiteSpace(request.UpdateDescription)
            ? $"SDK POC update {code}"
            : request.UpdateDescription.Trim();

        ISqlAccComApp? app = null;
        var loginOk = false;
        var createOk = false;
        var readCreateOk = false;
        var updateOk = false;
        var readUpdateOk = false;
        var deleteOk = false;
        var finalGoneOk = false;
        var logoutOk = false;
        var cleanupOk = false;
        var orphan = false;
        string? readCreate = null;
        string? readUpdate = null;
        var message = "";
        var failed = false;

        try
        {
            app = _factory.Create();
            app.Login(request.UserName.Trim(), request.Password, request.DcfPath.Trim(), request.DatabaseFileName.Trim());
            loginOk = app.IsLogin;
            if (!loginOk)
            {
                failed = true;
                message = "Login failed (IsLogin=false).";
            }
            else if (app.AgentExists(code))
            {
                failed = true;
                message = $"Agent code {code} already exists. Refusing overwrite.";
            }
            else
            {
                app.AgentCreate(code, createDesc);
                createOk = true;

                readCreate = app.AgentReadDescription(code);
                readCreateOk = string.Equals(readCreate?.Trim(), createDesc, StringComparison.Ordinal);
                if (!readCreateOk)
                {
                    failed = true;
                    orphan = true;
                    message = $"Create readback mismatch. Expected '{createDesc}', got '{readCreate}'. Delete skipped for inspection. Code={code}";
                }
                else
                {
                    app.AgentUpdate(code, updateDesc);
                    updateOk = true;
                    readUpdate = app.AgentReadDescription(code);
                    readUpdateOk = string.Equals(readUpdate?.Trim(), updateDesc, StringComparison.Ordinal);
                    if (!readUpdateOk)
                    {
                        failed = true;
                        orphan = true;
                        message = $"Update readback mismatch. Expected '{updateDesc}', got '{readUpdate}'. Delete skipped. Code={code}";
                    }
                    else
                    {
                        try
                        {
                            app.AgentDelete(code);
                            deleteOk = true;
                            finalGoneOk = !app.AgentExists(code);
                            if (!finalGoneOk)
                            {
                                failed = true;
                                orphan = true;
                                message = $"Delete reported OK but Agent {code} still exists. Manual cleanup required.";
                            }
                            else
                            {
                                message = $"Agent CRUD POC completed for {code}.";
                            }
                        }
                        catch (Exception delEx)
                        {
                            failed = true;
                            orphan = true;
                            message = $"Delete failed for {code}: {delEx.GetType().Name}: {delEx.Message}. Manual cleanup required.";
                        }
                    }
                }
            }

            if (app.IsLogin)
            {
                app.Logout();
                logoutOk = !app.IsLogin;
            }
        }
        catch (Exception ex)
        {
            failed = true;
            orphan = createOk && !deleteOk;
            message = "SDK Agent Write POC failed: " + ex.GetType().Name + ": " + ex.Message
                      + (orphan ? $" Possible leftover Agent code={code}" : "");
            try
            {
                if (app?.IsLogin == true)
                {
                    app.Logout();
                    logoutOk = !app.IsLogin;
                }
            }
            catch { /* ignore */ }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    if (app.IsLogin)
                    {
                        app.Logout();
                        logoutOk = !app.IsLogin;
                    }
                }
                catch { /* ignore */ }

                try
                {
                    app.Dispose();
                    cleanupOk = true;
                }
                catch { cleanupOk = false; }
            }
        }

        return new SqlAccAgentWriteResult(
            Success: !failed && loginOk && createOk && readCreateOk && updateOk && readUpdateOk && deleteOk && finalGoneOk && logoutOk && cleanupOk,
            SafetyBlocked: false,
            Message: message,
            AgentCode: code,
            LoginOk: loginOk,
            CreateOk: createOk,
            ReadAfterCreateOk: readCreateOk,
            UpdateOk: updateOk,
            ReadAfterUpdateOk: readUpdateOk,
            DeleteOk: deleteOk,
            FinalGoneOk: finalGoneOk,
            LogoutOk: logoutOk,
            ComCleanupOk: cleanupOk,
            CreateDescriptionRead: readCreate,
            UpdateDescriptionRead: readUpdate,
            OrphanPossible: orphan);
    }
}
