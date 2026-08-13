# Job Configurator handover: remote Windows PC onboarding

## Objective

Update Kiloview Job Configurator so an installed `Kiloview PC Agent` can be
onboarded only through a locally approved remote request. The agent continues to
run unelevated. A Windows user must accept a visible Yes/No prompt before the
agent launches the installed onboarding utility with UAC. The elevated utility
then fetches the complete desired configuration from the requesting Configurator,
applies it without showing the old onboarding UI, registers the PC, and displays
one final success or failure message locally.

The HTTP `202 Accepted` response from the agent means the user approved the
request and elevated processing was launched. It does not mean onboarding has
finished. Treat the subsequent registration update as completion.

## Agent discovery and compatibility

Continue sending the exact UTF-8 UDP query
`KILOVIEW_PC_AGENT_DISCOVER_V1` to port 8093. A compatible agent advertises:

```json
{
  "capabilities": [
    "status-v1",
    "memberships-v1",
    "open-onboarding-v1",
    "remote-onboarding-v2",
    "network-config-v1"
  ]
}
```

Only show the new remote-onboarding action when both `remote-onboarding-v2` and
`network-config-v1` are present. Do not invoke the legacy local UI for an older
agent; show **PC Agent update required** instead.

`GET http://PC_ADDRESS:8094/api/v1/status` now includes current selected-adapter
information suitable for pre-populating the server UI:

```json
{
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "address": "192.168.50.20",
  "prefixLength": 24,
  "adapterId": "{ADAPTER-GUID}",
  "adapterName": "Ethernet",
  "networkConfiguration": {
    "dhcpEnabled": false,
    "defaultGateways": ["192.168.50.1"],
    "dnsServers": ["192.168.50.2", "192.168.50.3"]
  }
}
```

Keep `adapterId` read-only in the server UI. It is the guard that prevents a
configuration prepared for one adapter being applied to another.

## Configuration endpoint to add

Add this endpoint to Job Configurator on TCP 8091:

```text
GET /api/pc-onboarding/configuration/{endpointId}
```

Register this API route before the single-page-app fallback. The current live
server returns `index.html` with HTTP 200 for this unknown path; that must not
happen for `/api/*`. Missing API resources must return a JSON 404 response so
the elevated client never mistakes the web UI shell for configuration data.

Return `Content-Type: application/json` and schema version 1:

```json
{
  "schemaVersion": 1,
  "product": "Kiloview Job Configurator",
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "jobName": "Studio A",
  "ndiDiscoveryServerIp": "192.168.50.11",
  "network": {
    "adapterId": "{ADAPTER-GUID}",
    "mode": "static",
    "address": "192.168.50.20",
    "prefixLength": 24,
    "defaultGateway": "192.168.50.1",
    "dnsServers": ["192.168.50.2", "192.168.50.3"]
  }
}
```

The `network` object is optional. Supported modes are:

- `unchanged`: retain IPv4, gateway, and DNS settings;
- `dhcp`: obtain IPv4, gateway, and DNS settings from DHCP;
- `static`: apply the supplied IPv4 address and prefix, optional gateway, and
  optional DNS list.

For `unchanged` and `dhcp`, omit `address`, `prefixLength`, `defaultGateway`, and
`dnsServers`. For `static`, `address` and `prefixLength` are required.
`defaultGateway` may be null. A null `dnsServers` property means retain the
current DNS configuration; an empty array clears static DNS servers.

The agent requires a static target address to remain in the requesting
Configurator's subnet. If supplied, the gateway must be inside that same subnet.
The PC validates these constraints again before changing Windows. Restrict the
server UI to prefix lengths 1 through 30 and reject subnet/broadcast addresses.

Recommended responses:

- `200`: configuration returned;
- `403`: request source is not allowed to fetch this endpoint's configuration;
- `404`: no pending configuration exists for this endpoint;
- `409`: the pending configuration is incomplete or no active job is available.

