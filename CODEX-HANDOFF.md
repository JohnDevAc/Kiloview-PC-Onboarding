# Kiloview PC Onboarding 0.3.0-dev.1 developer handoff

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
