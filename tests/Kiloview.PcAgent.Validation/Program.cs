using KiloviewPcAgent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var adapter = NetworkInterface.GetAllNetworkInterfaces()
    .Where(item => item.OperationalStatus == OperationalStatus.Up
        && item.NetworkInterfaceType is not NetworkInterfaceType.Loopback
        && item.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
    .SelectMany(item => item.GetIPProperties().UnicastAddresses
        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address.Address))
        .Select(address => new
        {
            Id = item.Id,
            Name = item.Name,
            Address = address.Address,
            address.PrefixLength
        }))
    .First();
var configuration = new AgentConfiguration(
    1,
    Guid.NewGuid().ToString("D"),
    adapter.Id,
    adapter.Name,
    adapter.Address.ToString(),
    adapter.PrefixLength,
    DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow,
    []);

OnboardingLaunchRequest? launchRequest = null;
var discoveryPort = FreeUdpPort(adapter.Address);
var apiPort = FreeTcpPort(adapter.Address);
using var host = new AgentNetworkHost(
    () => configuration,
    request =>
    {
        launchRequest = request;
        return Task.FromResult(string.Equals(
            request.JobName,
            "Approved remote onboarding",
            StringComparison.Ordinal));
    },
    discoveryPort,
    apiPort);
host.Start();
await Task.Delay(150);

using var udp = new UdpClient(new IPEndPoint(adapter.Address, 0));
var query = Encoding.UTF8.GetBytes(AgentNetworkHost.DiscoveryQuery);
await udp.SendAsync(query, new IPEndPoint(adapter.Address, discoveryPort));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var discovery = await udp.ReceiveAsync(timeout.Token);
using var discoveryJson = JsonDocument.Parse(discovery.Buffer);
Require(
    discoveryJson.RootElement.GetProperty("endpointId").GetString() == configuration.EndpointId,
    "Discovery endpoint identity did not match.");
Require(
    discoveryJson.RootElement.GetProperty("apiPort").GetInt32() == apiPort,
    "Discovery API port did not match.");
var capabilities = discoveryJson.RootElement.GetProperty("capabilities")
    .EnumerateArray()
    .Select(item => item.GetString())
    .ToArray();
Require(capabilities.Contains("remote-onboarding-v2"), "Remote onboarding capability was not advertised.");
Require(capabilities.Contains("network-config-v1"), "Network configuration capability was not advertised.");

using var handler = new SocketsHttpHandler
{
    UseProxy = false,
    ConnectCallback = async (context, ct) =>
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(adapter.Address, 0));
        await socket.ConnectAsync(context.DnsEndPoint, ct);
        return new NetworkStream(socket, ownsSocket: true);
    }
};
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
using var health = await client.GetAsync(
    $"http://{adapter.Address}:{apiPort}/api/health");
health.EnsureSuccessStatusCode();
using var healthJson = JsonDocument.Parse(await health.Content.ReadAsByteArrayAsync());
Require(
    healthJson.RootElement.GetProperty("product").GetString() == "Kiloview PC Agent",
    "Health product identity did not match.");

using var status = await client.GetAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/status");
status.EnsureSuccessStatusCode();
using var statusJson = JsonDocument.Parse(await status.Content.ReadAsByteArrayAsync());
Require(
    statusJson.RootElement.GetProperty("endpointId").GetString() == configuration.EndpointId,
    "Status endpoint identity did not match.");
Require(
    statusJson.RootElement.TryGetProperty("operatingSystemVersion", out _),
    "Status did not include the Windows version.");
Require(
    statusJson.RootElement.TryGetProperty("ndiToolsInstalled", out _),
    "Status did not include NDI Tools state.");
Require(
    statusJson.RootElement.TryGetProperty("networkConfiguration", out var networkConfiguration)
    && networkConfiguration.TryGetProperty("defaultGateways", out _)
    && networkConfiguration.TryGetProperty("dnsServers", out _),
    "Status did not include manageable network configuration.");

using var memberships = await client.GetAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/memberships");
memberships.EnsureSuccessStatusCode();
using var launch = await client.PostAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/onboarding/open",
    new
    {
        serverName = "STÜDIO-SERVER",
        serverAddress = "203.0.113.10",
        jobName = "Stüdio",
        configuratorUrl = "http://203.0.113.10:8091/"
    });
Require(launch.StatusCode == HttpStatusCode.Forbidden, "Denied onboarding request did not return 403.");
Require(launchRequest?.ServerAddress == adapter.Address.ToString(), "The agent trusted a submitted server address.");
Require(launchRequest?.ConfiguratorUrl == $"http://{adapter.Address}:8091/", "The agent trusted a mismatched Configurator URL.");
Require(launchRequest?.JobName == "Stüdio", "The UTF-8 onboarding request was not decoded correctly.");
using var approvedLaunch = await client.PostAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/onboarding/open",
    new
    {
        serverName = "TEST-SERVER",
        serverAddress = "203.0.113.11",
        jobName = "Approved remote onboarding",
        configuratorUrl = $"http://{adapter.Address}:8091/"
    });
Require(
    approvedLaunch.StatusCode == HttpStatusCode.Accepted,
    "Approved remote onboarding request did not return 202.");

Console.WriteLine("AGENT_DISCOVERY=PASS");
Console.WriteLine("AGENT_HEALTH=PASS");
Console.WriteLine("AGENT_MONITORING=PASS");
Console.WriteLine("AGENT_MEMBERSHIPS=PASS");
Console.WriteLine("AGENT_ONBOARDING_APPROVAL_BOUNDARY=PASS");
Console.WriteLine("AGENT_REMOTE_ONBOARDING_ACCEPTED=PASS");
Console.WriteLine("AGENT_REMOTE_ONBOARDING_CAPABILITIES=PASS");
Console.WriteLine("AGENT_NETWORK_CONFIGURATION_STATUS=PASS");
Console.WriteLine($"AGENT_ADDRESS={adapter.Address}");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static int FreeTcpPort(IPAddress address)
{
    var listener = new TcpListener(address, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

static int FreeUdpPort(IPAddress address)
{
    using var client = new UdpClient(new IPEndPoint(address, 0));
    return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
}
