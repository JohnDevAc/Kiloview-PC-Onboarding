# PC Agent handover: sequential `/24` multicast allocation upgrade

## Purpose

Update NDI Configurator PC Agent so it accepts and applies the Job Configurator's
new per-sender `/24` multicast allocations. This document is a delta to
`SERVER-MULTICAST-CONFIGURATION-HANDOVER.md` and supersedes that document's
requirement for an exact `/28` mask.

The server-side implementation is installed and live-tested as Kiloview Job
Configurator `0.8.0-dev.66`. The current companion build `0.4.0-dev.2` rejects
the new request with:

```text
HTTP 400: Multicast mode requires netmask 255.255.255.240 (/28).
```

The agent must be updated before a remote Windows endpoint can use its new
sender allocation. Multicast receiving is already functional on the test
laptop and must not regress.

## Required allocation contract

Every sender now receives a separate aligned `/24`. Within a job `/16`, ranges
advance by third octet:

```text
239.x.1.0/24
239.x.2.0/24
239.x.3.0/24
...
239.x.255.0/24
```

Example fleet allocation from the live test:

```text
N60 encoder             239.193.1.0/24
TeleTool encoder        239.193.2.0/24
Remote Windows endpoint 239.193.3.0/24
Local Windows endpoint  239.193.4.0/24
```

The job `/16` remains inside the organization-local `239.192.0.0/14` scope.
The Configurator owns fleet-wide uniqueness. The agent validates only its own
assigned block and local state.

## Endpoint and capability compatibility

Keep the existing endpoint and request schema:

```text
PUT /api/v1/multicast/configuration
```

Keep advertising:

```json
"multicast-config-v1"
```

This is a validation expansion, not a request-shape change. Continue requiring
`schemaVersion: 1`. Bump the agent product version to the next appropriate
development version, recommended `0.4.0-dev.3` or later, so the server and UI
can identify the corrected build.

Do not advertise a new capability unless the endpoint or response schema is
changed. If a later capability is introduced, the server must retain an
explicit compatibility path for `multicast-config-v1`.

## Validation change

Replace the old exact `/28` requirement:

```text
netmask == 255.255.255.240
prefix aligned to 16 addresses
```

with the new exact `/24` requirement:

```text
netmask == 255.255.255.0
prefix aligned to 256 addresses
```

For multicast apply, require all of the following:

- `mode` is exactly `multicast`;
- `sendEnabled` and `receiveEnabled` are both `true`;
- `netPrefix` is a valid IPv4 address inside `239.192.0.0/14`;
- `netmask` is exactly `255.255.255.0`;
- the final octet of `netPrefix` is zero;
- the entire `/24` remains inside `239.192.0.0/14`;
- TTL is from 1 through 255;
- endpoint, adapter, membership, source subnet, server address and job pass all
  existing security checks.

Reject the legacy `/28` mask for a newly requested configuration. Existing
on-disk `/28` state may be read and reported accurately, but it does not satisfy
a `/24` request and must be replaced when the new request is approved.

For unicast revert, retain the existing rules: both booleans false and
prefix/mask/TTL absent or null.

## Example apply request

```json
{
  "schemaVersion": 1,
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "jobName": "GeorgesVIP",
  "adapterId": "{A9A139E-474D-40E1-83...}",
  "mode": "multicast",
  "sendEnabled": true,
  "receiveEnabled": true,
  "netPrefix": "239.193.3.0",
  "netmask": "255.255.255.0",
  "ttl": 1
}
```

## NDI configuration write

Apply and verify:

```json
{
  "ndi": {
    "multicast": {
      "send": {
        "enable": true,
        "netprefix": "239.193.3.0",
        "netmask": "255.255.255.0",
        "ttl": 1
      },
      "recv": {
        "enable": true
      }
    }
  }
}
```

Preserve the existing NDI groups, Discovery Server, preferred adapter, external
sources and all unrelated configuration. Continue using atomic same-directory
writes, a recoverable backup, serialized mutations, and post-write disk
readback.

`ndi.multicast.recv.subnets`, when present, contains permitted **sender network
subnets**, for example `192.168.0.0/24`. It must not be replaced with the
multicast allocation `239.193.3.0/24`. Preserve a valid existing value or derive
the selected production subnet using the selected adapter address and prefix.

The NDI configuration is loaded when an NDI application starts. Continue
returning HTTP 409 if Access Manager or an affected NDI client is open rather
than writing underneath a cached application. Do not terminate applications
automatically.

## Verified response and status

The response and `GET /api/v1/status` must return the values read from disk:

