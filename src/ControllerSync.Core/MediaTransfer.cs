using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControllerSync.Core;

public sealed record MediaHeader(
    string FileName,
    long TotalBytes,
    string Sha256,
    int Layer,
    int Clip,
    bool Play,
    string Channel);

public sealed record MediaReceipt(bool Ok, string SavedPath, string Detail);

public readonly record struct MediaTarget(string Host, int Port);

public static class MediaNames
{
    public const long MaxBytes = 8L * 1024 * 1024 * 1024;

    public static string SafeFileName(string? name)
    {
        var text = (name ?? "").Replace('\\', '/');
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
            text = text[(slash + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned is "" or "." or "..")
            return "clip";
        return cleaned;
    }

    public static IReadOnlyList<MediaTarget> Targets(string? remoteHost, int remoteSyncPort, string? extraLines)
    {
        var list = new List<MediaTarget>();
        Add(list, remoteHost, remoteSyncPort);
        if (string.IsNullOrWhiteSpace(extraLines))
            return list;

        foreach (var raw in extraLines.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var colon = line.LastIndexOf(':');
            if (colon > 0
                && !line.Contains("://", StringComparison.Ordinal)
                && !line[..colon].Contains(':')
                && int.TryParse(line[(colon + 1)..], out var port)
                && port is >= 1 and <= 65534)
            {
                Add(list, line[..colon], port);
                continue;
            }

            Add(list, line, remoteSyncPort);
        }

        return list;
    }

    private static void Add(List<MediaTarget> list, string? host, int syncPort)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0 || syncPort is < 1 or > 65534)
            return;
        var target = new MediaTarget(host, syncPort + 1);
        if (list.Any(item => item.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && item.Port == target.Port))
            return;
        list.Add(target);
    }
}

public static class ResolumeAddress
{
    public static string FileUri(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath).Replace('\\', '/');
        var escaped = Uri.EscapeDataString(full).Replace("%2F", "/", StringComparison.Ordinal).Replace("%3A", ":", StringComparison.Ordinal);
        return "file:///" + escaped;
    }

    public static string OpenUrl(int port, int layer, int clip) =>
        $"http://127.0.0.1:{port}/api/v1/composition/layers/{layer}/clips/{clip}/open";

    public static string ConnectUrl(int port, int layer, int clip) =>
        $"http://127.0.0.1:{port}/api/v1/composition/layers/{layer}/clips/{clip}/connect";
}

