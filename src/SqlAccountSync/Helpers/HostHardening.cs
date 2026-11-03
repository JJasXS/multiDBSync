namespace SqlAccountSync.Helpers;

public static class HostHardening
{
    /// <summary>
    /// Production must not run with AllowedHosts=* or empty (host-header attacks).
    /// Set env <c>AllowedHosts</c> to the public DNS name(s), semicolon-separated.
    /// </summary>
    public static void EnsureProductionAllowedHosts(IHostEnvironment env, IConfiguration config, ILogger logger)
    {
        if (env.IsDevelopment())
            return;

        var hosts = (config["AllowedHosts"] ?? "").Trim();
        if (hosts.Length == 0 || hosts == "*")
        {
            throw new InvalidOperationException(
                "AllowedHosts must be set explicitly in Production (for example env AllowedHosts=sync.example.com). " +
                "Refusing to start with empty or '*'.");
        }

        if (IsLocalOnlyHosts(hosts))
        {
            logger.LogWarning(
                "AllowedHosts is local-only ({Hosts}). Set AllowedHosts to your public hostname before exposing this service.",
                hosts);
        }
    }

    public static bool IsLocalOnlyHosts(string hosts) =>
        hosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(h =>
                h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || h.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                || h.Equals("::1", StringComparison.OrdinalIgnoreCase));

    public static bool IsLoopbackIp(string? clientIp)
    {
        var t = (clientIp ?? "").Trim();
        if (t.Length == 0)
            return false;
        if (!System.Net.IPAddress.TryParse(t, out var ip))
            return false;
        return System.Net.IPAddress.IsLoopback(ip);
    }
}
