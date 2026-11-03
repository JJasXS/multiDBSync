using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SqlAccountSync.Helpers;

/// <summary>
/// Configures trusted reverse proxies only — never clears KnownProxies/Networks to trust the world.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    public static void Configure(ForwardedHeadersOptions options, IConfiguration config)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Keep framework defaults (loopback). Add explicit proxies/networks from config only.
        foreach (var raw in config.GetSection($"{SectionName}:KnownProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse((raw ?? "").Trim(), out var ip))
                options.KnownProxies.Add(ip);
        }

        foreach (var raw in config.GetSection($"{SectionName}:KnownNetworks").Get<string[]>() ?? [])
        {
            if (TryParseCidr((raw ?? "").Trim(), out var network))
                options.KnownNetworks.Add(network);
        }
    }

    public static bool TryParseCidr(string cidr, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = default!;
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;
        if (!IPAddress.TryParse(parts[0], out var prefix))
            return false;
        if (!int.TryParse(parts[1], out var prefixLength) || prefixLength < 0)
            return false;
        try
        {
            network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
