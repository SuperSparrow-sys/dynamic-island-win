using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicBay.Services.Link;

/// <summary>
/// Finds other DynamicBays: UDP announcements in the LAN (every 5 s and on request), the online peers Tailscale lists
/// (asked directly and probed on the link port) and addresses the user typed in. Announcements carry only id, name,
/// port and protocol version: who someone really is only shows in the TLS connection.
/// </summary>
internal sealed class LinkDiscovery
{
    private readonly LinkOptions _opts;
    private readonly Func<string> _id, _name;
    private readonly Func<int> _port;
    private readonly Action _changed;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manual = new(StringComparer.OrdinalIgnoreCase);
    private List<IPAddress> _tailscalePeers = new();
    private UdpClient? _udp;
    private CancellationToken _ct;
    private bool _running;
    private string? _tailscaleState;
    private int _scanning;

    /// <summary>Tailscale went to "Running" (connected): the link reconnects.</summary>
    public event Action? TailscaleUp;

    private sealed class Entry
    {
        public string Id = "", Name = "";
        public List<string> Addresses = new();
        public LinkVia Via;
        public DateTime LastSeen;
    }

    public LinkDiscovery(LinkOptions opts, Func<string> id, Func<string> name, Func<int> port, Action changed)
    {
        _opts = opts; _id = id; _name = name; _port = port; _changed = changed;
    }

    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _ct = ct;
        }
        if (_opts.LanDiscovery)
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, _opts.DiscoveryPort));
                udp.EnableBroadcast = true;
                _udp = udp;
                _ = Task.Run(() => ReceiveLoop(udp, ct));
                _ = Task.Run(() => AnnounceLoop(ct));
            }
            catch (SocketException ex) { _opts.Log($"Link: LAN discovery off ({ex.SocketErrorCode})"); }
        }
        if (_opts.TailscaleDiscovery) _ = Task.Run(() => TailscaleLoop(ct));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _entries.Clear();
        }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    // ---------- results ----------

    public IReadOnlyList<DiscoveredDevice> Snapshot(Func<string, bool> isPaired)
    {
        lock (_gate)
        {
            Prune();
            return _entries.Values
                .Select(e => new DiscoveredDevice(e.Id, e.Name, e.Addresses.ToList(), e.Via, e.Id.Length > 0 && isPaired(e.Id), e.LastSeen))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<string> AddressesFor(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var e) ? e.Addresses.ToList() : Array.Empty<string>();
    }

    // LAN entries vanish when their device stopped announcing for a while.
    private void Prune()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(20);
        foreach (var k in _entries.Where(kv => kv.Value.Via == LinkVia.Lan && kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
            _entries.Remove(k);
    }

    /// <summary>A device said who it is (announce from <paramref name="from"/>): one entry per id, address-only entries merge in.</summary>
    internal void Upsert(string id, string name, IPAddress from, int port, LinkVia via)
    {
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        string address = LinkProtocol.FormatAddress(from, port);
        lock (_gate)
        {
            if (IsTailscale(from)) via |= LinkVia.Tailscale;
            foreach (var key in _entries.Where(kv => kv.Value.Id.Length == 0 && kv.Value.Addresses.Any(a => SameHost(a, from))).Select(kv => kv.Key).ToList())
            {
                via |= _entries[key].Via;
                _entries.Remove(key);
            }
            if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry { Id = id };
            e.Name = name;
            e.Via |= via;
            e.LastSeen = DateTime.UtcNow;
            e.Addresses.Remove(address);
            e.Addresses.Insert(0, address);
            if (e.Addresses.Count > 4) e.Addresses.RemoveRange(4, e.Addresses.Count - 4);
        }
        _changed();
    }

    private void UpsertAddressOnly(string address, string name, LinkVia via)
    {
        lock (_gate)
        {
            if (!LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out var host, out _)) return;
            // Already known with its id: just note how it was found.
            var known = _entries.Values.FirstOrDefault(e => e.Id.Length > 0 && e.Addresses.Any(a => LinkProtocol.TryParseAddress(a, LinkProtocol.Port, out var h, out _) && string.Equals(h, host, StringComparison.OrdinalIgnoreCase)));
            if (known is not null) { known.Via |= via; known.LastSeen = DateTime.UtcNow; }
            else
            {
                string key = "addr:" + address;
                if (!_entries.TryGetValue(key, out var e)) _entries[key] = e = new Entry { Name = name, Addresses = { address } };
                e.Via |= via;
                e.LastSeen = DateTime.UtcNow;
            }
        }
        _changed();
    }

    private static bool SameHost(string address, IPAddress ip) =>
        LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out var host, out _) && IPAddress.TryParse(host, out var h) && h.Equals(ip);

    /// <summary>100.64.0.0/10, the range Tailscale hands out.</summary>
    internal static bool IsTailscale(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.ToString().StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase);
        var b = ip.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    // ---------- LAN ----------

    private byte[] Message(string type) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["t"] = type, ["id"] = _id(), ["name"] = _name(), ["port"] = _port(), ["proto"] = LinkProtocol.Version,
    }.ToJsonString());

    /// <summary>Announce or query from the network, or null when it is not one of ours (or broken).</summary>
    internal static (string Type, string Id, string Name, int Port, int Proto)? ParseAnnounce(byte[] data)
    {
        if (data.Length is 0 or > 2048) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(data) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        string? t = LinkProtocol.Str(o, "t"), id = LinkProtocol.Str(o, "id");
        if (t is not ("announce" or "query") || id is not { Length: 16 } || !id.All(Uri.IsHexDigit)) return null;
        long port = LinkProtocol.Long(o, "port") ?? LinkProtocol.Port;
        if (port is <= 0 or >= 65536) return null;
        int proto = (int)Math.Clamp(LinkProtocol.Long(o, "proto") ?? 0, 0, 1000);
        return (t, id.ToUpperInvariant(), LinkService.CleanName(LinkProtocol.Str(o, "name")), (int)port, proto);
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; } // ICMP port unreachable from a unicast query
            if (ParseAnnounce(r.Buffer) is not { } m || m.Id == _id() || m.Proto < 1) continue;
            if (m.Type == "query") await SendTo(udp, Message("announce"), r.RemoteEndPoint).ConfigureAwait(false);
            Upsert(m.Id, m.Name, r.RemoteEndPoint.Address, m.Port, LinkVia.Lan);
        }
    }

    private async Task AnnounceLoop(CancellationToken ct)
    {
        Query();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_udp is { } udp) await Broadcast(udp, Message("announce")).ConfigureAwait(false);
            bool pruned;
            lock (_gate) { int n = _entries.Count; Prune(); pruned = n != _entries.Count; }
            if (pruned) _changed();
        }
    }

    /// <summary>Asks everyone (LAN broadcast, Tailscale peers, typed addresses) to announce themselves now.</summary>
    public void Query()
    {
        if (_udp is not { } udp) { _ = ProbeManual(); return; }
        _ = Task.Run(async () =>
        {
            var q = Message("query");
            await Broadcast(udp, q).ConfigureAwait(false);
            List<IPAddress> ts;
            lock (_gate) ts = _tailscalePeers.ToList();
            foreach (var ip in ts) await SendTo(udp, q, new IPEndPoint(ip, _opts.DiscoveryPort)).ConfigureAwait(false);
            await ProbeManual().ConfigureAwait(false);
        });
    }

    private async Task Broadcast(UdpClient udp, byte[] data)
    {
        foreach (var target in BroadcastAddresses())
            await SendTo(udp, data, new IPEndPoint(target, _opts.DiscoveryPort)).ConfigureAwait(false);
    }

    private static async Task SendTo(UdpClient udp, byte[] data, IPEndPoint to)
    {
        try { await udp.SendAsync(data, to).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    /// <summary>255.255.255.255 and the broadcast address of every IPv4 network this PC is in (not Tailscale's).</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || IsTailscale(u.Address) || u.IPv4Mask is null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 255)) continue;
                    for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(ip));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    // ---------- typed addresses ----------

    public void AddManual(string address)
    {
        lock (_gate) _manual.Add(address);
        _ = ProbeManual();
    }

    private async Task ProbeManual()
    {
        List<string> manual;
        lock (_gate) manual = _manual.ToList();
        foreach (var address in manual)
        {
            if (!LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out var host, out var port)) continue;
            IPAddress[] ips;
            try { ips = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, _ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { continue; }
            if (_udp is { } udp)
                foreach (var ip in ips.Where(i => i.AddressFamily == AddressFamily.InterNetwork))
                    await SendTo(udp, Message("query"), new IPEndPoint(ip, _opts.DiscoveryPort)).ConfigureAwait(false);
            if (await Probe(host, port, _ct).ConfigureAwait(false)) UpsertAddressOnly(address, host, LinkVia.Manual);
        }
    }

    private static async Task<bool> Probe(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { return false; }
    }

    // ---------- Tailscale ----------

    internal sealed record TailscalePeer(string HostName, string DnsName, IReadOnlyList<IPAddress> Ips, bool Online);

    /// <summary>The parts of "tailscale status --json" the link needs. Missing fields are normal.</summary>
    internal static (string? BackendState, List<TailscalePeer> Peers) ParseTailscaleStatus(string json)
    {
        var peers = new List<TailscalePeer>();
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return (null, peers); }
        if (root?["Peer"] is JsonObject map)
            foreach (var (_, node) in map)
            {
                if (node is not JsonObject p) continue;
                var ips = new List<IPAddress>();
                if (p["TailscaleIPs"] is JsonArray arr)
                    foreach (var a in arr)
                        if (a is JsonValue v && v.TryGetValue<string>(out var s) && IPAddress.TryParse(s, out var ip)) ips.Add(ip);
                if (ips.Count == 0) continue;
                peers.Add(new TailscalePeer(LinkProtocol.Str(p, "HostName") ?? "", (LinkProtocol.Str(p, "DNSName") ?? "").TrimEnd('.'),
                    ips.OrderBy(i => i.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).ToList(), LinkProtocol.Bool(p, "Online")));
            }
        return (LinkProtocol.Str(root, "BackendState"), peers);
    }

    public void RefreshTailscale()
    {
        if (_opts.TailscaleDiscovery && _running) _ = Task.Run(() => ScanTailscale(_ct));
    }

    private async Task TailscaleLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ScanTailscale(ct).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ScanTailscale(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var exe = TaildropService.TailscaleExe();
            if (exe is null) return;
            string json = await RunStatus(exe, ct).ConfigureAwait(false);
            if (json.Length == 0) return;
            var (state, peers) = ParseTailscaleStatus(json);
            bool up;
            lock (_gate)
            {
                up = state == "Running" && _tailscaleState is not null && _tailscaleState != "Running";
                _tailscaleState = state;
            }
            if (up) TailscaleUp?.Invoke();
            var online = peers.Where(p => p.Online && state == "Running").ToList();
            lock (_gate) _tailscalePeers = online.Select(p => p.Ips[0]).ToList();

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = Message("query");
            await Task.WhenAll(online.Select(async p =>
            {
                var ip = p.Ips[0];
                if (_udp is { } udp) await SendTo(udp, q, new IPEndPoint(ip, _opts.DiscoveryPort)).ConfigureAwait(false);
                if (!await Probe(ip.ToString(), LinkProtocol.Port, ct).ConfigureAwait(false)) return;
                string address = LinkProtocol.FormatAddress(ip, LinkProtocol.Port);
                lock (found) found.Add(address);
                UpsertAddressOnly(address, p.HostName.Length > 0 ? p.HostName : p.DnsName, LinkVia.Tailscale);
            })).ConfigureAwait(false);

            // Tailscale peers that went away (address-only entries; announced ones expire like LAN entries).
            bool removed = false;
            lock (_gate)
                foreach (var key in _entries.Where(kv => kv.Value.Id.Length == 0 && kv.Value.Via == LinkVia.Tailscale && !kv.Value.Addresses.Any(found.Contains)).Select(kv => kv.Key).ToList())
                    removed |= _entries.Remove(key);
            if (removed) _changed();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _opts.Log($"Link: Tailscale scan failed ({ex.GetType().Name})"); }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    private static async Task<string> RunStatus(string exe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        using var p = Process.Start(psi);
        if (p is null) return "";
        var output = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return ""; }
        return await output.ConfigureAwait(false);
    }
}
