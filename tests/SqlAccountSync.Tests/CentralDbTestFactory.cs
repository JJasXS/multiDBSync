using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlAccountSync.Data;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Queue;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Tests;

internal static class CentralDbTestFactory
{
    public static (JobQueue Queue, CentralDb Db)? TryCreate()
    {
        var path = (Environment.GetEnvironmentVariable("CENTRAL_FB_PATH")
                    ?? Environment.GetEnvironmentVariable("CentralDb__Path")
                    ?? @"C:\SQLData\SqlAccountSync\SYNC.FDB").Trim();
        if (!File.Exists(path))
            return null;

        var host = (Environment.GetEnvironmentVariable("CENTRAL_FB_HOST") ?? "localhost").Trim();
        var options = Microsoft.Extensions.Options.Options.Create(new CentralDbOptions
        {
            Host = host,
            Port = 3050,
            Path = path,
            User = "SYSDBA",
            PasswordSecretRef = "env:CENTRAL_FB_MASTERKEY"
        });
        var sync = Microsoft.Extensions.Options.Options.Create(new SyncOptions
        {
            MaxAttempts = 5,
            ProcessingLeaseSeconds = 900
        });
        var secrets = new EnvSecretResolver();
        var db = new CentralDb(options, secrets, NullLogger<CentralDb>.Instance);
        if (!db.IsConfigured)
            return null;

        try
        {
            // Probe connectivity; skip quietly if Firebird/masterkey unavailable.
            db.ScalarAsync("SELECT 1 FROM RDB$DATABASE").GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }

        return (new JobQueue(db, sync), db);
    }

    private sealed class EnvSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default)
        {
            var r = (secretRef ?? "").Trim();
            if (r.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Environment.GetEnvironmentVariable(r[4..]));
            return Task.FromResult(Environment.GetEnvironmentVariable("CENTRAL_FB_MASTERKEY"));
        }
    }
}
