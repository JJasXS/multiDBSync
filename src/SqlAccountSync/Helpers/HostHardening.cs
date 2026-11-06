using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.HostFiltering;

namespace SqlAccountSync.Helpers;

public static class HostHardening
{
    /// <summary>
    /// Production must not run with AllowedHosts=* or empty (host-header attacks).
    /// Set env <c>AllowedHosts</c> to the public DNS name(s), semicolon-separated.
    /// Private LAN IPs / machine name are appended at runtime (see <see cref="RegisterLanAllowedHosts"/>).
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

    /// <summary>
    /// Appends this machine's private LAN IPv4 addresses and Windows computer name to
    /// <see cref="HostFilteringOptions.AllowedHosts"/> so office LAN clients can open
    /// http://&lt;server-ip&gt;:port without editing AllowedHosts per site.
    /// Opt out with <c>HostHardening__IncludeLanAddresses=false</c>.
    /// </summary>
    public static void RegisterLanAllowedHosts(IServiceCollection services, IConfiguration config)
    {
        if (IsLanIncludeDisabled(config))
            return;

        services.PostConfigure<HostFilteringOptions>(options =>
        {
            var extras = GetLocalLanHosts();
            if (extras.Count == 0)
                return;

            // Config binding often uses a fixed-size array; replace with a mutable list.
            var merged = new List<string>(options.AllowedHosts ?? Array.Empty<string>());
            var existing = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
            foreach (var host in extras)
            {
                if (existing.Add(host))
                    merged.Add(host);
            }
            options.AllowedHosts = merged;
        });
    }

    public static bool IsLanIncludeDisabled(IConfiguration config) =>
        string.Equals(config["HostHardening:IncludeLanAddresses"], "false", StringComparison.OrdinalIgnoreCase);

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
        if (!IPAddress.TryParse(t, out var ip))
            return false;
        return IPAddress.IsLoopback(ip);
    }

    public static bool IsPrivateLanIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IPAddress.IsLoopback(ip))
            return false;

        var bytes = ip.GetAddressBytes();
        // 10.0.0.0/8
        if (bytes[0] == 10)
            return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;
        return false;
    }

    /// <summary>Private LAN IPv4s plus the machine hostname (when not localhost).</summary>
    public static IReadOnlyList<string> GetLocalLanHosts(
        Func<IEnumerable<IPAddress>>? addressSource = null,
        string? machineName = null)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var host = (value ?? "").Trim();
            if (host.Length == 0)
                return;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return;
            if (seen.Add(host))
                result.Add(host);
        }

        var name = (machineName ?? Environment.MachineName).Trim();
        Add(name);

        var addresses = addressSource?.Invoke() ?? EnumerateLocalIpv4();
        foreach (var ip in addresses)
        {
            if (IsPrivateLanIpv4(ip))
                Add(ip.ToString());
        }

        return result;
    }

    private static IEnumerable<IPAddress> EnumerateLocalIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return uni.Address;
            }
        }
    }
}
