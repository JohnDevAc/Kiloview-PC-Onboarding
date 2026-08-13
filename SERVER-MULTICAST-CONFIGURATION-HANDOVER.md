# PC Agent handover: managed NDI Access Manager multicast

> **Compatibility note:** `AGENT-MULTICAST-24-UPGRADE-HANDOVER.md` supersedes
> this document's `/28` allocation and validation requirements. Agent
> `0.4.0` uses exact aligned `/24` sender allocations while retaining the
> same `multicast-config-v1` endpoint and schema.

## Objective

Add agent-side support for Kiloview Job Configurator to apply and revert NDI
Access Manager multicast settings on an already onboarded remote Windows PC.
The Configurator now reserves a unique multicast sender range for every Windows
endpoint. A compatible agent must apply that endpoint's range, enable multicast
send and receive, verify the saved state, and report it through the status API.

This is an idempotent management action for an existing agent membership. It is
not a replacement for remote onboarding and must not create a membership.

## Compatibility capability

Advertise this additional capability in UDP discovery:

```json
"multicast-config-v1"
```

The complete compatible capability list should include:

```json
[
  "status-v1",
  "memberships-v1",
  "open-onboarding-v1",
  "remote-onboarding-v2",
  "network-config-v1",
  "multicast-config-v1"
]
```

The Configurator uses the new endpoint only when `multicast-config-v1` is
advertised. Older agents retain the existing manual-range workflow.

## Configuration endpoint

Add this endpoint to the resident PC Agent on TCP 8094:

```text
PUT /api/v1/multicast/configuration
Content-Type: application/json
```

### Apply multicast

```json
{
  "schemaVersion": 1,
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "jobName": "Studio A",
  "adapterId": "{ADAPTER-GUID}",
  "mode": "multicast",
  "sendEnabled": true,
  "receiveEnabled": true,
  "netPrefix": "239.193.59.32",
  "netmask": "255.255.255.240",
  "ttl": 1
}
```

### Revert to unicast

```json
{
  "schemaVersion": 1,
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "jobName": "Studio A",
  "adapterId": "{ADAPTER-GUID}",
  "mode": "unicast",
  "sendEnabled": false,
  "receiveEnabled": false,
  "netPrefix": null,
  "netmask": null,
  "ttl": null
}
```

For `multicast`, require both booleans to be true, a valid organization-local
multicast prefix inside `239.192.0.0/14`, the exact contiguous netmask
`255.255.255.240`, an aligned `/28` prefix, and TTL from 1 through 255. Reject
subnet overlaps only if the agent has enough locally managed allocation data to
make that determination; the Configurator remains responsible for fleet-wide
allocation uniqueness.

For `unicast`, require both booleans to be false and require the prefix, netmask
and TTL to be absent or null.

## Successful response

After writing the NDI configuration, read it back from disk and return the
verified state. Do not return success from the requested values alone.

```json
{
  "schemaVersion": 1,
  "product": "Kiloview PC Agent",
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "mode": "multicast",
  "adapterId": "{ADAPTER-GUID}",
  "sendEnabled": true,
  "receiveEnabled": true,
  "netPrefix": "239.193.59.32",
  "netmask": "255.255.255.240",
  "ttl": 1,
  "jobName": "Studio A",
  "inUse": true,
  "observedUtc": "2026-08-13T17:30:00Z"
}
```

For a verified unicast response, return `mode: "unicast"`, both booleans false,
and null prefix/netmask/TTL. `inUse` means that the requested Access Manager
configuration is enabled and matches; it does not need to prove that multicast
packets are currently flowing.

## NDI Access Manager changes

Use the same NDI configuration location and JSON handling already used by the
onboarding utility. For multicast apply, change only:

```text
ndi.multicast.send.enable = true
ndi.multicast.send.netprefix = request.netPrefix
ndi.multicast.send.netmask = request.netmask
ndi.multicast.send.ttl = request.ttl
ndi.multicast.recv.enable = true
```

For unicast revert, set only:

```text
ndi.multicast.send.enable = false
ndi.multicast.recv.enable = false
```

Preserve all unrelated configuration, particularly:

