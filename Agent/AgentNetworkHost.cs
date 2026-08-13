using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KiloviewPcAgent;

internal sealed class AgentNetworkHost : IDisposable
{
    public const int DiscoveryPort = 8093;
    public const int ApiPort = 8094;
    public const string DiscoveryQuery = "KILOVIEW_PC_AGENT_DISCOVER_V1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<AgentConfiguration> _configuration;
    private readonly Func<OnboardingLaunchRequest, Task<bool>> _confirmLaunch;
    private readonly int _discoveryPort;
    private readonly int _apiPort;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _listener;
    private UdpClient? _udp;

    public AgentNetworkHost(
        Func<AgentConfiguration> configuration,
        Func<OnboardingLaunchRequest, Task<bool>> confirmLaunch,
        int discoveryPort = DiscoveryPort,
        int apiPort = ApiPort)
    {
        _configuration = configuration;
        _confirmLaunch = confirmLaunch;
        _discoveryPort = discoveryPort;
        _apiPort = apiPort;
    }

    public void Start()
    {
        var state = _configuration();
        var address = IPAddress.Parse(state.Address);
        _listener = new TcpListener(address, _apiPort);
        _listener.Start(16);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        _ = AcceptLoopAsync(_stopping.Token);
        _ = DiscoveryLoopAsync(_stopping.Token);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Stop();
        _udp?.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var state = _configuration();
                if (!IsSameSubnet(result.RemoteEndPoint.Address, state)
                    || !string.Equals(
                        Encoding.UTF8.GetString(result.Buffer).Trim(),
                        DiscoveryQuery,
                        StringComparison.Ordinal))
                    continue;

                var response = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    product = "Kiloview PC Agent",
                    agentVersion = AgentMonitor.Version(),
                    endpointId = state.EndpointId,
                    hostname = Environment.MachineName,
                    address = state.Address,
                    prefixLength = state.PrefixLength,
                    apiPort = _apiPort,
                    status = "online",
                    membershipCount = state.Memberships.Count,
                    capabilities = new[]
                    {
                        "status-v1",
                        "memberships-v1",
                        "open-onboarding-v1",
                        "remote-onboarding-v2",
                        "network-config-v1"
                    }
                }, Json);
                await _udp.SendAsync(response, result.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var state = _configuration();
                if (remote is null || !IsSameSubnet(remote, state))
                {
                    await WriteJsonAsync(client, 403, new { error = "LAN access is restricted to the selected subnet." }, ct);
                    return;
                }

                var request = await ReadRequestAsync(client.GetStream(), ct);
                await RouteAsync(
                    client,
                    request.Method,
                    request.Path.Split('?', 2)[0],
                    request.Body,
                    remote,
                    ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
            {
                try { await WriteJsonAsync(client, 400, new { error = ex.Message }, ct); }
                catch { }
            }
        }
    }

    private static async Task<HttpRequestData> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        const int maximumHeaders = 8192;
        const int maximumBody = 8192;
        using var received = new MemoryStream(maximumHeaders + maximumBody);
        var buffer = new byte[4096];
        var headerEnd = -1;
        var contentLength = 0;
        var chunked = false;
        byte[]? decodedChunkedBody = null;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            received.Write(buffer, 0, read);
            if (received.Length > maximumHeaders + maximumBody + 4)
                throw new InvalidDataException("Request is too large.");

            var data = received.ToArray();
            if (headerEnd < 0)
            {
                headerEnd = HeaderEnd(data);
                if (headerEnd < 0)
                {
                    if (received.Length > maximumHeaders)
                        throw new InvalidDataException("Request headers are too large.");
                    continue;
                }

                var headers = Encoding.ASCII.GetString(data, 0, headerEnd);
                foreach (var line in headers.Split("\r\n").Skip(1))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && !int.TryParse(line[15..].Trim(), out contentLength))
                        throw new InvalidDataException("Content-Length is invalid.");
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                        && line[18..].Contains("chunked", StringComparison.OrdinalIgnoreCase))
                        chunked = true;
                }
                if (contentLength is < 0 or > maximumBody)
                    throw new InvalidDataException("Request body is too large.");
            }

            if (chunked)
            {
                var requestData = received.ToArray();
                if (TryDecodeChunkedBody(requestData, headerEnd + 4, maximumBody, out decodedChunkedBody))
                    break;
            }
            else if (received.Length >= headerEnd + 4 + contentLength)
                break;
        }

        if (headerEnd < 0)
            throw new InvalidDataException("Request headers are incomplete.");
        var requestBytes = received.ToArray();
        var headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
        var requestLine = headerText.Split("\r\n", 2)[0];
        var components = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 3 || components[0].Length > 16 || components[1].Length > 2048)
            throw new InvalidDataException("Request line is invalid.");
        var body = Encoding.UTF8.GetString(
            decodedChunkedBody ?? requestBytes,
            decodedChunkedBody is null ? headerEnd + 4 : 0,
            decodedChunkedBody?.Length ?? contentLength);
        return new(components[0], components[1], body);
    }

    private static bool TryDecodeChunkedBody(
        byte[] request,
        int bodyStart,
        int maximumBody,
        out byte[]? body)
    {
        body = null;
        using var decoded = new MemoryStream();
        var position = bodyStart;
        while (true)
        {
            var lineEnd = CrLf(request, position);
            if (lineEnd < 0)
                return false;
            var sizeText = Encoding.ASCII.GetString(request, position, lineEnd - position);
            var extension = sizeText.IndexOf(';');
            if (extension >= 0)
                sizeText = sizeText[..extension];
            if (!int.TryParse(
                sizeText.Trim(),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var size)
                || size < 0)
                throw new InvalidDataException("Chunk size is invalid.");
            position = lineEnd + 2;
            if (size == 0)
            {
                if (position + 2 > request.Length)
                    return false;
                if (request[position] != (byte)'\r' || request[position + 1] != (byte)'\n')
                    throw new InvalidDataException("Chunked request terminator is invalid.");
                body = decoded.ToArray();
                return true;
            }
            if (decoded.Length + size > maximumBody)
                throw new InvalidDataException("Request body is too large.");
            if (position + size + 2 > request.Length)
                return false;
            decoded.Write(request, position, size);
            position += size;
            if (request[position] != (byte)'\r' || request[position + 1] != (byte)'\n')
                throw new InvalidDataException("Chunk framing is invalid.");
            position += 2;
        }
    }

    private static int CrLf(byte[] data, int start)
    {
        for (var index = start; index < data.Length - 1; index++)
        {
            if (data[index] == (byte)'\r' && data[index + 1] == (byte)'\n')
                return index;
        }
        return -1;
    }

    private static int HeaderEnd(byte[] data)
    {
        for (var index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == (byte)'\r'
                && data[index + 1] == (byte)'\n'
                && data[index + 2] == (byte)'\r'
                && data[index + 3] == (byte)'\n')
                return index;
        }
        return -1;
    }

    private async Task RouteAsync(
        TcpClient client,
        string method,
        string path,
        string body,
        IPAddress remote,
        CancellationToken ct)
    {
        var state = _configuration();
        if (method == "GET" && path == "/api/health")
        {
            await WriteJsonAsync(client, 200, new
            {
                status = "ok",
                product = "Kiloview PC Agent",
                version = AgentMonitor.Version(),
                schemaVersion = 1
            }, ct);
            return;
        }
        if (method == "GET" && path == "/api/v1/status")
        {
            await WriteJsonAsync(client, 200, AgentMonitor.Snapshot(state, _startedUtc), ct);
            return;
        }
        if (method == "GET" && path == "/api/v1/memberships")
        {
            await WriteJsonAsync(client, 200, new
            {
                endpointId = state.EndpointId,
                memberships = state.Memberships
            }, ct);
            return;
        }
        if (method == "POST" && path == "/api/v1/onboarding/open")
        {
            var submitted = string.IsNullOrWhiteSpace(body)
                ? new OnboardingLaunchRequest(null, null, null, null, remote.ToString())
                : (JsonSerializer.Deserialize<OnboardingLaunchRequest>(body, Json)
                    ?? new OnboardingLaunchRequest(null, null, null, null, remote.ToString()));
            var configuratorUrl = $"http://{remote}:8091/";
            var payload = submitted with
            {
                ServerAddress = remote.ToString(),
                ConfiguratorUrl = configuratorUrl,
                RemoteAddress = remote.ToString()
            };
            var approved = await _confirmLaunch(payload);
            await WriteJsonAsync(
                client,
                approved ? 202 : 403,
                approved
                    ? new { status = "accepted", message = "Elevated remote onboarding is starting on the Windows PC." }
                    : new { status = "denied", message = "The Windows user did not approve onboarding." },
                ct);
            return;
        }
        await WriteJsonAsync(client, 404, new { error = "Endpoint not found." }, ct);
    }

    private static async Task WriteJsonAsync(
        TcpClient client,
        int statusCode,
        object value,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var reason = statusCode switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "Error"
        };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {payload.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static bool IsSameSubnet(IPAddress remote, AgentConfiguration configuration)
    {
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (remote.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.TryParse(configuration.Address, out var local))
            return false;
        var prefix = configuration.PrefixLength;
        if (prefix is < 1 or > 32)
            return false;
        var mask = prefix == 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);
        return (ToUInt(remote) & mask) == (ToUInt(local) & mask);
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private sealed record HttpRequestData(string Method, string Path, string Body);
}
