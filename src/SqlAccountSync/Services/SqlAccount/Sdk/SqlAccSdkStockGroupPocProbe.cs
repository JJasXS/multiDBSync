using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Items;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Safety-gated Update/Delete for known leftover SDK POC stock groups (Website-path adapter).
/// </summary>
public sealed record SqlAccStockGroupPocRequest(
    string DcfPath,
    string DatabaseFileName,
    string UserName,
    string Password,
    string GroupCode,
    string OtherDatabaseFileName,
    bool AuthorizeLanRead,
    string AuthorizedExactTarget,
    string? SupervisorManagedBackupConfirm = null);

public sealed record SqlAccStockGroupPocStep(
    string Operation,
    bool Ok,
    string Detail,
    bool Blocked = false);

public sealed record SqlAccStockGroupPocResult(
    bool Success,
    bool SafetyBlocked,
    string Message,
    string DatabaseFileName,
    string GroupCode,
    IReadOnlyList<SqlAccStockGroupPocStep> Steps);

public sealed class SqlAccSdkStockGroupPocProbe
{
    private readonly ISqlAccComAppFactory _factory;
    private readonly StaWorkQueue _sta;

    public SqlAccSdkStockGroupPocProbe(ISqlAccComAppFactory factory, StaWorkQueue? sta = null)
    {
        _factory = factory;
        _sta = sta ?? new StaWorkQueue();
    }

    public async Task<SqlAccStockGroupPocResult> RunAsync(SqlAccStockGroupPocRequest request)
    {
        try
        {
            SqlAccWritePocSafety.EnsureRestorableBackup(
                request.DatabaseFileName, backupPath: null, request.SupervisorManagedBackupConfirm);
        }
        catch (Exception ex)
        {
            return new SqlAccStockGroupPocResult(
                false, true, ex.Message, request.DatabaseFileName, request.GroupCode,
                [new SqlAccStockGroupPocStep("safety", false, ex.Message, Blocked: true)]);
        }

        if (!SqlAccProjectTargets.IsAuthorizedProjectDatabaseFileName(request.DatabaseFileName)
            || SqlAccProjectTargets.IsForbiddenDatabaseFileName(request.DatabaseFileName))
        {
            return new SqlAccStockGroupPocResult(
                false, true, "Unauthorized database.", request.DatabaseFileName, request.GroupCode,
                [new SqlAccStockGroupPocStep("safety", false, "Unauthorized database.", Blocked: true)]);
        }

        // Orchestration off STA; PocSession marshals COM onto _sta (avoid nested STA deadlock).
        return await Task.Run(() => Execute(request)).ConfigureAwait(false);
    }

