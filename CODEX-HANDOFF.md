# NDI Configurator PC Agent 0.5.0 developer handoff

## Current workflow

The elevated WinForms utility is a bootstrap installer on an unconfigured PC. It
records EULA consent, selects the production adapter, reports NDI Tools state,
installs the unelevated tray agent, writes HKCU startup state, and creates the two
subnet-scoped firewall rules. NDI Tools is not an installation prerequisite.

After agent state exists, a normal utility launch is blocked with a remote-only
message. The tray no longer exposes local onboarding and double-click opens
read-only status.

Job Configurator discovers the agent on UDP 8093 and initiates onboarding through
`POST /api/v1/onboarding/open` on TCP 8094. The agent validates the TCP source,
shows a local warning/approval prompt, and launches the installed utility with
UAC and remote-only arguments.

The elevated background flow fetches
`GET /api/pc-onboarding/configuration/{endpointId}` from the requesting server on
TCP 8091, validates schema/endpoint/adapter/network fields, optionally applies
DHCP or static IPv4/gateway/DNS with `netsh`, writes NDI settings, refreshes agent
state and firewall scope, registers the endpoint, records membership, and shows a
single final result message.

Missing/outdated NDI Tools does not block onboarding. The final message reports
NDI state and directs the user to `https://ndi.video/tools/` when installation,
update, or manual currency verification is needed.

## Contracts and acceptance

- `SERVER-REMOTE-ONBOARDING-HANDOVER.md`: complete server contract and rollout
  checklist.
- `TEST-MACHINE-HANDOVER.md`: Windows acceptance and evidence plan.
- `README.md`: operator-facing package behavior.

Agent discovery advertises `remote-onboarding-v2` and `network-config-v1`.
Status includes DHCP state, IPv4 gateways, and IPv4 DNS servers for the selected
adapter. The existing registration and deletion endpoints remain unchanged.

Version `0.5.0` advertises `multicast-config-v1`. An authorized
existing membership can apply or revert its NDI Access Manager multicast state
through `PUT /api/v1/multicast/configuration` without UAC. The agent validates
the endpoint, adapter, source membership, job, organization-local aligned `/24`
range, mask, and TTL; writes atomically; preserves unrelated fields; reports
live status and drift; and records a safe audit entry. See
`SERVER-MULTICAST-CONFIGURATION-HANDOVER.md`. The exact assigned prefix, mask,
TTL, and enable flags are persisted so a different valid `/24` is still reported
as drift. Valid NDI receive sender-subnet entries are preserved; a missing or
invalid entry is derived from the selected production adapter.

The agent also prevents non-installed workspace/package copies from
holding the single-instance lock. Such launches redirect to the Program Files
agent, and remote onboarding resolves the installed utility as a fallback.

The rebrand migrates earlier Kiloview-branded state and endpoint identity to
`%LocalAppData%\NDI Configurator\PC Agent`, installs beneath
`%ProgramFiles%\NDI Configurator\PC Agent`, replaces the old HKCU startup value
and branded firewall rules, and advertises product `NDI Configurator PC Agent`.
The discovery query remains `KILOVIEW_PC_AGENT_DISCOVER_V1` for wire compatibility.

## Build and validation

```powershell
dotnet build .\Kiloview.PcOnboarding.csproj --configuration Release
dotnet run --project .\tests\Kiloview.PcOnboarding.Validation\Kiloview.PcOnboarding.Validation.csproj --configuration Release
dotnet run --project .\tests\Kiloview.PcAgent.Validation\Kiloview.PcAgent.Validation.csproj --configuration Release
.\scripts\Publish.ps1
.\scripts\Publish.ps1 -FrameworkDependent
```

The agent validation host uses ephemeral ports so it can run while an installed
tray agent owns production ports 8093/8094.

Current local packages:

- `artifacts/NDI-Configurator-PC-Agent-win-x64.zip` — 126.998 MB,
  SHA-256 `9172C8411277755F96BFD4AAEFE7F1AFB5AE59A355AFD32EA4E2FCB5D4A464A6`
- `artifacts/NDI-Configurator-PC-Agent-win-x64-framework-dependent.zip` —
  0.870 MB, SHA-256
  `E190671357731137A2CB28330D3F3DB2E05EE60314D0F6A0D4AE2CA22F817D60`

Each archive has an adjacent `.sha256` manifest.

## Safety invariants

- The agent never elevates itself and never applies configuration.
- Every remote onboarding attempt requires a visible local Yes/No decision and
  Windows UAC.
- Submitted server addresses are replaced by the actual TCP source.
- Static target IP and gateway must remain in the requesting server's subnet.
- Firewall rules remain bound to the selected local address and subnet on all
  profiles; unrelated rules are untouched.
- No Windows service or scheduled task is created.
- Remote configuration is declarative and contains no executable content or
  credentials.