public sealed class MediaServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _directory;
    private readonly string _channel;
    private readonly Func<MediaHeader, string, CancellationToken, Task<string>>? _afterSave;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private int _disposed;

    public MediaServer(string directory, string channel, Func<MediaHeader, string, CancellationToken, Task<string>>? afterSave = null)
    {
        _directory = directory;
        _channel = channel;
        _afterSave = afterSave;
    }

    public int BoundPort { get; private set; }

    public Task StartAsync(string bindAddress, int port, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var bind = bindAddress is null or "" or "0.0.0.0"
            ? IPAddress.Any
            : bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback
                : IPAddress.Parse(bindAddress);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(bind, port);
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
                port == 0
                    ? "Could not listen for Resolume content."
                    : $"Could not listen for Resolume content on port {port}. {ex.Message}",
                ex);
        }

        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var token = _cts.Token;
        _accept = Task.Run(() => AcceptLoop(token), token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Listener is already closed.
        }

        if (_accept != null)
        {
            try
            {
                await _accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // The accept loop ends when the listener closes.
            }
        }

        _cts?.Dispose();
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
            catch
            {
                break;
            }

            _ = Task.Run(() => Receive(client, cancellationToken), cancellationToken);
        }
    }

    private async Task Receive(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string? temp = null;
            try
            {
                var headerJson = await MediaFrames.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                var header = JsonSerializer.Deserialize<MediaHeader>(headerJson, Json);
                if (header == null || !ChannelGuard.Matches(_channel, header.Channel))
                {
                    await WriteReceipt(stream, new MediaReceipt(false, "", "Channel does not match."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (header.Layer < 1 || header.Clip < 1)
                {
                    await WriteReceipt(stream, new MediaReceipt(false, "", "Layer and clip start at 1."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (header.TotalBytes is <= 0 or > MediaNames.MaxBytes)
                {
                    await WriteReceipt(stream, new MediaReceipt(false, "", "That file is empty or larger than 8 GB."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                temp = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var remaining = header.TotalBytes;
                    var buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            throw new EndOfStreamException("The upload stopped before the file finished.");
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hasher.AppendData(buffer.AsSpan(0, read));
                        remaining -= read;
                    }
                }

                var hash = Convert.ToHexString(hasher.GetHashAndReset());
                if (!hash.Equals(header.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteReceipt(stream, new MediaReceipt(false, "", "The file arrived damaged."), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var destination = Path.Combine(_directory, MediaNames.SafeFileName(header.FileName));
                File.Move(temp, destination, overwrite: true);
                temp = null;
                var detail = "Saved " + destination + ".";
                if (_afterSave != null)
                    detail = await _afterSave(header, destination, cancellationToken).ConfigureAwait(false);
                await WriteReceipt(stream, new MediaReceipt(true, destination, detail), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await WriteReceipt(stream, new MediaReceipt(false, "", ex.Message), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The sender has already gone.
                }
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch
                    {
                        // A partial file can be removed on the next send.
                    }
                }
            }
        }
    }

    private static Task WriteReceipt(NetworkStream stream, MediaReceipt receipt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        return MediaFrames.WriteAsync(stream, json, cancellationToken);
    }
}

public static class MediaClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<MediaReceipt> SendFileAsync(
        string host,
        int port,
        string channel,
        string filePath,
        int layer,
        int clip,
        bool play,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MediaNames.MaxBytes)
            return new MediaReceipt(false, "", "That file is empty or larger than 8 GB.");

        string hash;
        using (var hasher = SHA256.Create())
        {
            var computed = await hasher.ComputeHashAsync(file, cancellationToken).ConfigureAwait(false);
            hash = Convert.ToHexString(computed);
        }

        file.Position = 0;
        return await SendStreamAsync(host, port, channel, Path.GetFileName(filePath), file, hash, layer, clip, play, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<MediaReceipt> SendBytesAsync(
        string host,
        int port,
        string channel,
        string fileName,
        byte[] bytes,
        int layer,
        int clip,
        bool play,
        string? shaOverride = null,
        CancellationToken cancellationToken = default)
    {
        var hash = shaOverride ?? Convert.ToHexString(SHA256.HashData(bytes));
        return SendStreamAsync(host, port, channel, fileName, new MemoryStream(bytes), hash, layer, clip, play, null, cancellationToken);
    }

    private static async Task<MediaReceipt> SendStreamAsync(
        string host,
        int port,
        string channel,
        string fileName,
        Stream source,
        string sha256,
        int layer,
        int clip,
        bool play,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        await client.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
        client.NoDelay = true;
        var stream = client.GetStream();
        var length = source.Length - source.Position;
        var header = new MediaHeader(fileName, length, sha256, layer, clip, play, channel);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        await MediaFrames.WriteAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[65536];
        long sent = 0;
        while (sent < length)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;
            if (length > 0)
                progress?.Report(sent / (double)length);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = await MediaFrames.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MediaReceipt>(response, Json)
            ?? new MediaReceipt(false, "", "The other laptop did not answer.");
    }
}

internal static class MediaFrames
{
    public const int MaxControlBytes = 1024 * 1024;

    public static async Task WriteAsync(NetworkStream stream, byte[] body, CancellationToken cancellationToken)
    {
        var frame = new byte[4 + body.Length];
        Endian.WriteInt32(frame, body.Length);
        body.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExact(stream, header, cancellationToken).ConfigureAwait(false);
        var length = Endian.ReadInt32(header);
        if (length is <= 0 or > MaxControlBytes)
            throw new InvalidDataException("Content message was not valid.");
        var body = new byte[length];
        await ReadExact(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static async Task ReadExact(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The other laptop closed the content link.");
            offset += read;
        }
    }
}
