# NDI Configurator PC Agent

Copyright © 2026 John Lightfoot. This is proprietary software made available
free of charge for non-commercial use only. Commercial use requires separate
written permission or a commercial licence. See [LICENSE.md](LICENSE.md).

This package installs a Windows tray agent and owns Windows NDI onboarding.
Remote PCs require local approval and UAC. When installed alongside NDI Job
Configurator on the server PC, the elevated server invokes the installed utility
directly without another confirmation. Both products remain independent Git
repositories, installations, and release feeds, managed by the suite workspace.

## Server installation and local onboarding

The server installer includes the complete PC Agent package as an optional
component, selected by default. Its license is included in the initial agreement.
The agent may be installed before a network adapter is selected; server onboarding
then activates it on the selected adapter. Newer independently updated agents
are retained, and uninstalling the server does not uninstall this component.

The installed utility's `--server-command` mode uses bounded stdin/stdout JSON.
It verifies the selected active adapter, preserves Windows IP settings, applies
and verifies NDI settings, and records agent membership. It uses the server's
existing administrator rights and installation consent. There is no additional
Yes/No, UAC, or result dialog. The remote network API gains no bypass.
See `SERVER-LOCAL-ONBOARDING-HANDOVER.md` and the server workspace's
`PC-ONBOARDING-CONTRACT.md` for the versioned contract.

Setup and the tray agent use `assets/PcAgent.ico`, with a royal-blue background
that distinguishes the companion from the server. See `assets/ICON.md`.

## Online updates

Right-click the tray icon and select **Check for updates**. The agent checks the
latest production GitHub Release deployed from `main`. When a newer version is
available, the user can download it and approve installation through Windows
UAC. The agent verifies the exact release asset name, source branch, package
size, GitHub SHA-256 digest, checksum manifest, archive paths, product identity,
and binary version before launching Setup.

The source repository and its production Releases must be public for installed
agents to check and download updates without embedded GitHub credentials.

## Bootstrap installation

Run `NDI Configurator PC Agent Setup.exe` from the complete package and approve UAC. The
bootstrap UI:

1. records EULA acceptance;
2. selects the production IPv4 adapter;
3. reports installed/current NDI Tools state without making it a prerequisite;
4. installs `NDI Configurator PC Agent` beneath `%ProgramFiles%\NDI Configurator\PC Agent`;
5. records the adapter beneath `%LocalAppData%\NDI Configurator\PC Agent`;
6. creates an HKCU startup entry; and
7. creates subnet-scoped inbound UDP 8093 and TCP 8094 firewall rules.

The interactive bootstrap UI cannot join or update a job; the installed server uses the separate local process command. Once agent state exists, launching
the utility normally displays a message directing the user to start onboarding
from Job Configurator.

Upgrading an earlier Kiloview-branded installation preserves its endpoint ID,
memberships, adapter selection, and EULA acceptance. Setup replaces the old
startup entry and branded firewall rules so a second agent is not created.

## Remote onboarding

The tray agent listens for the exact UDP discovery query
`KILOVIEW_PC_AGENT_DISCOVER_V1` on port 8093. Its read-only API on TCP 8094
provides health, monitoring, membership, and locally approved remote-onboarding
entry points. Both the Windows Firewall rules and the agent's own request checks
restrict access to the selected production subnet.

Job Configurator initiates onboarding with
`POST /api/v1/onboarding/open`. The PC displays a Yes/No prompt naming the TCP
source and warning that NDI and Windows adapter settings may change. Approval
returns HTTP 202, then launches the installed utility with UAC after a short
delay so the server can finish the POST before servicing the configuration GET.
Denial returns HTTP 403.

The elevated process has no main onboarding UI. It:

1. fetches `GET /api/pc-onboarding/configuration/{endpointId}` from the requesting
   Configurator on TCP 8091;
2. validates product, schema, stable endpoint identity, adapter identity, job,
   NDI discovery address, and all requested network fields;
3. optionally retains the current network, switches the selected adapter to
   DHCP, or applies static IPv4/prefix, gateway, and DNS settings;
4. applies the preferred NDI interface, send/receive group, and discovery server;
5. refreshes agent state and firewall scope;
6. registers the PC with the existing Configurator registration endpoint; and
7. shows one final success or failure message.

Static addressing is accepted only when the target PC address remains in the
requesting Configurator's subnet. A supplied gateway must be in that same subnet.
Remote settings are declarative; the protocol does not accept commands, scripts,
credentials, executable paths, or arbitrary file content.

NDI Tools is not a prerequisite for agent installation or remote registration.
The final message always reports NDI state. If Tools is missing, older than the
current official release, or currency cannot be confirmed, the user is directed
to `https://ndi.video/tools/`.

## Tray agent

The tray agent is a per-user application, not a Windows service or scheduled
task. Double-clicking the icon, or selecting **Status**, opens a compact
human-readable status window with computer, agent, network, job, NDI Tools, and
NDI transport information. Its menu lists
onboarded jobs, opens their Configurator pages, supports locally confirmed job
removal, and exits the current tray process. There is no local onboarding action.

Agent discovery advertises `remote-onboarding-v2` and `network-config-v1` in
addition to the existing status, membership, and open-request capabilities. An
onboarded agent also advertises `multicast-config-v1` and accepts an authorized
Configurator's idempotent `PUT /api/v1/multicast/configuration` request.
Status includes current DHCP state, default gateways, and DNS servers for the
selected adapter plus the live verified NDI multicast state. The `ndiConfiguration` object reports the preferred-interface match, send/receive groups, and discovery server for onboarding drift checks. Multicast changes
run unelevated, preserve unrelated Access Manager fields, and require the TCP
source and job to match an existing membership. Open NDI configuration clients
produce HTTP 409 instead of being terminated. Status contains no passwords,
tokens, or unrelated NDI configuration contents.

Managed sender allocations use exact, aligned `/24` blocks within the
organization-local `239.192.0.0/14` scope. The agent preserves valid NDI receive
sender-subnet entries or derives the selected production subnet when needed.

See `SERVER-REMOTE-ONBOARDING-HANDOVER.md` for the server contract and
`SERVER-MULTICAST-CONFIGURATION-HANDOVER.md` for managed multicast, and
`TEST-MACHINE-HANDOVER.md` for acceptance testing.

## Requirements

- Windows 10 or 11 x64;
- administrator approval for bootstrap and each approved remote onboarding;
- an active IPv4 production adapter;
- NDI Job Configurator reachable on TCP 8091 in the selected subnet; and
- NDI Access Manager, NDI Discovery, and NDI Studio Monitor closed while settings are written (the background Discovery Server can remain running).

Running NDI applications must be restarted after onboarding. If NDI Tools is
missing or outdated, install the latest release from the NDI website after the
remote process completes.

## Build

From the repository root:

```powershell
.\scripts\Publish.ps1
```

For the smaller package that requires the .NET 8 Desktop Runtime:

```powershell
.\scripts\Publish.ps1 -FrameworkDependent
```

Both packages include the bootstrap executable, agent payload, licence, local and remote server handovers, and test-machine handover. ZIP checksum manifests are generated
beside the archives.
