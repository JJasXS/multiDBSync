using FirebirdSql.Data.FirebirdClient;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Connections;

public sealed record FirebirdAttachmentRow(
    long AttachmentId,
    string User,
    string RemoteAddress,
    string RemoteProcess,
    string ProcessShort,
    DateTime? ConnectedAt,
    int? RemotePid,
    bool IsOwn,
    bool IsSystem,
    bool LooksLikeSqlAcc);

public sealed record FirebirdKickResult(
    bool Success,
    string Message,
    long OwnAttachmentId,
    int Killed,
    int Failed,
    IReadOnlyList<FirebirdAttachmentRow> Remaining);

public sealed record FirebirdDbSessionSummary(
    int DbId,
    string DisplayName,
    string DbCode,
    string FdbFile,
    int ClientCount,
    int OtherCount,
    int SqlAccCount,
    string? Error);

public sealed record FirebirdMultiKickResult(
    bool Success,
    string Message,
    int DatabasesOk,
    int DatabasesFailed,
    int Killed,
    int Failed,
    IReadOnlyList<FirebirdDbSessionSummary> Summaries);

public interface IFirebirdSessionKickService
{
    Task<IReadOnlyList<FirebirdAttachmentRow>> ListAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);

    Task<FirebirdKickResult> KickOthersAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kick client sessions. When <paramref name="includeOwn"/> is true, opens a second
    /// connection so the listing attachment can also be deleted (Firebird forbids
    /// deleting CURRENT_CONNECTION on the same connection).
    /// </summary>
    Task<FirebirdKickResult> KickAsync(
        RegisteredDatabase db, bool includeOwn, CancellationToken cancellationToken = default);

    Task<FirebirdDbSessionSummary> SummarizeAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);

    Task<FirebirdMultiKickResult> KickOthersOnManyAsync(
        IReadOnlyList<RegisteredDatabase> databases, CancellationToken cancellationToken = default);
}

public sealed class FirebirdSessionKickService : IFirebirdSessionKickService
{
    private readonly CompanyConnectionFactory _factory;
    private readonly ILogger<FirebirdSessionKickService> _logger;

    public FirebirdSessionKickService(
        CompanyConnectionFactory factory,
        ILogger<FirebirdSessionKickService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FirebirdAttachmentRow>> ListAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        await using var conn = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        var ownId = await ReadOwnAttachmentIdAsync(conn, cancellationToken).ConfigureAwait(false);
        return await ReadAttachmentsAsync(conn, ownId, cancellationToken).ConfigureAwait(false);
    }

    public Task<FirebirdKickResult> KickOthersAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
        => KickAsync(db, includeOwn: false, cancellationToken);

