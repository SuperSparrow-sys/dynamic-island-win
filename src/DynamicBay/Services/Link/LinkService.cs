using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicBay.Core;
using Microsoft.Win32;

namespace DynamicBay.Services.Link;

/// <summary>
/// Pairing attempts while "Koppeln erlauben" is open: at most <c>perAddress</c> per sender address in the period, so one
/// noisy device cannot lock the window for the real one, plus four times that in total. Not thread-safe (caller locks).
/// </summary>
internal sealed class PairingLimiter(int perAddress, TimeSpan period)
{
    private readonly Dictionary<string, Queue<DateTime>> _byAddress = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _all = new();

    public int GlobalLimit => perAddress * 4;

    /// <summary>True: this attempt may go on (and is counted).</summary>
    public bool TryAttempt(System.Net.IPAddress? from, DateTime now)
    {
        if (from is { IsIPv4MappedToIPv6: true }) from = from.MapToIPv4();
        string key = from?.ToString() ?? "?";
        Trim(_all, now);
        foreach (var k in _byAddress.Keys.ToList())
        {
            Trim(_byAddress[k], now);
            if (_byAddress[k].Count == 0) _byAddress.Remove(k);
        }
        if (!_byAddress.TryGetValue(key, out var q)) q = new Queue<DateTime>();
        if (q.Count >= perAddress || _all.Count >= GlobalLimit) return false;
        q.Enqueue(now);
        _byAddress[key] = q;
        _all.Enqueue(now);
        return true;
    }

    private void Trim(Queue<DateTime> q, DateTime now)
    {
        while (q.Count > 0 && now - q.Peek() > period) q.Dequeue();
    }
}

