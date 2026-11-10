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
    bool IsOwn,
    bool LooksLikeSqlAcc);

public sealed record FirebirdKickResult(
    bool Success,
    string Message,
    long OwnAttachmentId,
    int Killed,
    int Failed,
    IReadOnlyList<FirebirdAttachmentRow> Remaining);

public interface IFirebirdSessionKickService
{
    Task<IReadOnlyList<FirebirdAttachmentRow>> ListAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);

    Task<FirebirdKickResult> KickOthersAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default);
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

    public async Task<FirebirdKickResult> KickOthersAsync(
        RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        await using var conn = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        var ownId = await ReadOwnAttachmentIdAsync(conn, cancellationToken).ConfigureAwait(false);
        var before = await ReadAttachmentsAsync(conn, ownId, cancellationToken).ConfigureAwait(false);
        var targets = FirebirdAttachmentKickRules.IdsToKick(
            before.Select(r => r.AttachmentId), ownId);

        if (targets.Count == 0)
        {
            return new FirebirdKickResult(
                true,
                "No other Firebird sessions on this database.",
                ownId,
                0,
                0,
                before);
        }

        var killed = 0;
        var failed = 0;
        foreach (var id in targets)
        {
            if (await TryDeleteAttachmentAsync(conn, id, cancellationToken).ConfigureAwait(false))
                killed++;
            else
                failed++;
        }

        var remaining = await ReadAttachmentsAsync(conn, ownId, cancellationToken).ConfigureAwait(false);
        var ok = failed == 0;
        var message = ok
            ? $"Kicked {killed} other session(s). Your connection was kept."
            : $"Kicked {killed} session(s); {failed} could not be closed.";

        _logger.LogInformation(
            "Firebird kick-others on {DbCode}: killed={Killed} failed={Failed} own={OwnId}",
            db.DbCode, killed, failed, ownId);

        return new FirebirdKickResult(ok, message, ownId, killed, failed, remaining);
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
        cmd.CommandText =
            """
            SELECT
              MON$ATTACHMENT_ID,
              MON$USER,
              MON$REMOTE_ADDRESS,
              MON$REMOTE_PROCESS,
              MON$TIMESTAMP
            FROM MON$ATTACHMENTS
            ORDER BY MON$ATTACHMENT_ID
            """;

        var rows = new List<FirebirdAttachmentRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var user = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
            var address = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
            var process = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
            DateTime? connected = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
            rows.Add(new FirebirdAttachmentRow(
                id,
                user,
                string.IsNullOrWhiteSpace(address) ? "—" : address,
                process,
                FirebirdAttachmentKickRules.ShortProcess(process),
                connected,
                id == ownId,
                FirebirdAttachmentKickRules.LooksLikeSqlAcc(process)));
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