Store pending configuration by stable `endpointId`, not hostname or current IP.
Give it a short expiry such as ten minutes. Do not consume it on GET because the
elevated client may retry. Remove it after successful registration or expiry.

## Server initiation sequence

1. Discover the agent and read `/api/v1/status`.
2. Let the operator choose `unchanged`, `dhcp`, or `static` and validate all
   fields before staging the configuration above.
3. From the Configurator's production interface, send:

```http
POST http://PC_ADDRESS:8094/api/v1/onboarding/open
Content-Type: application/json

{
  "serverName": "CONFIGURATOR-HOSTNAME",
  "serverAddress": "CONFIGURATOR_IP",
  "jobName": "Studio A",
  "configuratorUrl": "http://CONFIGURATOR_IP:8091/"
}
```

4. The agent replaces `serverAddress` with the TCP source and reconstructs
   `configuratorUrl` as `http://TCP_SOURCE:8091/`. The local prompt
   warns that NDI and Windows network settings may change.
5. On local denial, expect `403 Forbidden`. On approval and successful UAC
   launch, expect `202 Accepted`.
6. The elevated utility fetches the staged configuration, applies network and
   NDI settings, refreshes agent state/firewall scope, and calls the existing
   `POST /api/pc-onboarding/register` endpoint. A changed static/DHCP address is
   reflected in that registration.
7. Mark the operation successful only when the endpoint record is created or
   updated by stable `endpointId`. Use a practical timeout such as 90 seconds and
   show pending/failed state if registration does not arrive.

The existing registration request remains:

```json
{
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "hostname": "WINDOWS-PC",
  "address": "192.168.50.20",
  "adapterName": "Ethernet",
  "prefixLength": 24,
  "preferredInterfaceConfigured": true,
  "ndiToolsVersion": "6.3.2.0",
  "utilityVersion": "0.3.0",
  "eulaVersion": "1.0",
  "operatingSystemVersion": "Microsoft Windows 10.0.26200"
}
```

When NDI Tools is missing, `ndiToolsVersion` is `not installed`. The PC still
stages the NDI configuration and completes registration, then displays a local
warning directing the user to `https://ndi.video/tools/`. It also warns when the
installed version is older than the current official release or currency could
not be confirmed.

## Security requirements

- Serve only declarative fields from the schema above. Never return commands,
  executable paths, scripts, credentials, or arbitrary file content.
- Bind pending settings to `endpointId` and the currently discovered PC/source
  address. Reject unrelated clients.
- Keep Configurator and agent traffic on the selected production subnet. The
  agent independently rejects off-subnet callers.
- Do not trust `serverAddress` or `configuratorUrl` submitted in the open request;
  the agent replaces both with the actual TCP source and fixed port 8091.
- Do not report completion from the `202` alone. Require the stable endpoint's
  registration update.
- Log who staged the network change, the before/after values, request time,
  approval result, and registration result. Do not log secrets or expose the
  endpoint ID outside operational diagnostics.

## Server acceptance checks

1. Compatible agents show the remote-onboarding action; older agents show update
   required.
2. `unchanged`, `dhcp`, and valid `static` payloads serialize exactly as schema 1.
3. Adapter-ID mismatch, invalid IP/prefix, off-subnet static IP, invalid gateway,
   and malformed DNS entries are rejected before the POST.
4. Local denial produces HTTP 403 and leaves the staged configuration available
   until expiry.
5. Local approval produces HTTP 202, followed by an endpoint registration update.
6. A static address change updates the device card to the new address and the
   agent becomes reachable on TCP 8094 at that address.
7. Missing/outdated NDI Tools does not prevent registration; the device card
   reports the state and the Windows PC displays the final NDI warning.
8. A missing/expired pending configuration produces a visible failure on the PC
   and no successful server status.
9. Repeating the operation is idempotent by `endpointId`.