- NDI send and receive groups;
- Discovery Server addresses;
- preferred/allowed adapter addresses;
- unrelated multicast or application fields not owned by this schema.

Write atomically through a same-directory temporary file, retain a recoverable
backup, parse the generated JSON before replacement, then reopen the installed
configuration and verify every managed field. Serialise mutations so onboarding
and multicast requests cannot write the file concurrently.

The resident agent runs unelevated and should perform this per-user Access
Manager update without UAC. Do not launch the onboarding utility and do not show
the old onboarding UI for this endpoint.

## Application preflight

Before mutation, detect NDI Access Manager and other NDI configuration clients
that cache and rewrite settings. Do not terminate them automatically. If a
relevant application is open, return HTTP 409 with safe JSON such as:

```json
{
  "error": "Close NDI Access Manager and active NDI client applications, then retry multicast setup."
}
```

The Configurator will show this error against the endpoint and allow a retry.

## Status API extension

When `multicast-config-v1` is advertised, extend
`GET /api/v1/status` with the verified current state:

```json
"multicastConfiguration": {
  "mode": "multicast",
  "adapterId": "{ADAPTER-GUID}",
  "sendEnabled": true,
  "receiveEnabled": true,
  "netPrefix": "239.193.59.32",
  "netmask": "255.255.255.240",
  "ttl": 1,
  "jobName": "Studio A",
  "inUse": true,
  "observedUtc": "2026-08-13T17:30:00Z"
}
```

Read this from current Access Manager state rather than a cached last request.
The Configurator polls it and marks the endpoint drifted if multicast is
disabled or the prefix, mask, TTL, adapter or job no longer matches.

Persist the associated `jobName` in agent-owned state only after the Access
Manager write has been verified. Clear that association after a successful
unicast revert. If no agent-managed multicast state exists, report a verified
unicast object rather than omitting the property.

## Security boundaries

- Require schema version 1 and the agent's exact stable `endpointId`.
- Require `adapterId` to match the currently selected agent adapter exactly.
- Accept the request only from the selected production subnet.
- Require the TCP source address to match an existing membership's
  `serverAddress`, and require `jobName` to match that membership.
- Do not trust forwarded headers or server-address fields from the JSON body.
- Accept only the declarative fields documented above. Never accept commands,
  executable paths, scripts, registry paths, or arbitrary JSON/file content.
- Limit the request body and reject unknown or duplicated security-sensitive
  fields if the current JSON policy supports it.
- Log endpoint ID, TCP source, job, mode, adapter, before/after multicast fields,
  result and elapsed time. Do not log credentials or unrelated NDI settings.

Recommended responses:

- `200`: settings were applied and verified; return the result schema above;
- `400`: invalid schema, mode, multicast range, mask, TTL or field combination;
- `403`: source subnet/membership/job is not authorized;
- `409`: adapter mismatch, NDI applications open, or settings could not be
  safely changed in the current state;
- `500`: unexpected local read/write failure, with a safe JSON error.

## Idempotency and recovery

Repeating an identical request must be safe. If current verified settings
already match, return HTTP 200 without rewriting the file. If verification after
write fails, restore the backup where safe and return a failure. A failed apply
must not alter membership or report a false multicast state.

## Acceptance checks

1. Discovery advertises `multicast-config-v1` only when the endpoint and status
   reporting are implemented.
2. A valid multicast request enables send/receive and applies the assigned
   prefix, `/28` mask and TTL while retaining group, Discovery Server and adapter
   configuration.
3. Repeating the request is idempotent.
4. A unicast request disables send/receive and preserves all unrelated fields.
5. `/api/v1/status` reports live verified values and exposes subsequent manual
   drift within the Configurator's next monitor pass.
6. Wrong source, endpoint ID, job or adapter is rejected without mutation.
7. Invalid/non-local multicast ranges, non-`/28` masks and invalid TTL values
   are rejected.
8. Open NDI applications return HTTP 409 and remain running.
9. A corrupt Access Manager file is preserved for recovery and is not silently
   replaced with an empty configuration.
10. Apply and revert work without UAC and without displaying onboarding UI.

The compatible stable agent implementation is packaged as `0.4.0`.
