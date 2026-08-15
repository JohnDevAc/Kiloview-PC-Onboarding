using KiloviewPcAgent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    [new AgentMembership(
        adapter.Address.ToString(),
        $"http://{adapter.Address}:8091/",
        "Studio A",
        DateTimeOffset.UtcNow)]);
var testRoot = Path.Combine(Path.GetTempPath(), $"Kiloview-Agent-Multicast-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
var statePath = Path.Combine(testRoot, "agent-state.json");
var ndiPath = Path.Combine(testRoot, "ndi-config.v1.json");
var auditPath = Path.Combine(testRoot, "audit.jsonl");
Environment.SetEnvironmentVariable("KILOVIEW_AGENT_STATE_PATH", statePath);
Environment.SetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH", ndiPath);
Environment.SetEnvironmentVariable("KILOVIEW_AGENT_AUDIT_PATH", auditPath);
Environment.SetEnvironmentVariable("KILOVIEW_NDI_SKIP_PROCESS_CHECK", "1");
var testJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(configuration, testJson));
await File.WriteAllTextAsync(
    ndiPath,
    """
    {
      "unrelatedRoot": "retain-root",
      "ndi": {
        "adapters": { "allowed": ["192.0.2.20"] },
        "groups": { "send": "Studio A", "recv": "Studio A" },
        "networks": { "discovery": "192.0.2.10" },
        "multicast": {
          "unrelatedMulticast": "retain-multicast",
          "send": { "enable": false, "unrelatedSend": 42 },
          "recv": {
            "enable": false,
            "subnets": ["192.0.2.0/24"],
            "unrelatedReceive": true
          }
        }
      }
    }
    """);

OnboardingLaunchRequest? launchRequest = null;
var approvedLaunchStarted = new TaskCompletionSource<OnboardingLaunchRequest>(
    TaskCreationOptions.RunContinuationsAsynchronously);
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
    apiPort,
    request => approvedLaunchStarted.TrySetResult(request),
    TimeSpan.FromMilliseconds(250));
host.Start();
await Task.Delay(150);

using var udp = new UdpClient(new IPEndPoint(adapter.Address, 0));
var query = Encoding.UTF8.GetBytes(AgentNetworkHost.DiscoveryQuery);
await udp.SendAsync(query, new IPEndPoint(adapter.Address, discoveryPort));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var discovery = await udp.ReceiveAsync(timeout.Token);
using var discoveryJson = JsonDocument.Parse(discovery.Buffer);
Require(
    discoveryJson.RootElement.GetProperty("product").GetString() == "NDI Configurator PC Agent",
    "Discovery product identity did not match.");
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
Require(capabilities.Contains("multicast-config-v1"), "Multicast configuration capability was not advertised.");

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
    healthJson.RootElement.GetProperty("product").GetString() == "NDI Configurator PC Agent",
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
Require(
    statusJson.RootElement.TryGetProperty("multicastConfiguration", out var initialMulticast)
    && initialMulticast.GetProperty("mode").GetString() == "unicast"
    && initialMulticast.GetProperty("inUse").GetBoolean(),
    "Status did not include verified initial unicast configuration.");
