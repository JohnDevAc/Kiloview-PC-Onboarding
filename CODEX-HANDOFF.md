# NDI Configurator PC Agent developer handoff

## Internal onboarding failure diagnostics — 7 September 2026

Prepared for Main 0.7.2 and Development 0.7.2-dev.1. Setup/Agent capture bounded stage timelines and exception details, queue remote failures across restart/reconnection, and require a matching server acknowledgement before deleting queued reports. The queue retains at most 64 reports for seven days and backs off failed delivery without blocking outcome reconciliation. Local server failures return optional `failureReport` in the existing schema-1 response. Remote Setup now returns a failing exit code for failure, and Agent captures UAC/launch and abnormal exits without adding a second Setup result dialog. Diagnostics are for internal testing; no log viewer or report links are added to the UI. See the suite's `ONBOARDING-DIAGNOSTICS.md` for storage, retention, retrieval and validation. Both companion validation projects and the server integration suites passed using isolated paths before release preparation. Publish this repository before the matching Job Configurator 0.8.10 packages.

## Further QA corrections — 6 September 2026

The initial QA changes were committed at `7a22b74`. The subsequent corrections and test evidence are recorded in the suite's QA-FOLLOWUP-2026-09-06.md. Read the current INTEROPERABILITY.md additions for retry fairness, strict persisted identity, deployment evidence and mutation authorization. Earlier implementation reports remain historical checkpoints.

## QA follow-up — 6 September 2026

The prior interoperability implementation was committed as `19a70fc5a10696d68140b12538181ed200c6632d` before QA corrections. The current follow-up implements the latest QA report; see INTEROPERABILITY.md and the suite's QA-FIX-IMPLEMENTATION-2026-09-06.md for the durable outcome protocol, package recovery, deployment boundaries and validation. Historical “uncommitted” and “no commit” notes below describe the earlier checkpoint. No application was installed or release published during this follow-up.

## Interoperability implementation — 6 September 2026

Uncommitted changes add `onboarding-attempt-v1`, local network/NDI recovery, original desktop-user ownership, DHCP reconciliation, bounded download readiness and complete-pair installation recovery. See [INTEROPERABILITY.md](INTEROPERABILITY.md); the owning suite's INTEROP-IMPLEMENTATION.md records cross-repository evidence. Both validation projects and self-contained package build pass with isolated state. Actual alternate-account UAC, network changes and reboot require controlled deployment acceptance. No installation, commit or release was performed; baseline notes below refer to committed releases.


## Aligned release baseline: 6 September 2026

Main `0.7.0` and Development `0.7.0-dev.2` share the server-local onboarding and
blue companion icon implementation. The branches differ only in version/channel
metadata. This project is managed together with the sibling server
workspace, while keeping separate Git repositories and deployments. Read
`AGENTS.md` and `SERVER-LOCAL-ONBOARDING-HANDOVER.md`.

The server can install this complete package as a default-selected optional
component and invoke installed Setup via `--server-command` with schema-1
stdin/stdout JSON. Local server onboarding inherits existing elevation and
installer consent, so it displays no second prompt. Remote Yes/No and UAC stay
unchanged. The companion owns all Windows NDI writes, replaces prior managed job
groups, and exposes live NDI configuration status for server drift monitoring.
Setup and the agent now use the royal-blue `assets/PcAgent.ico`.

Both validation projects passed (11 onboarding and 20 agent checks). The suite's
package/test and deployment evidence is recorded in the server's
`SUITE-INTEGRATION-VALIDATION.md`. The server release must bundle this version
from a clean committed checkout on the matching channel: server Main `0.8.8`
bundles companion Main `0.7.0`, while server Development `0.8.8-dev.1` bundles
companion Development `0.7.0-dev.2`. Installed agents follow the independent Main
release feed. Final release hashes and source revisions are recorded in the
server's `CHANNEL-ALIGNMENT-2026-09-06.md`.

The workflow below still describes remote PCs; server-local installation and
onboarding use the new process contract.

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

Version `0.6.1` advertises `multicast-config-v1`. An authorized
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

Remote onboarding accepts the current server product identity
`NDI Job Configurator` and the legacy `Kiloview Job Configurator` identity during
the migration window. All user-visible agent and server labels use the current
names.

The interactive NDI Discovery settings process still blocks onboarding and
multicast changes, but the always-on `NDI Discovery Service` background process
does not. The multicast preflight also excludes that service from its loaded-DLL
fallback scan. This avoids false preflight failures on normal NDI Tools 6.3
installations without weakening the Access Manager or Studio Monitor guards.

The tray menu now provides **Check for updates**. It consumes the public latest
production GitHub Release deployed from `main`, verifies the GitHub asset digest,
adjacent SHA-256 manifest, archive safety, product identity, and binary version,
then launches the staged Setup executable with UAC. See
`ONLINE-UPDATE-DEPLOYMENT-HANDOVER.md`. The main-branch release workflow is
`.github/workflows/release-main.yml`; `.github/workflows/release-dev.yml`
publishes development prereleases that installed agents ignore.

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

Package outputs are under `artifacts`; each ZIP has an adjacent `.sha256` manifest. Historical package hashes are not reused for this version.

## Safety invariants

- The agent runs unelevated. Privileged onboarding belongs to Setup; membership-authorized multicast is applied by the agent.
- Installed server-local onboarding uses existing elevation and installer consent.
- Every remote onboarding attempt requires a visible local Yes/No decision and
  Windows UAC.
- Submitted server addresses are replaced by the actual TCP source.
- Static target IP and gateway must remain in the requesting server's subnet.
- Firewall rules remain bound to the selected local address and subnet on all
  profiles; unrelated rules are untouched.
- No Windows service or scheduled task is created.
- Remote configuration is declarative and contains no executable content or
  credentials.