    public async Task<FirebirdKickResult> KickAsync(
        RegisteredDatabase db, bool includeOwn, CancellationToken cancellationToken = default)
    {
        await using var probe = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        var ownId = await ReadOwnAttachmentIdAsync(probe, cancellationToken).ConfigureAwait(false);
        var before = await ReadAttachmentsAsync(probe, ownId, cancellationToken).ConfigureAwait(false);
        var targets = FirebirdAttachmentKickRules.IdsToKick(
            before.Select(r => (r.AttachmentId, r.User)), ownId, includeOwn);

        if (targets.Count == 0)
        {
            return new FirebirdKickResult(
                true,
                includeOwn
                    ? "No client Firebird sessions on this database."
                    : "No other Firebird sessions on this database.",
                ownId,
                0,
                0,
                before);
        }

        var needsSecond = includeOwn && targets.Contains(ownId);
        if (!needsSecond)
            return await KickWithConnectionAsync(db, probe, ownId, targets, includeOwn, cancellationToken)
                .ConfigureAwait(false);

        await using var killer = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        return await KickWithConnectionAsync(db, killer, ownId: 0, targets, includeOwn, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<FirebirdDbSessionSummary> SummarizeAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await ListAsync(db, cancellationToken).ConfigureAwait(false);
            var clients = rows.Where(r => !r.IsSystem).ToList();
            return new FirebirdDbSessionSummary(
                db.DbId,
                db.DisplayName,
                db.DbCode,
                DatabaseListSearch.FdbFileName(db.FbPath),
                clients.Count,
                clients.Count(r => !r.IsOwn),
                clients.Count(r => r.LooksLikeSqlAcc && !r.IsOwn),
                null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not summarize Firebird sessions for {DbCode}", db.DbCode);
            return new FirebirdDbSessionSummary(
                db.DbId,
                db.DisplayName,
                db.DbCode,
                DatabaseListSearch.FdbFileName(db.FbPath),
                0,
                0,
                0,
                CredentialRedactor.Redact(ex.Message));
        }
    }

    public async Task<FirebirdMultiKickResult> KickOthersOnManyAsync(
        IReadOnlyList<RegisteredDatabase> databases, CancellationToken cancellationToken = default)
    {
        var summaries = new List<FirebirdDbSessionSummary>();
        var killed = 0;
        var failed = 0;
        var dbOk = 0;
        var dbFail = 0;

        foreach (var db in databases)
        {
            try
            {
                var result = await KickOthersAsync(db, cancellationToken).ConfigureAwait(false);
                killed += result.Killed;
                failed += result.Failed;
                if (result.Success)
                    dbOk++;
                else
                    dbFail++;

                var clients = result.Remaining.Where(r => !r.IsSystem).ToList();
                summaries.Add(new FirebirdDbSessionSummary(
                    db.DbId,
                    db.DisplayName,
                    db.DbCode,
                    DatabaseListSearch.FdbFileName(db.FbPath),
                    clients.Count,
                    clients.Count(r => !r.IsOwn),
                    clients.Count(r => r.LooksLikeSqlAcc && !r.IsOwn),
                    result.Success ? null : result.Message));
            }
            catch (Exception ex)
            {
                dbFail++;
                _logger.LogWarning(ex, "Kick others failed on linked DB {DbCode}", db.DbCode);
                summaries.Add(new FirebirdDbSessionSummary(
                    db.DbId,
                    db.DisplayName,
                    db.DbCode,
                    DatabaseListSearch.FdbFileName(db.FbPath),
                    0,
                    0,
                    0,
                    CredentialRedactor.Redact(ex.Message)));
            }
        }

        var ok = dbFail == 0;
        var message = databases.Count == 0
            ? "No linked SUB databases."
            : ok
                ? $"Kicked {killed} other session(s) across {dbOk} SUB database(s)."
                : $"Kicked {killed} session(s) across {dbOk} SUB(s); {dbFail} database(s) had errors ({failed} session failures).";

        return new FirebirdMultiKickResult(ok, message, dbOk, dbFail, killed, failed, summaries);
    }

    private async Task<FirebirdKickResult> KickWithConnectionAsync(
        RegisteredDatabase db,
        FbConnection killer,
        long ownId,
        IReadOnlyList<long> targets,
        bool includeOwn,
        CancellationToken cancellationToken)
    {
        var killed = 0;
        var failed = 0;
        foreach (var id in targets)
        {
            if (await TryDeleteAttachmentAsync(killer, id, cancellationToken).ConfigureAwait(false))
                killed++;
            else
                failed++;
        }

        var remainingOwn = ownId > 0
            ? ownId
            : await ReadOwnAttachmentIdAsync(killer, cancellationToken).ConfigureAwait(false);
        var remaining = await ReadAttachmentsAsync(killer, remainingOwn, cancellationToken)
            .ConfigureAwait(false);
        var ok = failed == 0;
        var message = ok
            ? includeOwn
                ? $"Kicked {killed} session(s), including this app’s connection if it was listed."
                : $"Kicked {killed} other session(s). Your connection was kept."
            : $"Kicked {killed} session(s); {failed} could not be closed.";

        _logger.LogInformation(
            "Firebird kick on {DbCode}: includeOwn={IncludeOwn} killed={Killed} failed={Failed}",
            db.DbCode, includeOwn, killed, failed);

        return new FirebirdKickResult(ok, message, remainingOwn, killed, failed, remaining);
    }

    private static async Task<long> ReadOwnAttachmentIdAsync(
        FbConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CURRENT_CONNECTION FROM RDB$DATABASE";
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value);
    }

    private static async Task<List<FirebirdAttachmentRow>> ReadAttachmentsAsync(
        FbConnection conn, long ownId, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        // CAST timestamp: FB 4 zoned MON$TIMESTAMP throws "Unknown time zone ID" in the .NET client.
        cmd.CommandText =
            """
            SELECT
              MON$ATTACHMENT_ID,
              MON$USER,
              MON$REMOTE_ADDRESS,
              MON$REMOTE_PROCESS,
              CAST(MON$TIMESTAMP AS TIMESTAMP) AS STARTED_AT,
              MON$REMOTE_PID
            FROM MON$ATTACHMENTS
            ORDER BY MON$ATTACHMENT_ID
            """;

        var rows = new List<FirebirdAttachmentRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Convert.ToInt64(reader.GetValue(0));
            var user = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1))?.Trim() ?? "";
            var address = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2))?.Trim() ?? "";
            var process = reader.IsDBNull(3) ? "" : Convert.ToString(reader.GetValue(3))?.Trim() ?? "";
            DateTime? connected = reader.IsDBNull(4) ? null : Convert.ToDateTime(reader.GetValue(4));
            int? pid = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5));
            var isSystem = FirebirdAttachmentKickRules.IsSystemAttachment(user);
            rows.Add(new FirebirdAttachmentRow(
                id,
                user,
                string.IsNullOrWhiteSpace(address) ? "—" : address,
                process,
                isSystem ? user : FirebirdAttachmentKickRules.ShortProcess(process),
                connected,
                pid,
                id == ownId,
                isSystem,
                !isSystem && FirebirdAttachmentKickRules.LooksLikeSqlAcc(process)));
        }

        return rows;
    }

    private async Task<bool> TryDeleteAttachmentAsync(
        FbConnection conn, long attachmentId, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MON$ATTACHMENTS WHERE MON$ATTACHMENT_ID = @ID";
            CentralDb.Bind(cmd, new Dictionary<string, object?> { ["ID"] = attachmentId });
            var n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return n > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not kill Firebird attachment {AttachmentId}",
                attachmentId);
            return false;
        }
    }
}