var initialNdiText = await File.ReadAllTextAsync(ndiPath);
var legacyRoot = JsonNode.Parse(initialNdiText)!;
legacyRoot["ndi"]!["multicast"]!["send"]!["enable"] = true;
legacyRoot["ndi"]!["multicast"]!["send"]!["netprefix"] = "239.193.59.32";
legacyRoot["ndi"]!["multicast"]!["send"]!["netmask"] = "255.255.255.240";
legacyRoot["ndi"]!["multicast"]!["send"]!["ttl"] = 1;
legacyRoot["ndi"]!["multicast"]!["recv"]!["enable"] = true;
await File.WriteAllTextAsync(ndiPath, legacyRoot.ToJsonString(testJson));
using (var legacyStatus = await client.GetAsync($"http://{adapter.Address}:{apiPort}/api/v1/status"))
{
    legacyStatus.EnsureSuccessStatusCode();
    using var legacyStatusJson = JsonDocument.Parse(await legacyStatus.Content.ReadAsByteArrayAsync());
    var legacyMulticast = legacyStatusJson.RootElement.GetProperty("multicastConfiguration");
    Require(
        legacyMulticast.GetProperty("mode").GetString() == "multicast"
        && legacyMulticast.GetProperty("netPrefix").GetString() == "239.193.59.32"
        && legacyMulticast.GetProperty("netmask").GetString() == "255.255.255.240"
        && !legacyMulticast.GetProperty("inUse").GetBoolean(),
        "Legacy /28 state was not accurately reported as unmanaged drift.");
}
await File.WriteAllTextAsync(ndiPath, initialNdiText);

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
Require(
    !approvedLaunchStarted.Task.IsCompleted,
    "The elevated launch was scheduled before the HTTP 202 response completed.");
var deferredLaunch = await approvedLaunchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
Require(
    deferredLaunch.JobName == "Approved remote onboarding",
    "The deferred elevated launch did not retain its approved request.");

var multicastRequest = new
{
    schemaVersion = 1,
    endpointId = configuration.EndpointId,
    jobName = "Studio A",
    adapterId = configuration.AdapterId,
    mode = "multicast",
    sendEnabled = true,
    receiveEnabled = true,
    netPrefix = "239.193.3.0",
    netmask = "255.255.255.0",
    ttl = 1
};
using var wrongEndpoint = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { endpointId = Guid.NewGuid().ToString("D") });
Require(wrongEndpoint.StatusCode == HttpStatusCode.Forbidden, "Wrong multicast endpoint ID was not rejected.");
using var wrongJob = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { jobName = "Other Job" });
Require(wrongJob.StatusCode == HttpStatusCode.Forbidden, "Wrong multicast job was not rejected.");
using var wrongAdapter = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { adapterId = Guid.NewGuid().ToString("B") });
Require(wrongAdapter.StatusCode == HttpStatusCode.Conflict, "Wrong multicast adapter was not rejected.");
using var invalidRange = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { netPrefix = "239.196.0.0" });
Require(invalidRange.StatusCode == HttpStatusCode.BadRequest, "Non-organization-local multicast range was not rejected.");
using var unalignedRange = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { netPrefix = "239.193.3.1" });
Require(unalignedRange.StatusCode == HttpStatusCode.BadRequest, "Unaligned multicast /24 was not rejected.");
using var legacyMask = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { netmask = "255.255.255.240" });
Require(legacyMask.StatusCode == HttpStatusCode.BadRequest, "Legacy multicast /28 mask was not rejected.");
using var widerMask = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { netmask = "255.255.254.0" });
Require(widerMask.StatusCode == HttpStatusCode.BadRequest, "Multicast /23 mask was not rejected.");
using var nonContiguousMask = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { netmask = "255.0.255.0" });
Require(nonContiguousMask.StatusCode == HttpStatusCode.BadRequest, "Non-contiguous multicast mask was not rejected.");
using var ttlZero = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { ttl = 0 });
Require(ttlZero.StatusCode == HttpStatusCode.BadRequest, "Multicast TTL zero was not rejected.");
using var ttlTooHigh = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { ttl = 256 });
Require(ttlTooHigh.StatusCode == HttpStatusCode.BadRequest, "Multicast TTL 256 was not rejected.");
using var wrongModeCase = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { mode = "Multicast" });
Require(wrongModeCase.StatusCode == HttpStatusCode.BadRequest, "Non-exact multicast mode was not rejected.");
using var wrongContentType = await client.PutAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    new StringContent(JsonSerializer.Serialize(multicastRequest), Encoding.UTF8, "text/plain"));
Require(wrongContentType.StatusCode == HttpStatusCode.BadRequest, "Non-JSON multicast content type was not rejected.");
RequireApiStatus(
    () => AgentMulticastService.Apply(
        configuration,
        IPAddress.Parse("203.0.113.99"),
        JsonSerializer.Serialize(multicastRequest)),
    403,
    "A source without a matching membership was not rejected.");

