using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiloviewPcAgent;

internal sealed class AgentApiException(int statusCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

internal static class AgentMulticastService
{
    private const string Product = "NDI Configurator PC Agent";
    private const string MulticastMask = "255.255.255.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<string> RequestFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "endpointId",
        "jobName",
        "adapterId",
        "mode",
        "sendEnabled",
        "receiveEnabled",
        "netPrefix",
        "netmask",
        "ttl"
    };
    private static readonly SemaphoreSlim ProcessMutationGate = new(1, 1);
    private static readonly object AuditGate = new();

    private static string ConfigPath => Environment.GetEnvironmentVariable(
        "KILOVIEW_NDI_CONFIG_PATH") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NDI",
                "ndi-config.v1.json");
    private static string AuditPath => Environment.GetEnvironmentVariable(
        "KILOVIEW_AGENT_AUDIT_PATH") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NDI Configurator",
                "PC Agent",
                "audit.jsonl");

    public static MulticastConfigurationState Current(AgentConfiguration configuration)
    {
        var root = ReadConfiguration();
        return Snapshot(configuration, root);
    }

    public static MulticastConfigurationState Apply(
        AgentConfiguration configuration,
        IPAddress remote,
        string body)
    {
        var started = Stopwatch.StartNew();
        MulticastConfigurationRequest? request = null;
        MulticastConfigurationState? before = null;
        try
        {
            request = ParseRequest(body);
            Authorize(configuration, remote, request);
            ValidateRequest(request);
            EnsureNdiApplicationsClosed();

            ProcessMutationGate.Wait();
            try
            {
                using var configurationLock = AcquireConfigurationLock();
                var root = ReadConfiguration();
                before = Snapshot(configuration, root);
                var changed = !MatchesRequest(before, request)
                    || IsMulticastMode(request.Mode) && !HasValidReceiveSubnets(root);
                var originalText = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
                var previousAssociation = AgentStore.Read()?.Multicast;
                if (changed)
                    WriteAndVerify(configuration, root, request);
                try
                {
                    AgentStore.SetMulticastAssociation(IsMulticastMode(request.Mode)
                        ? new AgentMulticastAssociation(
                            request.JobName,
                            DateTimeOffset.UtcNow,
                            request.NetPrefix,
                            request.Netmask,
                            request.Ttl,
                            request.SendEnabled,
                            request.ReceiveEnabled)
                        : null);
                    var updatedConfiguration = AgentStore.Read() ?? configuration;
                    var verifiedRoot = ReadConfiguration();
                    var verified = Snapshot(updatedConfiguration, verifiedRoot);
                    if (!MatchesRequest(verified, request)
                        || IsMulticastMode(request.Mode) && !HasValidReceiveSubnets(verifiedRoot))
                        throw new AgentApiException(500, "NDI Access Manager did not retain the requested multicast settings.");
                    Audit(remote, request, before, verified, "success", null, started.Elapsed);
                    return verified;
                }
                catch
                {
                    if (changed)
                        RestoreConfiguration(originalText);
                    try { AgentStore.SetMulticastAssociation(previousAssociation); }
                    catch { }
                    throw;
                }
            }
            finally
            {
                ProcessMutationGate.Release();
            }
        }
        catch (AgentApiException ex)
        {
            Audit(remote, request, before, null, "failed", ex.Message, started.Elapsed);
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            const string message = "The local NDI multicast configuration could not be safely updated.";
            Audit(remote, request, before, null, "failed", message, started.Elapsed);
            throw new AgentApiException(500, message, ex);
        }
    }

    internal static MulticastConfigurationRequest ParseRequest(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new AgentApiException(400, "The multicast configuration body is required.");
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new AgentApiException(400, "The multicast configuration must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RequestFields.Contains(property.Name))
                    throw new AgentApiException(400, $"Unknown multicast configuration field '{property.Name}'.");
                if (!seen.Add(property.Name))
                    throw new AgentApiException(400, $"Duplicate multicast configuration field '{property.Name}'.");
            }
            foreach (var required in new[]
            {
                "schemaVersion", "endpointId", "jobName", "adapterId",
                "mode", "sendEnabled", "receiveEnabled"
            })
            {
                if (!seen.Contains(required))
                    throw new AgentApiException(400, $"Required multicast configuration field '{required}' is missing.");
            }
            return JsonSerializer.Deserialize<MulticastConfigurationRequest>(body, Json)
                ?? throw new AgentApiException(400, "The multicast configuration body is empty.");
        }
        catch (AgentApiException) { throw; }
        catch (JsonException ex)
        {
            throw new AgentApiException(400, "The multicast configuration JSON is invalid.", ex);
        }
    }

    internal static void ValidateRequest(MulticastConfigurationRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new AgentApiException(400, "Only multicast configuration schema version 1 is supported.");
        if (string.IsNullOrWhiteSpace(request.JobName)
            || request.JobName.Length > 128
            || request.JobName.Any(char.IsControl))
            throw new AgentApiException(400, "The multicast job name is invalid.");

        var mode = request.Mode;
        if (mode == "unicast")
        {
            if (request.SendEnabled || request.ReceiveEnabled
                || request.NetPrefix is not null
                || request.Netmask is not null
                || request.Ttl is not null)
                throw new AgentApiException(
                    400,
                    "Unicast mode requires send/receive disabled and prefix, mask, and TTL omitted or null.");
            return;
        }
        if (mode != "multicast")
            throw new AgentApiException(400, "Multicast mode must be 'multicast' or 'unicast'.");
        if (!request.SendEnabled || !request.ReceiveEnabled)
            throw new AgentApiException(400, "Multicast mode requires both send and receive enabled.");
        if (!string.Equals(request.Netmask, MulticastMask, StringComparison.Ordinal))
            throw new AgentApiException(400, "Multicast mode requires netmask 255.255.255.0 (/24).");
        if (request.Ttl is not int ttl || ttl is < 1 or > 255)
            throw new AgentApiException(400, "Multicast TTL must be from 1 through 255.");
        if (!IPAddress.TryParse(request.NetPrefix, out var prefix)
            || prefix.AddressFamily != AddressFamily.InterNetwork)
            throw new AgentApiException(400, "The multicast prefix is not valid IPv4.");
        var value = ToUInt(prefix);
        var organizationLocalStart = ToUInt(IPAddress.Parse("239.192.0.0"));
        var organizationLocalEnd = ToUInt(IPAddress.Parse("239.195.255.255"));
        if (value < organizationLocalStart
            || value > organizationLocalEnd
            || value + 255u > organizationLocalEnd)
            throw new AgentApiException(400, "The multicast prefix must be inside 239.192.0.0/14.");
        if ((value & 255u) != 0)
            throw new AgentApiException(400, "The multicast prefix must be aligned to a /24 range.");
    }

    private static void Authorize(
        AgentConfiguration configuration,
        IPAddress remote,
        MulticastConfigurationRequest request)
    {
        if (!Guid.TryParse(request.EndpointId, out var requestedEndpoint)
            || !Guid.TryParse(configuration.EndpointId, out var configuredEndpoint)
            || requestedEndpoint != configuredEndpoint)
            throw new AgentApiException(403, "The multicast request does not match this endpoint.");
        var membership = configuration.Memberships.FirstOrDefault(item =>
            string.Equals(item.ServerAddress, remote.ToString(), StringComparison.OrdinalIgnoreCase));
        if (membership is null
            || !string.Equals(membership.JobName, request.JobName?.Trim(), StringComparison.Ordinal))
            throw new AgentApiException(403, "The requesting Configurator is not authorized for this job membership.");
        if (!string.Equals(configuration.AdapterId, request.AdapterId, StringComparison.OrdinalIgnoreCase))
            throw new AgentApiException(409, "The multicast request targets a different network adapter.");
    }

    private static void EnsureNdiApplicationsClosed()
    {
        if (Environment.GetEnvironmentVariable("KILOVIEW_NDI_SKIP_PROCESS_CHECK") == "1")
            return;
        var processes = Process.GetProcesses();
        try
        {
            var running = processes.Any(process =>
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                        return false;
                    return process.ProcessName.Equals("Application.NdiGroupEditor", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("Access Manager", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("NDI Access Manager", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("Application.Network.StudioMonitor.x64", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("Application.Network.StudioMonitor.x86", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("NDI Studio Monitor", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("Application.NDI.DiscoveryService.UI", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("NDI Discovery Service", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
            if (!running)
            {
                running = processes.Any(process =>
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId)
                            return false;
                        return process.Modules.Cast<ProcessModule>().Any(module =>
                            module.ModuleName.Contains("Processing.NDI", StringComparison.OrdinalIgnoreCase)
                            || module.ModuleName.StartsWith("NDILib", StringComparison.OrdinalIgnoreCase));
                    }
                    catch
                    {
                        return false;
                    }
                });
            }
            if (running)
                throw new AgentApiException(
                    409,
                    "Close NDI Access Manager and active NDI client applications, then retry multicast setup.");
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static JsonObject ReadConfiguration()
    {
        if (!File.Exists(ConfigPath))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject
                ?? throw new JsonException("The root value is not an object.");
        }
        catch (JsonException ex)
        {
            throw new AgentApiException(
                409,
                "The NDI Access Manager configuration is invalid. Open Access Manager once to repair it, then retry.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentApiException(
                500,
                "The NDI Access Manager configuration could not be read.",
                ex);
        }
    }

    private static FileStream AcquireConfigurationLock()
    {
        var directory = Path.GetDirectoryName(ConfigPath)
            ?? throw new AgentApiException(500, "The NDI configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, ".ndi-configurator-configuration.lock");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex)
            {
                throw new AgentApiException(
                    409,
                    "The NDI configuration is currently being updated. Retry shortly.",
                    ex);
            }
        }
    }

    private static void WriteAndVerify(
        AgentConfiguration configuration,
        JsonObject root,
        MulticastConfigurationRequest request)
    {
        var ndi = OwnedObject(root, "ndi");
        var multicast = OwnedObject(ndi, "multicast");
        var send = OwnedObject(multicast, "send");
        var receive = OwnedObject(multicast, "recv");
        var multicastMode = IsMulticastMode(request.Mode);
        send["enable"] = multicastMode;
        receive["enable"] = multicastMode;
        if (multicastMode)
        {
            send["netprefix"] = request.NetPrefix;
            send["netmask"] = request.Netmask;
            send["ttl"] = request.Ttl;
            if (!HasValidReceiveSubnets(root))
                receive["subnets"] = new JsonArray(SelectedSubnet(configuration));
        }

        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The NDI configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var backup = path + ".ndi-configurator-pc-agent-backup";
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var existed = File.Exists(path);
        if (existed)
            File.Copy(path, backup, true);
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(Json) + Environment.NewLine);
            _ = JsonNode.Parse(File.ReadAllText(temporary))
                ?? throw new JsonException("The generated configuration is empty.");
            File.Move(temporary, path, true);
            var verifiedRoot = ReadConfiguration();
            var verified = Snapshot(configuration, verifiedRoot);
            if (!MatchesRequest(verified, request)
                || IsMulticastMode(request.Mode) && !HasValidReceiveSubnets(verifiedRoot))
                throw new AgentApiException(500, "NDI Access Manager did not retain the requested multicast settings.");
        }
        catch
        {
            try
            {
                if (existed && File.Exists(backup))
                    File.Copy(backup, path, true);
                else if (!existed && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void RestoreConfiguration(string? originalText)
    {
        if (originalText is null)
        {
            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);
            return;
        }
        var directory = Path.GetDirectoryName(ConfigPath)!;
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(ConfigPath)}.restore.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, originalText);
            _ = JsonNode.Parse(File.ReadAllText(temporary))
                ?? throw new JsonException("The recovery configuration is empty.");
            File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static MulticastConfigurationState Snapshot(
        AgentConfiguration configuration,
        JsonObject root)
    {
        var send = root["ndi"]?["multicast"]?["send"] as JsonObject;
        var receive = root["ndi"]?["multicast"]?["recv"] as JsonObject;
        var sendEnabled = Bool(send, "enable");
        var receiveEnabled = Bool(receive, "enable");
        var multicast = sendEnabled || receiveEnabled;
        var prefix = multicast ? Text(send, "netprefix") : null;
        var mask = multicast ? Text(send, "netmask") : null;
        var ttl = multicast ? Integer(send, "ttl") : null;
        var association = configuration.Multicast;
        var validManagedMulticast = sendEnabled
            && receiveEnabled
            && string.Equals(mask, MulticastMask, StringComparison.Ordinal)
            && ttl is >= 1 and <= 255
            && ValidManagedPrefix(prefix)
            && association is not null
            && association.SendEnabled == sendEnabled
            && association.ReceiveEnabled == receiveEnabled
            && string.Equals(association.NetPrefix, prefix, StringComparison.Ordinal)
            && string.Equals(association.Netmask, mask, StringComparison.Ordinal)
            && association.Ttl == ttl;
        return new(
            1,
            Product,
            configuration.EndpointId,
            multicast ? "multicast" : "unicast",
            configuration.AdapterId,
            sendEnabled,
            receiveEnabled,
            prefix,
            mask,
            ttl,
            configuration.Multicast?.JobName,
            multicast
                ? validManagedMulticast
                : configuration.Multicast is null,
            DateTimeOffset.UtcNow);
    }

    private static bool MatchesRequest(
        MulticastConfigurationState state,
        MulticastConfigurationRequest request)
    {
        var multicast = IsMulticastMode(request.Mode);
        return string.Equals(state.Mode, multicast ? "multicast" : "unicast", StringComparison.Ordinal)
            && state.SendEnabled == request.SendEnabled
            && state.ReceiveEnabled == request.ReceiveEnabled
            && (!multicast
                || string.Equals(state.NetPrefix, request.NetPrefix, StringComparison.Ordinal)
                && string.Equals(state.Netmask, request.Netmask, StringComparison.Ordinal)
                && state.Ttl == request.Ttl);
    }

    private static JsonObject OwnedObject(JsonObject parent, string name)
    {
        if (parent[name] is null)
        {
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }
        return parent[name] as JsonObject
            ?? throw new AgentApiException(
                409,
                $"The existing NDI '{name}' setting is not an object and cannot be safely changed.");
    }

    private static bool ValidManagedPrefix(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var numeric = ToUInt(address);
        return numeric >= ToUInt(IPAddress.Parse("239.192.0.0"))
            && numeric + 255u <= ToUInt(IPAddress.Parse("239.195.255.255"))
            && (numeric & 255u) == 0;
    }

    private static bool HasValidReceiveSubnets(JsonObject root)
    {
        if (root["ndi"]?["multicast"]?["recv"]?["subnets"] is not JsonArray subnets
            || subnets.Count == 0)
            return false;
        return subnets.All(item => item is JsonValue value
            && value.TryGetValue<string>(out var subnet)
            && ValidIpv4Subnet(subnet));
    }

    private static bool ValidIpv4Subnet(string value)
    {
        var parts = value.Split('/', 2);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefixLength)
            || prefixLength is < 0 or > 32)
            return false;
        var hostBits = 32 - prefixLength;
        var hostMask = hostBits == 32 ? uint.MaxValue : (1u << hostBits) - 1u;
        return (ToUInt(address) & hostMask) == 0;
    }

    private static string SelectedSubnet(AgentConfiguration configuration)
    {
        if (!IPAddress.TryParse(configuration.Address, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || configuration.PrefixLength is < 0 or > 32)
            throw new AgentApiException(409, "The selected production adapter subnet is invalid.");
        var hostBits = 32 - configuration.PrefixLength;
        var networkMask = hostBits == 32 ? 0u : uint.MaxValue << hostBits;
        var network = ToUInt(address) & networkMask;
        var bytes = BitConverter.GetBytes(network);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return $"{new IPAddress(bytes)}/{configuration.PrefixLength}";
    }

    private static bool IsMulticastMode(string? mode) => string.Equals(
        mode,
        "multicast",
        StringComparison.Ordinal);

    private static bool Bool(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value
        && (value.TryGetValue<bool>(out var result) && result
            || value.TryGetValue<string>(out var text) && bool.TryParse(text, out result) && result);

    private static string? Text(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static int? Integer(JsonObject? parent, string name) =>
        parent?[name] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static void Audit(
        IPAddress remote,
        MulticastConfigurationRequest? request,
        MulticastConfigurationState? before,
        MulticastConfigurationState? after,
        string result,
        string? error,
        TimeSpan elapsed)
    {
        try
        {
            var path = AuditPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry = JsonSerializer.Serialize(new
            {
                eventType = "multicast-configuration",
                endpointId = request?.EndpointId,
                sourceAddress = remote.ToString(),
                jobName = request?.JobName,
                mode = request?.Mode,
                adapterId = request?.AdapterId,
                before = before is null ? null : new
                {
                    before.Mode,
                    before.SendEnabled,
                    before.ReceiveEnabled,
                    before.NetPrefix,
                    before.Netmask,
                    before.Ttl
                },
                after = after is null ? null : new
                {
                    after.Mode,
                    after.SendEnabled,
                    after.ReceiveEnabled,
                    after.NetPrefix,
                    after.Netmask,
                    after.Ttl
                },
                result,
                error,
                elapsedMilliseconds = (long)elapsed.TotalMilliseconds,
                observedUtc = DateTimeOffset.UtcNow
            }, Json);
            lock (AuditGate)
                File.AppendAllText(path, entry + Environment.NewLine);
        }
        catch
        {
            // Auditing must not turn a verified configuration result into failure.
        }
    }
}