    private SqlAccStockGroupPocResult Execute(SqlAccStockGroupPocRequest request)
    {
        var steps = new List<SqlAccStockGroupPocStep>();
        var code = (request.GroupCode ?? "").Trim();
        var db = new RegisteredDatabase
        {
            DbCode = Path.GetFileNameWithoutExtension(request.DatabaseFileName),
            FbPath = request.DatabaseFileName,
            DisplayName = request.DatabaseFileName
        };
        var otherDb = new RegisteredDatabase
        {
            DbCode = Path.GetFileNameWithoutExtension(request.OtherDatabaseFileName),
            FbPath = request.OtherDatabaseFileName,
            DisplayName = request.OtherDatabaseFileName
        };

        var sessions = new PocSession(request, _factory, _sta);
        var write = new SqlAccountSdkWriteAdapter(sessions, NullLogger<SqlAccountSdkWriteAdapter>.Instance);
        var failed = false;
        var message = "";

        try
        {
            var warm = sessions.RunAsync(db, app =>
            {
                if (!app.IsLogin) throw new InvalidOperationException("IsLogin=false.");
                return app.QueryRecordCount("SELECT COUNT(*) AS CNT FROM RDB$DATABASE");
            }).GetAwaiter().GetResult();
            steps.Add(Ok("login", $"SDK Login/SELECT OK CNT={warm}; FDB={request.DatabaseFileName}"));

            var row = SelectGroup(sessions, db, code);
            if (row is null)
            {
                failed = true;
                message = "POC stock group missing: " + code;
                steps.Add(Fail("exists", message, blocked: true));
                return Finish();
            }

            steps.Add(Ok("exists",
                $"Found CODE={row.Code}; DESC={row.Description}; ISACTIVE={row.IsActive}"));

            if (!LooksLikeOurPocGroup(row))
            {
                failed = true;
                message = "Group does not look like an SDK POC leftover; refusing write.";
                steps.Add(Fail("poc-identity", message + $" DESC='{row.Description}'", blocked: true));
                return Finish();
            }

            steps.Add(Ok("poc-identity", "Description matches SDK POC group pattern."));

            var itemRefs = CountItemRefs(sessions, db, code);
            if (itemRefs > 0)
            {
                failed = true;
                message = $"Stock group is referenced by {itemRefs} ST_ITEM row(s); refusing update/delete.";
                steps.Add(Fail("no-business-refs", message, blocked: true));
                return Finish();
            }

            steps.Add(Ok("no-business-refs", "No ST_ITEM rows reference this stock group."));

            var otherBefore = SelectGroup(sessions, otherDb, code);
            steps.Add(Ok("other-before",
                otherBefore is null
                    ? $"Other DB {request.OtherDatabaseFileName}: code absent."
                    : $"Other DB {request.OtherDatabaseFileName}: CODE present DESC={otherBefore.Description}"));

            var newDesc = "SDK POC group UPD " + code;
            if (newDesc.Length > 80) newDesc = newDesc[..80];
            var update = write.UpdateStockGroupAsync(db, new StockGroupRecord
            {
                Code = code,
                Description = newDesc,
                IsActive = true,
                CostingMethod = 0
            }).GetAwaiter().GetResult();
            steps.Add(update.Success
                ? Ok("sdk-update", update.Message)
                : Fail("sdk-update", update.Message));
            if (!update.Success)
            {
                failed = true;
                message = update.Message;
                return Finish();
            }

            var afterUpdate = SelectGroup(sessions, db, code);
            var updateVerified = afterUpdate is not null
                && string.Equals(afterUpdate.Description.Trim(), newDesc, StringComparison.Ordinal);
            steps.Add(updateVerified
                ? Ok("fb-verify-update",
                    $"SELECT DESCRIPTION='{afterUpdate!.Description}' matches update.")
                : Fail("fb-verify-update",
                    afterUpdate is null
                        ? "Missing after update."
                        : $"DESC mismatch: got '{afterUpdate.Description}', expected '{newDesc}'."));
            if (!updateVerified)
            {
                failed = true;
                message = "Firebird verify update failed.";
                return Finish();
            }

            var otherAfterUpdate = SelectGroup(sessions, otherDb, code);
            var otherUnchanged = SameOptional(otherBefore, otherAfterUpdate);
            steps.Add(otherUnchanged
                ? Ok("isolation-after-update", "Other company stock group unchanged.")
                : Fail("isolation-after-update", "Other company record changed unexpectedly."));
            if (!otherUnchanged)
            {
                failed = true;
                message = "Cross-company isolation failed after update.";
                return Finish();
            }

            var del = write.DeleteStockGroupAsync(db, code).GetAwaiter().GetResult();
            steps.Add(del.Success
                ? Ok("sdk-delete", del.Message)
                : Fail("sdk-delete", del.Message));
            if (!del.Success)
            {
                failed = true;
                message = del.Message;
                return Finish();
            }

            var afterDelete = SelectGroup(sessions, db, code);
            var deleteVerified = afterDelete is null;
            steps.Add(deleteVerified
                ? Ok("fb-verify-delete", "SELECT confirms stock group absent.")
                : Fail("fb-verify-delete", "Still present after SDK delete."));
            if (!deleteVerified)
            {
                failed = true;
                message = "Firebird verify delete failed.";
                return Finish();
            }

            var otherAfterDelete = SelectGroup(sessions, otherDb, code);
            var otherStillOk = SameOptional(otherBefore, otherAfterDelete);
            steps.Add(otherStillOk
                ? Ok("isolation-after-delete", "Other company unaffected by delete.")
                : Fail("isolation-after-delete", "Other company changed after delete."));
            if (!otherStillOk)
            {
                failed = true;
                message = "Cross-company isolation failed after delete.";
                return Finish();
            }

            message = "Stock group POC Update/Delete verified for " + code + " on " + request.DatabaseFileName;
            return Finish();
        }
        catch (Exception ex)
        {
            failed = true;
            message = ex.GetType().Name + ": " + ex.Message;
            steps.Add(Fail("exception", message));
            return Finish();
        }
        finally
        {
            sessions.Dispose();
        }

        SqlAccStockGroupPocResult Finish() =>
            new(!failed, false, string.IsNullOrWhiteSpace(message) ? "done" : message,
                request.DatabaseFileName, code, steps);
    }

