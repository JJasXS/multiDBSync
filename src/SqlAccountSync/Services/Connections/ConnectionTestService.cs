using System.Net.Sockets;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Connections;

public interface IConnectionTestService
{
    Task<ConnectionTestResult> TestAsync(RegisteredDatabase db, CancellationToken cancellationToken = default);
}

public sealed class ConnectionTestService : IConnectionTestService
{
    private readonly CompanyConnectionFactory _factory;
    private readonly SyncOptions _options;
    private readonly ILogger<ConnectionTestService> _logger;

    public ConnectionTestService(
        CompanyConnectionFactory factory,
        IOptions<SyncOptions> options,
        ILogger<ConnectionTestService> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConnectionTestResult> TestAsync(RegisteredDatabase db, CancellationToken cancellationToken = default)
    {
        try
        {
            _factory.GuardEnvironment(db);
        }
        catch (InvalidOperationException ex)
        {
            return new ConnectionTestResult { Status = ConnectionResults.UnknownError, Message = ex.Message };
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(db.FbHost, db.FbPort <= 0 ? 3050 : db.FbPort, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult { Status = ConnectionResults.Timeout, Message = "Network timeout connecting to Firebird host/port." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TCP probe failed for {Db}: {Msg}", db.DbCode, CredentialRedactor.Redact(ex.Message));
            return new ConnectionTestResult { Status = ConnectionResults.NetworkError, Message = "Could not reach the Firebird host and port." };
        }

        FbConnection? conn = null;
        try
        {
            conn = await _factory.OpenAsync(db, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var mapped = FirebirdErrorMapper.Map(ex);
            return new ConnectionTestResult { Status = mapped, Message = UserSafeMessage(mapped, ex) };
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM RDB$DATABASE";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var missing = new List<string>();
            foreach (var table in _options.RequiredTables)
            {
                await using var tcmd = conn.CreateCommand();
                tcmd.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE TRIM(RDB$RELATION_NAME) = @N";
                CentralDb.Bind(tcmd, new Dictionary<string, object?> { ["N"] = table.Trim().ToUpperInvariant() });
                var n = Convert.ToInt32(await tcmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                if (n == 0)
                    missing.Add(table);
            }

            if (missing.Count > 0)
            {
                return new ConnectionTestResult
                {
                    Status = ConnectionResults.UnsupportedSchema,
                    Message = "Connected, but required SQL Account tables were not found.",
                    MissingTables = missing
                };
            }

            return new ConnectionTestResult { Status = ConnectionResults.Online, Message = "Connection successful. Required tables present." };
        }
        catch (Exception ex)
        {
            var mapped = FirebirdErrorMapper.Map(ex);
            return new ConnectionTestResult { Status = mapped, Message = UserSafeMessage(mapped, ex) };
        }
        finally
        {
            if (conn is not null)
                await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string UserSafeMessage(string status, Exception ex) => status switch
    {
        ConnectionResults.AuthenticationFailed => "Authentication failed. Check the credential reference.",
        ConnectionResults.DatabaseNotFound => "The Firebird database file was not found on the server.",
        ConnectionResults.Timeout => "The database did not respond in time.",
        ConnectionResults.NetworkError => "A network error occurred reaching Firebird.",
        _ => "The connection test failed. See the error log for details."
    };
}