using var multicastApply = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest);
multicastApply.EnsureSuccessStatusCode();
using var multicastApplyJson = JsonDocument.Parse(await multicastApply.Content.ReadAsByteArrayAsync());
Require(
    multicastApplyJson.RootElement.GetProperty("mode").GetString() == "multicast"
    && multicastApplyJson.RootElement.GetProperty("inUse").GetBoolean()
    && multicastApplyJson.RootElement.GetProperty("jobName").GetString() == "Studio A"
    && multicastApplyJson.RootElement.GetProperty("netPrefix").GetString() == "239.193.3.0"
    && multicastApplyJson.RootElement.GetProperty("netmask").GetString() == "255.255.255.0",
    "Verified multicast response did not match the applied request.");
var managedAssociation = AgentStore.Read()?.Multicast;
Require(
    managedAssociation?.JobName == "Studio A"
    && managedAssociation.NetPrefix == "239.193.3.0"
    && managedAssociation.Netmask == "255.255.255.0"
    && managedAssociation.Ttl == 1
    && managedAssociation.SendEnabled
    && managedAssociation.ReceiveEnabled,
    "The exact managed /24 assignment was not persisted for drift detection.");
var appliedWriteTime = File.GetLastWriteTimeUtc(ndiPath);
await Task.Delay(50);
using var repeatedApply = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest);
repeatedApply.EnsureSuccessStatusCode();
Require(
    File.GetLastWriteTimeUtc(ndiPath) == appliedWriteTime,
    "Idempotent multicast apply rewrote the NDI configuration.");
using (var ttlMaximum = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest with { ttl = 255 }))
{
    ttlMaximum.EnsureSuccessStatusCode();
    using var ttlMaximumJson = JsonDocument.Parse(await ttlMaximum.Content.ReadAsByteArrayAsync());
    Require(
        ttlMaximumJson.RootElement.GetProperty("ttl").GetInt32() == 255
        && ttlMaximumJson.RootElement.GetProperty("inUse").GetBoolean(),
        "Maximum valid multicast TTL was not applied and verified.");
}
using (var restoreTtl = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest))
    restoreTtl.EnsureSuccessStatusCode();

var appliedRoot = JsonNode.Parse(await File.ReadAllTextAsync(ndiPath))!;
Require(appliedRoot["unrelatedRoot"]?.GetValue<string>() == "retain-root", "Multicast apply changed an unrelated root field.");
Require(appliedRoot["ndi"]?["groups"]?["send"]?.GetValue<string>() == "Studio A", "Multicast apply changed NDI groups.");
Require(appliedRoot["ndi"]?["networks"]?["discovery"]?.GetValue<string>() == "192.0.2.10", "Multicast apply changed NDI discovery.");
Require(appliedRoot["ndi"]?["multicast"]?["send"]?["unrelatedSend"]?.GetValue<int>() == 42, "Multicast apply changed an unrelated send field.");
Require(
    appliedRoot["ndi"]?["multicast"]?["recv"]?["subnets"]?[0]?.GetValue<string>() == "192.0.2.0/24",
    "Multicast apply changed a valid receive sender-subnet value.");

using var multicastStatus = await client.GetAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/status");
multicastStatus.EnsureSuccessStatusCode();
using var multicastStatusJson = JsonDocument.Parse(await multicastStatus.Content.ReadAsByteArrayAsync());
var liveMulticast = multicastStatusJson.RootElement.GetProperty("multicastConfiguration");
Require(
    liveMulticast.GetProperty("mode").GetString() == "multicast"
    && liveMulticast.GetProperty("inUse").GetBoolean(),
    "Status did not report the live managed multicast configuration.");