    private static bool LooksLikeOurPocGroup(GroupRow row)
    {
        var desc = row.Description.Trim();
        if (desc.StartsWith("SDK POC group", StringComparison.OrdinalIgnoreCase))
            return true;
        if (row.Code.StartsWith(SqlAccWritePocSafety.PocCodePrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        return row.Code.StartsWith("G26", StringComparison.OrdinalIgnoreCase)
               && row.Code.Length >= 12
               && row.Code.Skip(1).All(char.IsDigit);
    }

    private static bool SameOptional(GroupRow? a, GroupRow? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Description.Trim(), b.Description.Trim(), StringComparison.Ordinal)
               && a.IsActive == b.IsActive;
    }

    private static GroupRow? SelectGroup(PocSession sessions, RegisteredDatabase db, string code)
    {
        var safe = code.Replace("'", "''", StringComparison.Ordinal);
        var sql =
            "SELECT FIRST 1 TRIM(CODE) AS CODE, TRIM(COALESCE(DESCRIPTION,'')) AS DESCRIPTION, " +
            "ISACTIVE FROM ST_GROUP WHERE TRIM(CODE) = '" + safe + "'";
        return sessions.RunAsync(db, app =>
        {
            var native = SqlAccBizObjectWriter.NativeApp(app);
            var row = QueryFirst(native, sql);
            if (row is null) return null;
            string activeRaw = row.TryGetValue("ISACTIVE", out string? ia) ? (ia ?? "1") : "1";
            var active = !(activeRaw.Equals("0", StringComparison.OrdinalIgnoreCase)
                           || activeRaw.Equals("F", StringComparison.OrdinalIgnoreCase)
                           || activeRaw.Equals("false", StringComparison.OrdinalIgnoreCase));
            var foundCode = row.TryGetValue("CODE", out string? c) ? (c ?? code).Trim() : code;
            var desc = row.TryGetValue("DESCRIPTION", out string? d) ? (d ?? "") : "";
            return new GroupRow(foundCode, desc, active);
        }).GetAwaiter().GetResult();
    }

    private static int CountItemRefs(PocSession sessions, RegisteredDatabase db, string code)
    {
        var safe = code.Replace("'", "''", StringComparison.Ordinal);
        var sql = "SELECT COUNT(*) AS CNT FROM ST_ITEM WHERE TRIM(STOCKGROUP) = '" + safe + "'";
        return sessions.RunAsync(db, app =>
        {
            var native = SqlAccBizObjectWriter.NativeApp(app);
            var row = QueryFirst(native, sql);
            if (row is null) return 0;
            if (!row.TryGetValue("CNT", out string? cnt) || string.IsNullOrWhiteSpace(cnt))
                return 0;
            return int.TryParse(cnt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }).GetAwaiter().GetResult();
    }

    private static Dictionary<string, string>? QueryFirst(object nativeApp, string sql)
    {
        SqlGuard.EnsureSelectOnly(sql);
        dynamic native = nativeApp;
        dynamic? ds = null;
        try
        {
            ds = native.DBManager.NewDataSet(sql);
            if (Convert.ToInt32(ds.RecordCount) <= 0)
                return null;
            ds.First();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dynamic fields = ds.Fields;
            var fieldCount = Convert.ToInt32(fields.Count);
            for (var i = 0; i < fieldCount; i++)
            {
                dynamic f = fields.Items(i);
                var name = Convert.ToString(f.FieldName) ?? ("F" + i);
                string val;
                try { val = Convert.ToString(f.AsString) ?? Convert.ToString(f.value) ?? ""; }
                catch { val = Convert.ToString(f.value) ?? ""; }
                map[name] = val ?? "";
            }

            return map;
        }
        finally
        {
            try { if (ds is not null) ds.Close(); } catch { /* ignore */ }
        }
    }

    private static SqlAccStockGroupPocStep Ok(string op, string detail) => new(op, true, detail);
    private static SqlAccStockGroupPocStep Fail(string op, string detail, bool blocked = false) =>
        new(op, false, detail, blocked);

    private sealed record GroupRow(string Code, string Description, bool IsActive);

    private sealed class PocSession : ISqlAccSessionManager, IDisposable
    {
        private readonly SqlAccStockGroupPocRequest _request;
        private readonly ISqlAccComAppFactory _factory;
        private readonly StaWorkQueue _sta;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ISqlAccComApp? _app;
        private string? _activeKey;

        public PocSession(SqlAccStockGroupPocRequest request, ISqlAccComAppFactory factory, StaWorkQueue sta)
        {
            _request = request;
            _factory = factory;
            _sta = sta;
        }

        public Task<IDisposable> UseCompanyAsync(RegisteredDatabase db, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Use RunAsync.");

        public async Task<T> RunAsync<T>(RegisteredDatabase db, Func<ISqlAccComApp, T> action, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _sta.RunAsync(() =>
                {
                    EnsureLogin(db);
                    return action(_app!);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void EnsureLogin(RegisteredDatabase db)
        {
            var fdb = Path.GetFileName((db.FbPath ?? "").Trim());
            if (fdb.Length == 0)
                fdb = (db.FbPath ?? db.DbCode ?? "").Trim();
            var key = (_request.DcfPath ?? "").Trim() + "|" + fdb;
            if (_app is not null
                && string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase)
                && _app.IsLogin)
                return;

            try { _app?.Dispose(); } catch { /* ignore */ }
            _app = null;
            _activeKey = null;

            _app = _factory.Create();
            _app.Login(_request.UserName, _request.Password, _request.DcfPath ?? "", fdb);
            if (!_app.IsLogin)
                throw new InvalidOperationException("Login failed for " + fdb);
            _activeKey = key;
        }

        public void Dispose()
        {
            try { _app?.Dispose(); } catch { /* ignore */ }
            _app = null;
            _gate.Dispose();
        }
    }
}
