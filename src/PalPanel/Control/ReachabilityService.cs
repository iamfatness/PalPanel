using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PalPanel.Control;

// Answers "can players actually reach this server?" with the signals we can check from the host:
//   • the box's current public (WAN) IP, via an external lookup;
//   • whether the game port is bound/listening locally (server is actually up on that port);
//   • whether the configured public hostname's DNS resolves to that public IP (players who use
//     the domain will land on this box).
// True external UDP reachability (is the port-forward actually open to the internet) can't be
// probed reliably without a cooperating remote prober, so the UI is explicit that an open local
// port + matching DNS is a strong-but-not-absolute signal, and a closed router forward is the
// remaining unknown.
public partial class ReachabilityService(IHttpClientFactory httpFactory)
{
    public record Result(
        string? PublicIp,
        int GamePort,
        bool GamePortListening,
        string? DnsHostname,
        IReadOnlyList<string> DnsAddresses,
        bool? DnsMatchesPublicIp,
        string? Error);

    // Palworld's game/query port comes from `-port=NNNN` (default 8211). Not `-players`/`-publicport`.
    public static int ParseGamePort(string? launchArgs, int fallback = 8211)
    {
        if (string.IsNullOrWhiteSpace(launchArgs)) return fallback;
        var m = PortRegex().Match(launchArgs);
        return m.Success && int.TryParse(m.Groups["p"].Value, out var p) && p is > 0 and <= 65535 ? p : fallback;
    }

    // The dedicated server binds the game port as UDP; some builds also open a TCP query socket.
    // A listener on either is proof the server is actually serving that port on this host.
    public static bool GamePortListening(int port)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return props.GetActiveUdpListeners().Any(e => e.Port == port)
                || props.GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch { return false; }
    }

    public async Task<Result> CheckAsync(int gamePort, string? hostname, CancellationToken ct)
    {
        string? publicIp = null, error = null;
        try { publicIp = await GetPublicIpAsync(ct); }
        catch (Exception ex) { error = $"public IP lookup failed: {ex.Message}"; }

        var listening = GamePortListening(gamePort);

        string[] dnsAddrs = Array.Empty<string>();
        bool? match = null;
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(hostname.Trim(), AddressFamily.InterNetwork, ct);
                dnsAddrs = addrs.Select(a => a.ToString()).ToArray();
                if (publicIp is not null && dnsAddrs.Length > 0)
                    match = dnsAddrs.Contains(publicIp);
            }
            catch (Exception ex) { error = (error is null ? "" : error + "; ") + $"DNS lookup failed: {ex.Message}"; }
        }

        return new Result(publicIp, gamePort, listening,
            string.IsNullOrWhiteSpace(hostname) ? null : hostname.Trim(), dnsAddrs, match, error);
    }

    private async Task<string?> GetPublicIpAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(6);
        // api.ipify.org returns just the caller's public IPv4 as plain text.
        var text = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
        return IPAddress.TryParse(text, out _) ? text : null;
    }

    [GeneratedRegex(@"-port=(?<p>\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex PortRegex();
}