var driftedRoot = JsonNode.Parse(await File.ReadAllTextAsync(ndiPath))!;
driftedRoot["ndi"]!["multicast"]!["send"]!["netprefix"] = "239.193.4.0";
await File.WriteAllTextAsync(ndiPath, driftedRoot.ToJsonString(testJson));
using var driftedStatus = await client.GetAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/status");
driftedStatus.EnsureSuccessStatusCode();
using var driftedStatusJson = JsonDocument.Parse(await driftedStatus.Content.ReadAsByteArrayAsync());
Require(
    !driftedStatusJson.RootElement
        .GetProperty("multicastConfiguration")
        .GetProperty("inUse")
        .GetBoolean(),
    "Status did not expose manual multicast drift.");
using var repairDrift = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest);
repairDrift.EnsureSuccessStatusCode();

var unicastRequest = new
{
    schemaVersion = 1,
    endpointId = configuration.EndpointId,
    jobName = "Studio A",
    adapterId = configuration.AdapterId,
    mode = "unicast",
    sendEnabled = false,
    receiveEnabled = false,
    netPrefix = (string?)null,
    netmask = (string?)null,
    ttl = (int?)null
};
using var unicastRevert = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    unicastRequest);
unicastRevert.EnsureSuccessStatusCode();
using var unicastJson = JsonDocument.Parse(await unicastRevert.Content.ReadAsByteArrayAsync());
Require(
    unicastJson.RootElement.GetProperty("mode").GetString() == "unicast"
    && unicastJson.RootElement.GetProperty("inUse").GetBoolean()
    && unicastJson.RootElement.GetProperty("jobName").ValueKind == JsonValueKind.Null,
    "Verified unicast response did not clear the managed association.");
var revertedRoot = JsonNode.Parse(await File.ReadAllTextAsync(ndiPath))!;
Require(!(revertedRoot["ndi"]?["multicast"]?["send"]?["enable"]?.GetValue<bool>() ?? true), "Unicast revert did not disable multicast send.");
Require(!(revertedRoot["ndi"]?["multicast"]?["recv"]?["enable"]?.GetValue<bool>() ?? true), "Unicast revert did not disable multicast receive.");
Require(
    revertedRoot["ndi"]?["multicast"]?["send"]?["netprefix"]?.GetValue<string>() == "239.193.3.0",
    "Unicast revert removed the retained /24 prefix field.");
Require(
    revertedRoot["ndi"]?["multicast"]?["recv"]?["subnets"]?[0]?.GetValue<string>() == "192.0.2.0/24",
    "Unicast revert changed the receive sender-subnet value.");

revertedRoot["ndi"]!["multicast"]!["recv"]!["subnets"] = new JsonArray();
await File.WriteAllTextAsync(ndiPath, revertedRoot.ToJsonString(testJson));
using (var derivedSubnetApply = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest))
    derivedSubnetApply.EnsureSuccessStatusCode();
var derivedSubnetRoot = JsonNode.Parse(await File.ReadAllTextAsync(ndiPath))!;
Require(
    derivedSubnetRoot["ndi"]?["multicast"]?["recv"]?["subnets"]?[0]?.GetValue<string>()
        == NetworkSubnet(adapter.Address, adapter.PrefixLength),
    "Multicast apply did not derive the selected production sender subnet.");
using (var derivedSubnetRevert = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    unicastRequest))
    derivedSubnetRevert.EnsureSuccessStatusCode();