```json
{
  "schemaVersion": 1,
  "product": "NDI Configurator PC Agent",
  "endpointId": "b0ea92d2-2746-4da4-addb-5d510f01c5d3",
  "mode": "multicast",
  "adapterId": "{A9A139E-474D-40E1-83...}",
  "sendEnabled": true,
  "receiveEnabled": true,
  "netPrefix": "239.193.3.0",
  "netmask": "255.255.255.0",
  "ttl": 1,
  "jobName": "GeorgesVIP",
  "inUse": true,
  "observedUtc": "2026-08-13T22:45:00Z"
}
```

`inUse` means that the requested Access Manager state is enabled and matches;
it does not claim that media packets are currently flowing.

## Virtual-adapter diagnostic

The JOHN-PC end-to-end test found a Windows multicast receive failure caused by
an enabled VirtualBox Host-Only adapter. It had the same IPv4 interface metric
as the selected physical Ethernet adapter and installed an equal-cost
`224.0.0.0/4` route. The NDI sender and network were healthy, but Studio Monitor
opened only the TCP control connection and did not issue the required IGMP join.
Disabling the unused Host-Only adapter immediately restored 1080p59.94 multicast
reception.

Add a read-only diagnostic to status/preflight if it fits the existing agent
architecture:

- enumerate connected IPv4 interfaces other than the selected adapter;
- compare their interface metrics and `224.0.0.0/4` routes with the selected
  adapter;
- identify common virtual adapters such as VirtualBox Host-Only, VMware,
  Hyper-V/WSL/container host vNICs and VPN adapters;
- report an actionable warning when another connected adapter has an equal or
  lower multicast route metric.

Recommended optional status shape:

```json
"networkWarnings": [
  {
    "code": "ambiguous-multicast-route",
    "adapterName": "Ethernet 2",
    "adapterDescription": "VirtualBox Host-Only Ethernet Adapter",
    "message": "Another active adapter has an equal or preferred multicast route. Disable it or increase its interface metric before receiving NDI multicast."
  }
]
```

Do not automatically disable adapters, remove vSwitches, change interface
metrics or alter virtualization configuration. These can be production-critical
and require explicit local administrator choice. The selected adapter remains
the only address written to `ndi.adapters.allowed`.

## Security boundaries

Retain every boundary in the original multicast handover:

- exact stable endpoint ID and selected adapter ID;
- authorized membership, matching job and matching server source address;
- selected production subnet restriction;
- no trust in forwarded headers;
- bounded declarative JSON only;
- no arbitrary paths, commands, scripts, registry operations or file content;
- safe logs without credentials or unrelated NDI settings.

The expanded `/24` range must not weaken any authorization check.

## Required tests

1. A valid aligned `/24` request succeeds and is verified from disk.
2. The exact live-test request `239.193.3.0` with mask `255.255.255.0` succeeds.
3. `/28`, `/23`, non-contiguous masks and unaligned `/24` prefixes are rejected.
4. Prefixes outside `239.192.0.0/14` are rejected.
5. TTL 0 and 256 are rejected; 1 and 255 succeed.
6. Repeating the same request returns 200 without unnecessary rewrite.
7. Existing group, Discovery Server, allowed adapter and receive sender-subnet
   values survive apply and revert.
8. Status reports the verified `/24` values and later manual drift.
9. Revert disables multicast send and receive without deleting unrelated state.
10. Open NDI applications return 409 and remain running.
11. Wrong endpoint, adapter, membership, job or source address is rejected
    without mutation.
12. If the optional virtual-route diagnostic is implemented, an equal-metric
    VirtualBox Host-Only adapter produces a warning and no automatic mutation.

## Second-machine acceptance

On JL-LW-LAPTOP:

1. Close Access Manager and active NDI applications.
2. Update/install the corrected agent.
3. Confirm discovery reports the new agent version and
   `multicast-config-v1`.
4. From the Job Configurator, apply the laptop allocation
   `239.193.3.0/24`, TTL 1.
5. Confirm HTTP 200 and status readback with `inUse: true`.
6. Open Access Manager and confirm send range `239.193.3.0` with subnet mask
   `255.255.255.0`, receive enabled, preferred Wi-Fi adapter retained, and job
   group/Discovery Server unchanged.
7. Close Access Manager, open Studio Monitor and confirm the N60 source
   `GEORGESVIP-KV-002 (GeorgesVIP-183)` remains visible at 1080p59.94.
8. Revert to unicast, confirm status readback, and verify unrelated NDI settings
   remain unchanged.

The companion implementation is complete only when all mandatory tests pass
and the test-machine apply no longer returns the legacy `/28` validation error.

## Agent implementation status

Implemented in companion agent `0.4.0`. The automated validation suite
covers the mandatory `/24` request, mask/range/TTL boundaries, idempotency,
exact-assignment drift, receive sender-subnet preservation and derivation,
unicast revert, authorization, application preflight, and corrupt-file recovery.
The optional virtual-adapter route diagnostic is not part of this build; it
does not alter adapters, routes, metrics, or virtualization settings.