/// <summary>Why a link operation did not work: "unpaired", "pair-closed", "rate-limited", "busy", "auth", "unreachable", "protocol".</summary>
public sealed class LinkException(string reason, string? message = null, Exception? inner = null)
    : Exception(message ?? reason, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// DynamicBay to DynamicBay: finds other devices (LAN, Tailscale, typed address), pairs with them once (both users
/// compare a six-digit code) and keeps one TLS connection per paired device, with mutually pinned certificates.
/// Features plug in with <see cref="RegisterHandler"/> and <see cref="SendAsync"/>; files go with <see cref="SendFileAsync"/>.
/// <para>Events are raised on the <see cref="SynchronizationContext"/> passed in (the UI thread in the app), or on a
/// pool thread when there is none. Methods may be called from any thread.</para>
/// </summary>
public sealed partial class LinkService : IDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly LinkOptions _opts;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private readonly Dictionary<string, PairedDevice> _paired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkConnection> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DialState> _dial = new(StringComparer.Ordinal);
    private readonly HashSet<LinkConnection> _pending = new();
    private readonly Dictionary<string, List<Action<LinkMessage>>> _handlers = new(StringComparer.Ordinal);
    private readonly PairingLimiter _pairLimiter;
    private readonly string _boot = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly LinkDiscovery _discovery;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private SemaphoreSlim _wake = new(0);
    private DateTime _pairingUntil = DateTime.MinValue;
    private PairingSession? _activePairing;
    private bool _pairingStarting;
    private string _deviceName;
    private bool _systemHooked;

    private sealed class DialState
    {
        public int Failures, AuthFailures;
        public DateTime Next = DateTime.MinValue;
        public bool InFlight;
        public LinkPeerState State = LinkPeerState.Offline;
    }

    public LinkService(DeviceIdentity identity, string? deviceName, IEnumerable<PairedDevice>? paired, LinkOptions? options = null, SynchronizationContext? context = null)
    {
        _identity = identity;
        _opts = options ?? new LinkOptions();
        _context = context;
        _deviceName = CleanName(deviceName);
        _pairLimiter = new PairingLimiter(_opts.PairingRateLimit, _opts.PairingRatePeriod);
        foreach (var p in paired ?? Enumerable.Empty<PairedDevice>())
            if (p.Id.Length > 0 && p.Fingerprint.Length > 0) _paired[p.Id] = Clone(p);
        _discovery = new LinkDiscovery(_opts, () => DeviceId, () => DeviceName, () => ListeningPort, () => Post(() => DiscoveredChanged?.Invoke()));
        _discovery.TailscaleUp += () => { Log("Link: Tailscale connected, reconnecting"); Reconnect(); };
    }

    public string DeviceId => _identity.DeviceId;
    public string Fingerprint => _identity.Fingerprint;
    /// <summary>The name other devices see (sent when connecting).</summary>
    public string DeviceName { get => _deviceName; set => _deviceName = CleanName(value); }
    public bool IsRunning { get; private set; }
    /// <summary>The port really listened on (differs from the option when that was 0).</summary>
    public int ListeningPort { get; private set; }

    // ---------- events (on the context, see class summary) ----------

    public event Action<PairedDevice>? PeerOnline;
    public event Action<PairedDevice>? PeerOffline;
    public event Action<string, LinkPeerState>? PeerStateChanged;
    /// <summary>Every message of a type the link does not handle itself, also those with a registered handler.</summary>
    public event Action<LinkMessage>? MessageReceived;
    /// <summary>A pairing reached the code stage, on both devices: show <see cref="PairingSession.Code"/> and ask.</summary>
    public event Action<PairingSession>? PairingRequested;
    public event Action<PairingSession, PairingResult>? PairingFinished;
    /// <summary>The paired list changed (new pairing, removed, new address, rejected): save <see cref="PairedDevices"/>.</summary>
    public event Action? PairedDevicesChanged;
    public event Action? DiscoveredChanged;
    /// <summary>Another device offers a file. Without a subscriber offers are declined.</summary>
    public event Action<IncomingFileOffer>? FileOffered;
    public event Action<IncomingFileOffer, string>? FileReceived;
    public event Action<IncomingFileOffer, string>? FileFailed;

    // ---------- state ----------

    public IReadOnlyList<PairedDevice> PairedDevices { get { lock (_gate) return _paired.Values.Select(Clone).ToList(); } }

    public IReadOnlyList<DiscoveredDevice> Discovered
    {
        get
        {
            HashSet<string> paired;
            lock (_gate) paired = _paired.Keys.ToHashSet();
            return _discovery.Snapshot(paired.Contains);
        }
    }

    public LinkPeerState GetState(string peerId)
    {
        lock (_gate)
        {
            if (_paired.TryGetValue(peerId, out var p) && p.Rejected) return LinkPeerState.Rejected;
            if (_sessions.ContainsKey(peerId)) return LinkPeerState.Online;
            return _dial.TryGetValue(peerId, out var d) ? d.State : LinkPeerState.Offline;
        }
    }

    public bool IsOnline(string peerId) => GetState(peerId) == LinkPeerState.Online;

    /// <summary>Endpoints of the live connection to a peer (diagnostics and tests).</summary>
    internal (IPEndPoint? Local, IPEndPoint? Remote, bool Outbound, SslProtocols Protocol)? ConnectionInfo(string peerId)
    {
        lock (_gate) return _sessions.TryGetValue(peerId, out var c) ? (c.Local, c.Remote, c.Outbound, c.Protocol) : null;
    }

    // ---------- start / stop ----------

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _wake = new SemaphoreSlim(0);
            // Not exclusive: after a restart old connections may still linger on the port (TIME_WAIT), which would block
            // an exclusive bind. Another program on the port gains nothing: it cannot answer with a paired certificate.
            var listener = _opts.LoopbackOnly ? new TcpListener(IPAddress.Loopback, _opts.Port) : new TcpListener(IPAddress.IPv6Any, _opts.Port);
            if (!_opts.LoopbackOnly) listener.Server.DualMode = true;
            try { listener.Start(); }
            catch
            {
                listener.Server.Dispose();
                throw;
            }
            _listener = listener;
            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            IsRunning = true;
        }
        var ct = _cts.Token;
        var l = _listener;
        _ = Task.Run(() => AcceptLoop(l, ct));
        _ = Task.Run(() => SuperviseLoop(ct));
        _ = Task.Run(() => KeepAliveLoop(ct));
        _discovery.Start(ct);
        HookSystem(true);
        Log($"Link: started as {DeviceName} ({DeviceId}) on port {ListeningPort}, {_paired.Count} paired");
    }

    public void Stop()
    {
        List<LinkConnection> close;
        PairingSession? pairing;
        CancellationTokenSource? cts;
        TcpListener? listener;
        lock (_gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            cts = _cts;
            listener = _listener;
            _listener = null;
            close = _sessions.Values.Concat(_pending).ToList();
            pairing = _activePairing;
            _pairingUntil = DateTime.MinValue;
            foreach (var d in _dial.Values) { d.InFlight = false; d.Failures = 0; d.Next = DateTime.MinValue; }
        }
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        HookSystem(false);
        _discovery.Stop();
        foreach (var c in close) c.Close();
        if (pairing is not null) FinishPairing(pairing, null, PairingResult.Failed);
        Log("Link: stopped");
    }

    public void Dispose() => Stop();

    /// <summary>Try all offline devices now (network back, after sleep, the user pressed "Verbinden").</summary>
    public void Reconnect()
    {
        lock (_gate)
            foreach (var d in _dial.Values) { d.Failures = 0; d.Next = DateTime.MinValue; }
        Wake();
        _discovery.Query();
    }

    private void HookSystem(bool on)
    {
        if (!_opts.WatchSystem || on == _systemHooked) return;
        _systemHooked = on;
        try
        {
            if (on)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            else
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
        }
        catch (Exception ex) { Core.Log.Error("Link system events", ex); }
    }

    private int _netChangeSeq;

    // Fires several times while an adapter (Wi-Fi, Tailscale) comes up: wait until it settles, then try again.
    private async void OnNetworkChanged(object? sender, EventArgs e)
    {
        int seq = Interlocked.Increment(ref _netChangeSeq);
        await Task.Delay(1500).ConfigureAwait(false);
        if (seq != _netChangeSeq || !IsRunning) return;
        Log("Link: network changed, reconnecting");
        _discovery.RefreshTailscale();
        Reconnect();
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        await Task.Delay(3000).ConfigureAwait(false);
        if (!IsRunning) return;
        Log("Link: resumed, reconnecting");
        _discovery.RefreshTailscale();
        Reconnect();
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    // ---------- discovery ----------

    /// <summary>An address the user typed in (IP or host name, optional ":port"): probed and listed under Discovered.</summary>
    public void AddManualAddress(string address)
    {
        if (!LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out _, out _)) throw new ArgumentException("invalid address", nameof(address));
        _discovery.AddManual(address.Trim());
    }

    /// <summary>Asks the network again (LAN broadcast, Tailscale peers, typed addresses).</summary>
    public void RefreshDiscovery()
    {
        _discovery.Query();
        _discovery.RefreshTailscale();
    }

    // ---------- pairing ----------

    public bool PairingWindowOpen { get { lock (_gate) return DateTime.UtcNow < _pairingUntil; } }
    public DateTime PairingWindowEndsUtc { get { lock (_gate) return _pairingUntil; } }

    /// <summary>"Koppeln erlauben": for a while, other devices may start a pairing with this one (the user still confirms the code).</summary>
    public void AllowPairing(TimeSpan? duration = null)
    {
        lock (_gate) _pairingUntil = DateTime.UtcNow + (duration ?? _opts.PairingWindow);
        Log("Link: pairing allowed");
    }

    public void ClosePairingWindow()
    {
        lock (_gate) _pairingUntil = DateTime.MinValue;
    }

    /// <summary>Confirms or cancels the pairing in progress (same as <see cref="PairingSession.Confirm"/>).</summary>
    public void ConfirmPairing(bool accept)
    {
        PairingSession? s;
        lock (_gate) s = _activePairing;
        s?.Confirm(accept);
    }

    /// <summary>
    /// Starts pairing with the device at <paramref name="address"/>, which must have "Koppeln erlauben" open. Returns
    /// once both sides know the code; the result follows in <see cref="PairingSession.Completion"/>.
    /// Throws <see cref="LinkException"/> (reasons: pair-closed, rate-limited, busy, unreachable, auth, protocol).
    /// </summary>
    public async Task<PairingSession> PairAsync(string address, CancellationToken ct = default)
    {
        if (!IsRunning) throw new InvalidOperationException("link not started");
        if (!LinkProtocol.TryParseAddress(address, LinkProtocol.Port, out var host, out var port)) throw new ArgumentException("invalid address", nameof(address));
        lock (_gate)
        {
            if (_activePairing is not null || _pairingStarting) throw new LinkException("busy");
            _pairingStarting = true;
        }
        LinkConnection? conn = null;
        try
        {
            byte[]? commit = null;
            conn = await ConnectAsync(host, port, null, "pair", reply => commit = TryBase64(LinkProtocol.Str(reply, "commit")), ct).ConfigureAwait(false);
            if (commit is not { Length: 32 }) throw new LinkException("protocol", "no commitment");
            var nonceA = RandomNumberGenerator.GetBytes(32);
            await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.PairNonce, ["n"] = Convert.ToBase64String(nonceA) }, null, ct).ConfigureAwait(false);
            var next = await ReceiveWithTimeout(conn, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var nonceB = TryBase64(LinkProtocol.Str(next, "n"));
            if (LinkProtocol.Str(next, "t") != LinkProtocol.PairNonce || nonceB is not { Length: 32 }) throw new LinkException("protocol", "no nonce");
            if (!CryptographicOperations.FixedTimeEquals(commit, LinkProtocol.Commitment(conn.PeerFingerprint, Fingerprint, nonceB)))
                throw new LinkException("protocol", "commitment does not match");
            var code = LinkProtocol.PairingCode(Fingerprint, conn.PeerFingerprint, nonceA, nonceB);
            var session = BeginPairing(conn, code, initiator: true);
            return session;
        }
        catch (Exception ex)
        {
            conn?.Close();
            if (ex is LinkException or OperationCanceledException) throw;
            throw new LinkException("unreachable", ex.Message, ex);
        }
        finally
        {
            lock (_gate) _pairingStarting = false;
        }
    }

    private PairingSession BeginPairing(LinkConnection conn, string code, bool initiator)
    {
        var session = new PairingSession(conn.PeerId, conn.PeerName, conn.PeerFingerprint, code, initiator);
        lock (_gate)
        {
            if (_activePairing is not null) throw new LinkException("busy");
            _activePairing = session;
            _pending.Add(conn);
        }
        conn.Pairing = session;
        session.OnConfirm = accept => _ = OnLocalConfirm(conn, session, accept);
        conn.OnClosed += c =>
        {
            lock (_gate) _pending.Remove(c);
            if (c.Pairing is { } s) FinishPairing(s, null, PairingResult.Failed);
        };
        if (conn.IsClosed) { FinishPairing(session, conn, PairingResult.Failed); return session; }
        StartReadLoop(conn);
        var timeout = _opts.ConfirmTimeout;
        _ = Task.Delay(timeout).ContinueWith(_ => FinishPairing(session, conn, PairingResult.TimedOut), TaskScheduler.Default);
        Log($"Link: pairing with {conn.PeerName} ({conn.PeerId}), waiting for both confirmations");
        Post(() => PairingRequested?.Invoke(session));
        return session;
    }

    private async Task OnLocalConfirm(LinkConnection conn, PairingSession session, bool accept)
    {
        bool complete;
        lock (_gate)
        {
            if (session.Done.Task.IsCompleted || session.LocalConfirmed) return;
            session.LocalConfirmed = accept;
        }
        try { await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.PairConfirm, ["ok"] = accept }).ConfigureAwait(false); }
        catch (IOException) { FinishPairing(session, conn, PairingResult.Failed); return; }
        if (!accept) { FinishPairing(session, conn, PairingResult.Declined); return; }
        // Complete only once our confirmation is on the wire: nothing we send afterwards can overtake it.
        lock (_gate)
        {
            session.LocalSent = true;
            complete = session.RemoteConfirmed;
        }
        if (complete) CompletePairing(conn, session);
    }

    private void OnRemoteConfirm(LinkConnection conn, PairingSession session, bool accept)
    {
        if (!accept) { FinishPairing(session, conn, PairingResult.Declined); conn.Close(); return; } // we read their last word: close now
        bool complete;
        lock (_gate)
        {
            if (session.Done.Task.IsCompleted) return;
            session.RemoteConfirmed = true;
            complete = session.LocalSent;
        }
        if (complete) CompletePairing(conn, session);
    }

    private void CompletePairing(LinkConnection conn, PairingSession session)
    {
        PairedDevice device;
        lock (_gate)
        {
            if (session.Done.Task.IsCompleted || conn.IsClosed) return;
            device = new PairedDevice
            {
                Id = conn.PeerId, Name = conn.PeerName, Fingerprint = conn.PeerFingerprint, PairedAt = DateTime.UtcNow,
                LastAddresses = conn.Remote is { } r ? new() { LinkProtocol.FormatAddress(r.Address, conn.PeerPort) } : new(),
            };
            _paired[device.Id] = device;
            _dial.Remove(device.Id);
            _pairingUntil = DateTime.MinValue; // one pairing per "Koppeln erlauben"
            if (_activePairing == session) _activePairing = null;
            _pending.Remove(conn);
            conn.Pairing = null;
            session.Done.TrySetResult(PairingResult.Paired);
        }
        Log($"Link: paired with {device.Name} ({device.Id})");
        Post(() => PairingFinished?.Invoke(session, PairingResult.Paired));
        Post(() => PairedDevicesChanged?.Invoke());
        Attach(conn);
    }

    private void FinishPairing(PairingSession session, LinkConnection? conn, PairingResult result)
    {
        lock (_gate)
        {
            if (!session.Done.TrySetResult(result)) return;
            if (_activePairing == session) _activePairing = null;
        }
        // conn.Pairing stays set: the connection carries nothing else until it is gone.
        if (conn is not null)
        {
            if (result == PairingResult.Declined) conn.CloseSoon();
            else conn.Close();
        }
        Log($"Link: pairing with {session.PeerName} ended: {result}");
        Post(() => PairingFinished?.Invoke(session, result));
    }

    /// <summary>Removes the device and tells it (when online) so it stops connecting; it shows the pairing as rejected.</summary>
    public async Task UnpairAsync(string peerId)
    {
        LinkConnection? conn;
        PairedDevice? device;
        lock (_gate)
        {
            _paired.Remove(peerId, out device);
            _dial.Remove(peerId);
            _sessions.Remove(peerId, out conn);
        }
        if (device is null) return;
        if (conn is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.Unpair }, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            conn.CloseSoon();
            Post(() => PeerOffline?.Invoke(device));
        }
        Log($"Link: unpaired {device.Name} ({device.Id})");
        Post(() => PeerStateChanged?.Invoke(peerId, LinkPeerState.Offline));
        Post(() => PairedDevicesChanged?.Invoke());
    }

    private void MarkRejected(string peerId, string why)
    {
        LinkConnection? conn;
        PairedDevice? device;
        lock (_gate)
        {
            if (!_paired.TryGetValue(peerId, out device) || device.Rejected) return;
            device.Rejected = true;
            _sessions.Remove(peerId, out conn);
            if (_dial.TryGetValue(peerId, out var d)) d.State = LinkPeerState.Rejected;
        }
        conn?.Close();
        Log($"Link: {device.Name} ({device.Id}) rejected the pairing ({why})");
        var copy = Clone(device);
        if (conn is not null) Post(() => PeerOffline?.Invoke(copy));
        Post(() => PeerStateChanged?.Invoke(peerId, LinkPeerState.Rejected));
        Post(() => PairedDevicesChanged?.Invoke());
    }

    // ---------- connecting ----------

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; } // listener stopped
            catch (SocketException) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleInbound(tcp, ct));
        }
    }

    private bool AcceptsCertificate(string fingerprint)
    {
        lock (_gate)
            return _paired.Values.Any(p => p.Fingerprint == fingerprint) || DateTime.UtcNow < _pairingUntil;
    }

    private async Task HandleInbound(TcpClient tcp, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var ssl = new SslStream(tcp.GetStream(), false);
        LinkConnection? conn = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _identity.Context,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // Self-signed on purpose: trust comes from the pinned fingerprint, not from a certificate authority.
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && AcceptsCertificate(DeviceIdentity.FingerprintOf(cert)),
            }, timeout.Token).ConfigureAwait(false);
            conn = NewConnection(tcp, ssl, outbound: false);
            var hello = await ReceiveWithTimeout(conn, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (!ReadHello(conn, hello)) throw new LinkException("protocol", "bad hello");
            string intent = LinkProtocol.Str(hello, "intent") ?? "connect";
            if (intent == "pair") { await RespondToPairing(conn, ct).ConfigureAwait(false); return; }

            bool paired;
            lock (_gate) paired = _paired.TryGetValue(conn.PeerId, out var p) && p.Fingerprint == conn.PeerFingerprint;
            if (!paired)
            {
                await SendReject(conn, "unpaired").ConfigureAwait(false);
                return;
            }
            await conn.SendAsync(HelloEnvelope("connect"), null, ct).ConfigureAwait(false);
            Attach(conn);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && ex is not OperationCanceledException)
                Log($"Link: incoming connection from {tcp.Client?.RemoteEndPoint?.ToString() ?? "?"} refused ({Short(ex)})");
            conn?.Close();
            try { ssl.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
        }
    }

    private async Task RespondToPairing(LinkConnection conn, CancellationToken ct)
    {
        string? refuse = null;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now >= _pairingUntil) refuse = "pair-closed";
            else if (!_pairLimiter.TryAttempt(conn.Remote?.Address, now)) refuse = "rate-limited";
            else if (_activePairing is not null || _pairingStarting) refuse = "busy";
        }
        if (refuse is not null)
        {
            Log($"Link: pairing request from {conn.PeerName} refused ({refuse})");
            await SendReject(conn, refuse).ConfigureAwait(false);
            return;
        }
        var nonceB = RandomNumberGenerator.GetBytes(32);
        var hello = HelloEnvelope("pair");
        hello["commit"] = Convert.ToBase64String(LinkProtocol.Commitment(Fingerprint, conn.PeerFingerprint, nonceB));
        await conn.SendAsync(hello, null, ct).ConfigureAwait(false);
        var msg = await ReceiveWithTimeout(conn, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var nonceA = TryBase64(LinkProtocol.Str(msg, "n"));
        if (LinkProtocol.Str(msg, "t") != LinkProtocol.PairNonce || nonceA is not { Length: 32 }) throw new LinkException("protocol", "no nonce");
        await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.PairNonce, ["n"] = Convert.ToBase64String(nonceB) }, null, ct).ConfigureAwait(false);
        BeginPairing(conn, LinkProtocol.PairingCode(Fingerprint, conn.PeerFingerprint, nonceA, nonceB), initiator: false);
    }

    private static async Task SendReject(LinkConnection conn, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await conn.SendAsync(new JsonObject { ["t"] = LinkProtocol.Reject, ["reason"] = reason }, null, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        conn.CloseSoon();
    }

    /// <summary>
    /// Dials, runs TLS (server certificate pinned to <paramref name="expectedFingerprint"/>, any one when pairing) and the
    /// hello exchange. <paramref name="onReply"/> sees the other side's hello.
    /// </summary>
    private async Task<LinkConnection> ConnectAsync(string host, int port, string? expectedFingerprint, string intent, Action<JsonObject>? onReply, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0))
        {
            var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await tcp.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                tcp.Dispose();
                last = ex;
                continue;
            }
            return await HandshakeAsync(tcp, expectedFingerprint, intent, onReply, ct).ConfigureAwait(false);
        }
        throw new LinkException("unreachable", last?.Message ?? "no address", last);
    }

    private async Task<LinkConnection> HandshakeAsync(TcpClient tcp, string? expectedFingerprint, string intent, Action<JsonObject>? onReply, CancellationToken ct)
    {
        var ssl = new SslStream(tcp.GetStream(), false);
        LinkConnection? conn = null;
        // Who said no matters: only a device that showed the pinned certificate and then refused ours counts toward
        // "rejected". Another PC or DynamicBay at the old address (or anyone announcing the paired id) is just not it.
        bool pinMatched = false, weRefused = false;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "DynamicBay",
                        ClientCertificateContext = _identity.Context,
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        {
                            bool ok = cert is not null && (expectedFingerprint is null || DeviceIdentity.FingerprintOf(cert) == expectedFingerprint);
                            if (ok) pinMatched = true; else weRefused = true;
                            return ok;
                        },
                    }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException)
                {
                    throw new LinkException(FailureReason(pinMatched, weRefused), ex.Message, ex);
                }
            }
            conn = NewConnection(tcp, ssl, outbound: true);
            await conn.SendAsync(HelloEnvelope(intent), null, ct).ConfigureAwait(false);
            JsonObject? reply;
            // The other side turned our certificate down: with TLS 1.3 that only shows as a closed connection here.
            try { reply = await ReceiveWithTimeout(conn, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (IOException ex) { throw new LinkException("auth", ex.Message, ex); }
            if (reply is null) throw new LinkException("auth", "closed before hello");
            if (LinkProtocol.Str(reply, "t") == LinkProtocol.Reject) throw new LinkException(LinkProtocol.Str(reply, "reason") ?? "rejected");
            if (!ReadHello(conn, reply)) throw new LinkException("protocol", "bad hello");
            onReply?.Invoke(reply);
            return conn;
        }
        catch
        {
            conn?.Close();
            try { ssl.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// A failed TLS handshake: "auth" only when the other side showed the pinned certificate (then it refused ours),
    /// "wrong-device" when we refused its certificate, "unreachable" when it never got that far (no TLS there).
    /// </summary>
    internal static string FailureReason(bool pinMatched, bool weRefused) =>
        pinMatched ? "auth" : weRefused ? "wrong-device" : "unreachable";

    private LinkConnection NewConnection(TcpClient tcp, SslStream ssl, bool outbound)
    {
        var cert = ssl.RemoteCertificate as X509Certificate2 ?? throw new LinkException("auth", "no certificate");
        return new LinkConnection(tcp, ssl, outbound)
        {
            PeerFingerprint = DeviceIdentity.FingerprintOf(cert),
            PeerId = DeviceIdentity.IdOf(cert),
        };
    }

    private JsonObject HelloEnvelope(string intent) => new()
    {
        ["t"] = LinkProtocol.Hello, ["id"] = DeviceId, ["name"] = DeviceName, ["port"] = ListeningPort,
        ["proto"] = LinkProtocol.Version, ["boot"] = _boot, ["intent"] = intent,
    };

    /// <summary>The hello must name the device its certificate belongs to.</summary>
    private static bool ReadHello(LinkConnection conn, JsonObject? hello)
    {
        if (LinkProtocol.Str(hello, "t") != LinkProtocol.Hello) return false;
        if (LinkProtocol.Str(hello, "id") != conn.PeerId) return false;
        if (LinkProtocol.Long(hello, "proto") is not >= 1) return false;
        conn.PeerName = CleanName(LinkProtocol.Str(hello, "name"));
        conn.PeerPort = LinkProtocol.Long(hello, "port") is long p and > 0 and < 65536 ? (int)p : LinkProtocol.Port;
        conn.PeerBoot = LinkProtocol.Str(hello, "boot") ?? "";
        return true;
    }

    private static async Task<JsonObject?> ReceiveWithTimeout(LinkConnection conn, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return (await conn.ReceiveAsync(cts.Token).ConfigureAwait(false))?.Envelope; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LinkException("protocol", "no answer"); }
    }

    /// <summary>A connection that passed hello becomes the one for its device (see <see cref="LinkProtocol.KeepNew"/>).</summary>
    private void Attach(LinkConnection conn)
    {
        LinkConnection? loser = null;
        bool cameOnline, addressesChanged = false;
        PairedDevice? device;
        lock (_gate)
        {
            if (!IsRunning || !_paired.TryGetValue(conn.PeerId, out device) || device.Fingerprint != conn.PeerFingerprint)
            {
                device = null;
                cameOnline = false;
                loser = conn;
            }
            else
            {
                cameOnline = true;
                if (_sessions.TryGetValue(conn.PeerId, out var old) && !old.IsClosed)
                {
                    if (LinkProtocol.KeepNew(DeviceId, conn.PeerId, old.Outbound, old.PeerBoot, conn.Outbound, conn.PeerBoot))
                    {
                        loser = old;
                        cameOnline = false;
                    }
                    else
                    {
                        loser = conn;
                        device = null;
                    }
                }
                if (device is not null)
                {
                    _sessions[conn.PeerId] = conn;
                    var d = DialFor(conn.PeerId);
                    d.Failures = 0; d.AuthFailures = 0; d.State = LinkPeerState.Online;
                    device.Rejected = false;
                    if (!string.Equals(device.Name, conn.PeerName, StringComparison.Ordinal)) { device.Name = conn.PeerName; addressesChanged = true; }
                    if (conn.Remote is { } r)
                    {
                        var a = LinkProtocol.FormatAddress(r.Address, conn.PeerPort);
                        if (device.LastAddresses.FirstOrDefault() != a)
                        {
                            device.LastAddresses.Remove(a);
                            device.LastAddresses.Insert(0, a);
                            if (device.LastAddresses.Count > 4) device.LastAddresses.RemoveRange(4, device.LastAddresses.Count - 4);
                            addressesChanged = true;
                        }
                    }
                }
            }
        }
        loser?.Close();
        if (device is null) return;
        conn.OnClosed += OnSessionClosed;
        if (conn.IsClosed) { OnSessionClosed(conn); return; }
        StartReadLoop(conn);
        var copy = Clone(device);
        if (cameOnline)
        {
            Log($"Link: {copy.Name} ({copy.Id}) online, {conn}");
            Post(() => PeerStateChanged?.Invoke(copy.Id, LinkPeerState.Online));
            Post(() => PeerOnline?.Invoke(copy));
        }
        if (addressesChanged) Post(() => PairedDevicesChanged?.Invoke());
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<LinkConnection, byte> _readLoops = new();

    private void StartReadLoop(LinkConnection conn)
    {
        if (_readLoops.TryAdd(conn, 0)) _ = Task.Run(() => ReadLoop(conn));
    }

    private void OnSessionClosed(LinkConnection conn)
    {
        PairedDevice? device = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(conn.PeerId, out var current) && current == conn)
            {
                _sessions.Remove(conn.PeerId);
                if (_paired.TryGetValue(conn.PeerId, out var p))
                {
                    device = Clone(p);
                    var d = DialFor(conn.PeerId);
                    d.State = p.Rejected ? LinkPeerState.Rejected : LinkPeerState.Offline;
                    d.Next = DateTime.UtcNow + _opts.MinBackoff;
                }
            }
        }
        FailTransfers(conn);
        if (device is null) return;
        Log($"Link: {device.Name} ({device.Id}) offline");
        Post(() => PeerStateChanged?.Invoke(device.Id, device.Rejected ? LinkPeerState.Rejected : LinkPeerState.Offline));
        Post(() => PeerOffline?.Invoke(device));
        Wake();
    }

    private DialState DialFor(string id)
    {
        if (!_dial.TryGetValue(id, out var d)) _dial[id] = d = new DialState();
        return d;
    }

    /// <summary>Dials every paired device that is offline, with backoff (1 s doubling up to 60 s).</summary>
    private async Task SuperviseLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var due = new List<PairedDevice>();
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                foreach (var p in _paired.Values)
                {
                    if (p.Rejected || _sessions.ContainsKey(p.Id)) continue;
                    var d = DialFor(p.Id);
                    if (d.InFlight || d.Next > now) continue;
                    d.InFlight = true;
                    due.Add(Clone(p));
                }
            }
            foreach (var p in due) _ = Task.Run(() => DialAsync(p, ct));
            try { await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DialAsync(PairedDevice peer, CancellationToken ct)
    {
        SetDialState(peer.Id, LinkPeerState.Connecting);
        var addresses = peer.LastAddresses.Concat(_discovery.AddressesFor(peer.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool authFailed = false;
        try
        {
            foreach (var a in addresses)
            {
                if (ct.IsCancellationRequested) return;
                lock (_gate) if (_sessions.ContainsKey(peer.Id)) return; // the other side was faster
                if (!LinkProtocol.TryParseAddress(a, LinkProtocol.Port, out var host, out var port)) continue;
                try
                {
                    var conn = await ConnectAsync(host, port, peer.Fingerprint, "connect", null, ct).ConfigureAwait(false);
                    if (conn.PeerId != peer.Id) { conn.Close(); continue; }
                    Attach(conn);
                    return;
                }
                catch (LinkException ex) when (ex.Reason == "unpaired") { MarkRejected(peer.Id, "removed on the other device"); return; }
                catch (LinkException ex) when (ex.Reason == "auth") { authFailed = true; }
                catch (Exception ex) when (ex is LinkException or SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested) { }
            }
            bool reject = false;
            lock (_gate)
            {
                if (!_dial.TryGetValue(peer.Id, out var d)) return;
                d.Failures++;
                d.AuthFailures = authFailed ? d.AuthFailures + 1 : 0;
                // Three times in a row the device answered but refused our certificate: it no longer knows us.
                reject = d.AuthFailures >= 3;
                double secs = Math.Min(_opts.MaxBackoff.TotalSeconds, _opts.MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(d.Failures - 1, 16)));
                d.Next = DateTime.UtcNow + TimeSpan.FromSeconds(secs);
            }
            if (reject) MarkRejected(peer.Id, "certificate refused");
            else SetDialState(peer.Id, LinkPeerState.Offline);
        }
        finally
        {
            lock (_gate) if (_dial.TryGetValue(peer.Id, out var d)) d.InFlight = false;
        }
    }

    private void SetDialState(string id, LinkPeerState state)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id) || !_dial.TryGetValue(id, out var d) || d.State == state || d.State == LinkPeerState.Rejected) return;
            d.State = state;
        }
        Post(() => PeerStateChanged?.Invoke(id, state));
    }

    /// <summary>Ping every 15 s; a connection silent for 45 s is dead (sleeping laptop, lost Wi-Fi) and gets closed.</summary>
    private async Task KeepAliveLoop(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_opts.PingInterval.TotalMilliseconds / 3, 100, 1000));
        var lastPing = new Dictionary<LinkConnection, DateTime>();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            List<LinkConnection> all;
            lock (_gate) all = _sessions.Values.Concat(_pending).ToList();
            var now = DateTime.UtcNow;
            foreach (var c in all)
            {
                if (now - c.LastReceived > _opts.PingTimeout) { Log($"Link: {c.PeerName} silent, closing"); c.Close(); continue; }
                if (!lastPing.TryGetValue(c, out var t) || now - t >= _opts.PingInterval)
                {
                    lastPing[c] = now;
                    _ = SendQuietly(c, new JsonObject { ["t"] = LinkProtocol.Ping });
                }
            }
            foreach (var c in lastPing.Keys.Where(c => c.IsClosed).ToList()) lastPing.Remove(c);
        }
    }

    private static async Task SendQuietly(LinkConnection c, JsonObject env)
    {
        try { await c.SendAsync(env).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    // ---------- messages ----------

    private async Task ReadLoop(LinkConnection conn)
    {
        _readLoops.TryAdd(conn, 0);
        try
        {
            while (!conn.IsClosed)
            {
                var frame = await conn.ReceiveAsync().ConfigureAwait(false);
                if (frame is null) break;
                await Dispatch(conn, frame.Value.Envelope, frame.Value.Binary).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or AuthenticationException) { }
        catch (Exception ex) { Core.Log.Error("Link read", ex); }
        finally
        {
            conn.Close();
            _readLoops.TryRemove(conn, out _);
        }
    }

    private async Task Dispatch(LinkConnection conn, JsonObject env, byte[]? bin)
    {
        string type = LinkProtocol.Str(env, "t")!;
        switch (type)
        {
            case LinkProtocol.Ping: _ = SendQuietly(conn, new JsonObject { ["t"] = LinkProtocol.Pong }); return;
            case LinkProtocol.Pong: return;
        }
        if (conn.Pairing is { } pairing)
        {
            if (type == LinkProtocol.PairConfirm) OnRemoteConfirm(conn, pairing, LinkProtocol.Bool(env, "ok"));
            return; // nothing else counts before both confirmed
        }
        lock (_gate)
            if (!_paired.TryGetValue(conn.PeerId, out var p) || p.Rejected || p.Fingerprint != conn.PeerFingerprint) return; // removed meanwhile
        if (type == LinkProtocol.Unpair) { MarkRejected(conn.PeerId, "removed on the other device"); conn.Close(); return; }
        if (type.StartsWith("file.", StringComparison.Ordinal)) { await OnFileMessage(conn, type, env, bin).ConfigureAwait(false); return; }
        if (LinkProtocol.IsReserved(type)) return;

        var msg = new LinkMessage(conn.PeerId, conn.PeerName, type, LinkProtocol.Str(env, "id") ?? "", env["p"]?.DeepClone());
        List<Action<LinkMessage>>? handlers;
        lock (_gate) handlers = _handlers.TryGetValue(type, out var h) ? h.ToList() : null;
        Post(() =>
        {
            if (handlers is not null)
                foreach (var h in handlers)
                    try { h(msg); } catch (Exception ex) { Core.Log.Error("Link handler " + type, ex); }
            MessageReceived?.Invoke(msg);
        });
    }

    /// <summary>
    /// A feature's handler for one message type ("clipboard", "notification", "media", "status" …); dispose the result
    /// to remove it. Handlers run on the service's context.
    /// </summary>
    public IDisposable RegisterHandler(string type, Action<LinkMessage> handler)
    {
        if (string.IsNullOrWhiteSpace(type) || LinkProtocol.IsReserved(type)) throw new ArgumentException($"type \"{type}\" is reserved", nameof(type));
        lock (_gate)
        {
            if (!_handlers.TryGetValue(type, out var list)) _handlers[type] = list = new();
            list.Add(handler);
        }
        return new Unregister(() => { lock (_gate) if (_handlers.TryGetValue(type, out var l)) l.Remove(handler); });
    }

    private sealed class Unregister(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    /// <summary>Sends <paramref name="payload"/> (serialized to JSON) to a connected device. False: not online or the connection broke.</summary>
    public async Task<bool> SendAsync(string peerId, string type, object? payload = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type) || LinkProtocol.IsReserved(type)) throw new ArgumentException($"type \"{type}\" is reserved", nameof(type));
        var env = new JsonObject
        {
            ["t"] = type, ["id"] = Guid.NewGuid().ToString("N")[..12],
            ["p"] = payload switch { null => null, JsonNode n => n.DeepClone(), _ => JsonSerializer.SerializeToNode(payload) },
        };
        if (LinkProtocol.Encode(env).Length > LinkProtocol.MaxFrame) throw new ArgumentException("message too large", nameof(payload));
        LinkConnection? conn;
        lock (_gate) _sessions.TryGetValue(peerId, out conn);
        if (conn is null) return false;
        try { await conn.SendAsync(env, null, ct).ConfigureAwait(false); return true; }
        catch (IOException) { return false; }
    }

    // ---------- helpers ----------

    private void Post(Action a)
    {
        void Run()
        {
            try { a(); } catch (Exception ex) { Core.Log.Error("Link event", ex); }
        }
        if (_context is not null) _context.Post(_ => Run(), null);
        else Run();
    }

    private void Log(string message)
    {
        try { _opts.Log(message); } catch { }
    }

    private static string Short(Exception ex) => ex is LinkException l ? l.Reason : ex.GetType().Name + ": " + ex.Message;

    private static byte[]? TryBase64(string? s)
    {
        if (s is null) return null;
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }

    internal static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > 64) s = s[..64].Trim();
        return s.Length == 0 ? Environment.MachineName : s;
    }

    private static PairedDevice Clone(PairedDevice p) => new()
    {
        Id = p.Id, Name = p.Name, Fingerprint = p.Fingerprint, LastAddresses = p.LastAddresses.ToList(), PairedAt = p.PairedAt, Rejected = p.Rejected,
    };
}