var dummyStudioMonitorPath = Path.Combine(testRoot, "Application.Network.StudioMonitor.x64.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), dummyStudioMonitorPath);
using (var dummyStudioMonitor = Process.Start(new ProcessStartInfo(dummyStudioMonitorPath)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    ArgumentList = { "/d", "/c", "ping", "127.0.0.1", "-n", "4" }
}) ?? throw new InvalidOperationException("The Studio Monitor preflight test process could not start."))
{
    await Task.Delay(200);
    Environment.SetEnvironmentVariable("KILOVIEW_NDI_SKIP_PROCESS_CHECK", null);
    using var openApplicationApply = await client.PutAsJsonAsync(
        $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
        multicastRequest);
    Require(
        openApplicationApply.StatusCode == HttpStatusCode.Conflict,
        "An open NDI configuration client did not produce HTTP 409.");
    Require(!dummyStudioMonitor.HasExited, "The agent terminated Studio Monitor.");
    Environment.SetEnvironmentVariable("KILOVIEW_NDI_SKIP_PROCESS_CHECK", "1");
    await dummyStudioMonitor.WaitForExitAsync();
}

RequireApiStatus(
    () => AgentMulticastService.ParseRequest("{\"schemaVersion\":1,\"schemaVersion\":1}"),
    400,
    "Duplicate multicast fields were not rejected.");
var corrupt = "{ this is not valid JSON";
await File.WriteAllTextAsync(ndiPath, corrupt);
using var corruptApply = await client.PutAsJsonAsync(
    $"http://{adapter.Address}:{apiPort}/api/v1/multicast/configuration",
    multicastRequest);
Require(corruptApply.StatusCode == HttpStatusCode.Conflict, "Corrupt NDI configuration was not rejected with 409.");
Require(await File.ReadAllTextAsync(ndiPath) == corrupt, "Corrupt NDI configuration was not preserved for recovery.");
Require(File.Exists(auditPath), "Multicast management did not create an audit log.");
Require(
    (AgentStore.Read()?.Memberships.Count ?? 0) == 1,
    "Multicast management changed agent memberships.");

Console.WriteLine("AGENT_DISCOVERY=PASS");
Console.WriteLine("AGENT_HEALTH=PASS");
Console.WriteLine("AGENT_MONITORING=PASS");
Console.WriteLine("AGENT_MEMBERSHIPS=PASS");
Console.WriteLine("AGENT_ONBOARDING_APPROVAL_BOUNDARY=PASS");
Console.WriteLine("AGENT_REMOTE_ONBOARDING_ACCEPTED=PASS");
Console.WriteLine("AGENT_REMOTE_LAUNCH_DEFERRED=PASS");
Console.WriteLine("AGENT_REMOTE_ONBOARDING_CAPABILITIES=PASS");
Console.WriteLine("AGENT_NETWORK_CONFIGURATION_STATUS=PASS");
Console.WriteLine("AGENT_MULTICAST_CONFIGURATION=PASS");
Console.WriteLine("AGENT_MULTICAST_AUTHORIZATION=PASS");
Console.WriteLine("AGENT_MULTICAST_IDEMPOTENCY=PASS");
Console.WriteLine("AGENT_MULTICAST_LIVE_DRIFT=PASS");
Console.WriteLine("AGENT_MULTICAST_APPLICATION_PREFLIGHT=PASS");
Console.WriteLine("AGENT_MULTICAST_UNICAST_REVERT=PASS");
Console.WriteLine("AGENT_MULTICAST_CORRUPT_FILE_RECOVERY=PASS");
Console.WriteLine($"AGENT_ADDRESS={adapter.Address}");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RequireApiStatus(Action action, int expectedStatus, string message)
{
    try
    {
        action();
    }
    catch (AgentApiException ex) when (ex.StatusCode == expectedStatus)
    {
        return;
    }
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

static string NetworkSubnet(IPAddress address, int prefixLength)
{
    var bytes = address.GetAddressBytes();
    if (BitConverter.IsLittleEndian)
        Array.Reverse(bytes);
    var value = BitConverter.ToUInt32(bytes);
    var hostBits = 32 - prefixLength;
    var mask = hostBits == 32 ? 0u : uint.MaxValue << hostBits;
    var networkBytes = BitConverter.GetBytes(value & mask);
    if (BitConverter.IsLittleEndian)
        Array.Reverse(networkBytes);
    return $"{new IPAddress(networkBytes)}/{prefixLength}";
}
