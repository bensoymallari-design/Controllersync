using System.Net;
using System.Net.Sockets;

namespace ControllerSync.Core;

public enum LinkState
{
    Stopped,
    Listening,
    Linked
}

public sealed record LinkSnapshot(LinkState State, string Detail);

public sealed class SyncNodeOptions
{
    public string BindAddress { get; init; } = "0.0.0.0";
    public int ListenPort { get; init; } = 24710;
    public string? RemoteAddress { get; init; }
    public int RemotePort { get; init; } = 24710;
    public string Channel { get; init; } = "show";
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
}

public sealed class SyncNode : ISyncTransport, IAsyncDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _gate = new();
    private readonly object _seenGate = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private SyncNodeOptions _options = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private PeerConn? _outgoing;
    private PeerConn? _incoming;
    private Task? _acceptTask;
    private Task? _connectTask;
    private Task? _pingTask;
    private string? _peerName;
    private int _rejected;
    private long _seq;
    private int _disposed;

    public event Action<SyncMessage>? MessageReceived;
    public event Action<LinkSnapshot>? LinkChanged;
    public event Action<string>? Notice;

    public int BoundPort { get; private set; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return Alive(_outgoing) || Alive(_incoming);
        }
    }

    public Task StartAsync(SyncNodeOptions options, CancellationToken cancellationToken = default)
    {
        if (_cts != null)
            throw new InvalidOperationException("The link is already running.");

        _options = options;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bind = ResolveBind(options.BindAddress);
        _listener = new TcpListener(bind, options.ListenPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _cts.Dispose();
            _cts = null;
            _listener = null;
            throw new InvalidOperationException(
                $"Could not listen on {options.BindAddress}:{options.ListenPort}. {ExplainBind(ex)}",
                ex);
        }

        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var token = _cts.Token;
        _acceptTask = Task.Run(() => AcceptLoop(token), token);
        _connectTask = Task.Run(() => ConnectLoop(token), token);
        _pingTask = Task.Run(() => PingLoop(token), token);
        Publish(LinkState.Listening, "Waiting for the other laptop");
        return Task.CompletedTask;
    }

    public async Task SendAsync(SyncMessage message, CancellationToken cancellationToken = default)
    {
        var frame = SyncCodec.EncodeFrame(message);
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PeerConn? conn;
            lock (_gate)
                conn = Alive(_outgoing) ? _outgoing : Alive(_incoming) ? _incoming : null;
            if (conn == null)
                throw new IOException("Not linked.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await conn.Stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                Drop(conn);
                throw;
            }
        }
        finally
        {
            _write.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        var cts = _cts;
        _cts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Listener is already closed.
        }

        PeerConn? outgoing;
        PeerConn? incoming;
        lock (_gate)
        {
            outgoing = _outgoing;
            incoming = _incoming;
            _outgoing = null;
            _incoming = null;
        }

        outgoing?.Client.Dispose();
        incoming?.Client.Dispose();

        await WaitQuiet(_acceptTask).ConfigureAwait(false);
        await WaitQuiet(_connectTask).ConfigureAwait(false);
        await WaitQuiet(_pingTask).ConfigureAwait(false);
        cts?.Dispose();
        Publish(LinkState.Stopped, "Stopped");
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Notice?.Invoke("Listening stopped: " + ex.Message);
                break;
            }

            Attach(client, outgoing: false, cancellationToken);
            try
            {
                await SendHelloAsync().ConfigureAwait(false);
            }
            catch
            {
                // The hello is retried by the ping loop on the other side's connection.
            }
        }
    }

    private async Task ConnectLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.RemoteAddress))
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    if (Alive(_outgoing))
                    {
                        // Already connected.
                    }
                    else
                    {
                        _outgoing = null;
                    }
                }

                if (Alive(_outgoing))
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var client = new TcpClient();
                try
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await client.ConnectAsync(_options.RemoteAddress, _options.RemotePort, connectTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                Attach(client, outgoing: true, cancellationToken);
                try
                {
                    await SendHelloAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Read loop will notice a dead socket.
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void Attach(TcpClient client, bool outgoing, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch
        {
            // Keepalive is optional.
        }

        var conn = new PeerConn(client);
        PeerConn? previous;
        lock (_gate)
        {
            if (outgoing)
            {
                previous = _outgoing;
                _outgoing = conn;
            }
            else
            {
                previous = _incoming;
                _incoming = conn;
            }
        }

        previous?.Client.Dispose();
        conn.ReadTask = ReadLoop(conn, outgoing, cancellationToken);
        Publish(LinkState.Linked, string.IsNullOrEmpty(_peerName) ? "Linked" : "Linked to " + _peerName);
    }

    private async Task ReadLoop(PeerConn conn, bool outgoing, CancellationToken cancellationToken)
    {
        var reader = new FrameReader();
        var scratch = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await conn.Stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (read == 0)
                    break;

                try
                {
                    reader.Append(scratch.AsSpan(0, read));
                    while (reader.TryRead(out var frame))
                    {
                        if (!SyncCodec.TryDecode(frame, out var message, out var error) || message == null)
                        {
                            if (!string.IsNullOrEmpty(error))
                                Notice?.Invoke(error);
                            continue;
                        }

                        Handle(message);
                    }
                }
                catch (InvalidDataException)
                {
                    Notice?.Invoke("Closed a link because a packet was not valid.");
                    break;
                }
            }
        }
        catch (IOException)
        {
            // Peer went away.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        finally
        {
            lock (_gate)
            {
                if (outgoing && ReferenceEquals(_outgoing, conn))
                    _outgoing = null;
                if (!outgoing && ReferenceEquals(_incoming, conn))
                    _incoming = null;
            }

            try
            {
                conn.Client.Dispose();
            }
            catch
            {
                // Already closed.
            }

            Publish(IsConnected ? LinkState.Linked : LinkState.Listening,
                IsConnected
                    ? (string.IsNullOrEmpty(_peerName) ? "Linked" : "Linked to " + _peerName)
                    : "Waiting for the other laptop");
        }
    }

    private void Handle(SyncMessage message)
    {
        if (message.V != 1)
            return;
        if (!ChannelGuard.Matches(_options.Channel, message.Channel))
        {
            if (Interlocked.Increment(ref _rejected) == 1)
                Notice?.Invoke("A laptop answered on a different channel. Both channel names must match.");
            return;
        }

        lock (_seenGate)
        {
            if (!Remember(message.Id))
                return;
        }

        if (!string.IsNullOrWhiteSpace(message.Name) && message.Kind == SyncKind.Hello)
        {
            lock (_gate)
                _peerName = message.Name;
            Publish(LinkState.Linked, "Linked to " + message.Name);
        }

        if (message.Kind == SyncKind.Ping)
        {
            var pong = SyncMessage.Create(
                _options.MachineId,
                _options.MachineName,
                _options.Channel,
                SyncKind.Pong,
                Interlocked.Increment(ref _seq));
            _ = SendQuiet(pong);
            return;
        }

        if (message.Kind is SyncKind.Pong or SyncKind.Hello)
            return;

        MessageReceived?.Invoke(message);
    }

    private async Task PingLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (!IsConnected)
                    continue;
                var ping = SyncMessage.Create(
                    _options.MachineId,
                    _options.MachineName,
                    _options.Channel,
                    SyncKind.Ping,
                    Interlocked.Increment(ref _seq));
                await SendAsync(ping, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // SendAsync already dropped the dead socket. ConnectLoop opens a new one.
            }
        }
    }

    private void Drop(PeerConn conn)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_outgoing, conn))
                _outgoing = null;
            if (ReferenceEquals(_incoming, conn))
                _incoming = null;
        }

        try
        {
            conn.Client.Dispose();
        }
        catch
        {
            // Already closed.
        }
    }

    private async Task SendHelloAsync()
    {
        var hello = SyncMessage.Create(
            _options.MachineId,
            _options.MachineName,
            _options.Channel,
            SyncKind.Hello,
            Interlocked.Increment(ref _seq));
        await SendAsync(hello).ConfigureAwait(false);
    }

    private async Task SendQuiet(SyncMessage message)
    {
        try
        {
            await SendAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // The next hello or cue will try again.
        }
    }

    private bool Remember(string id)
    {
        if (string.IsNullOrEmpty(id) || !_seen.Add(id))
            return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 512)
            _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private void Publish(LinkState state, string detail) =>
        LinkChanged?.Invoke(new LinkSnapshot(state, detail));

    private static bool Alive(PeerConn? conn) =>
        conn != null && conn.Client.Connected;

    private static IPAddress ResolveBind(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind) || bind is "0.0.0.0")
            return IPAddress.Any;
        if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(bind, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            return address;
        throw new InvalidOperationException("Incoming IP must be 0.0.0.0 or an IP address of this laptop.");
    }

    private static string ExplainBind(SocketException ex) =>
        ex.SocketErrorCode == SocketError.AddressAlreadyInUse
            ? "That port is already in use on this laptop."
            : ex.Message;

    private static async Task WaitQuiet(Task? task)
    {
        if (task == null)
            return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The loop is stuck on a socket that is already closed.
        }
    }

    private sealed class PeerConn
    {
        public PeerConn(TcpClient client)
        {
            Client = client;
            Stream = client.GetStream();
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public Task ReadTask { get; set; } = Task.CompletedTask;
    }
}
