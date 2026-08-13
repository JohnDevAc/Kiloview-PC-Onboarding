# Kiloview PC Onboarding: remote-only acceptance handover

## Scope

Validate the packaged bootstrap utility, tray agent, locally approved remote
onboarding, server-sourced NDI configuration, and optional server-sourced Windows
network configuration on a disposable Windows 10/11 x64 PC. Capture exact
commands, outputs, screenshots, Windows build, scaling, and failure text.

Use only a disposable Job Configurator job and approved adapter settings. Never
disable security software, broaden firewall scope, or test a network change that
could strand a production PC.

## Package verification

Prefer the self-contained package:

- `Kiloview-PC-Onboarding-win-x64.zip`
- `Kiloview-PC-Onboarding-win-x64.zip.sha256`

The archive must contain:

```text
Kiloview PC Onboarding.exe
Agent\Kiloview PC Agent.exe
README.md
SERVER-REMOTE-ONBOARDING-HANDOVER.md
TEST-MACHINE-HANDOVER.md
LICENSE.md
```

Verify and extract from PowerShell:

```powershell
$expected = (Get-Content .\Kiloview-PC-Onboarding-win-x64.zip.sha256).Split(' ')[0]
$actual = (Get-FileHash -Algorithm SHA256 .\Kiloview-PC-Onboarding-win-x64.zip).Hash
if ($actual -ne $expected) { throw 'Package checksum mismatch' }
Expand-Archive .\Kiloview-PC-Onboarding-win-x64.zip .\Kiloview-PC-Onboarding-test
```

## Baseline

Record machine name, Windows build, display scaling, active IPv4 adapters,
prefixes, DHCP state, gateways, DNS servers, network profile, Defender Firewall
profile state, NDI Tools version, and whether NDI Access Manager or NDI Discovery
is running. Record existing NDI configuration and adapter settings so approved
network tests can be restored.

Do not publish the agent `endpointId` outside the test report.

## Bootstrap acceptance

1. Run the packaged `Kiloview PC Onboarding.exe` and approve UAC.
2. Accept the EULA on first use.
3. Select the intended production adapter.
4. Confirm the agent installs even when NDI Tools is missing or outdated.
5. Confirm the UI reports `PC Agent ready` and directs onboarding to Job
   Configurator. It must not provide an enabled local onboard/update action.
6. Confirm the tray agent appears without another UAC prompt.
7. Close the bootstrap UI, then run the installed copy normally. It must display
   that onboarding must be initiated remotely and must not show the old main UI.
8. Run a newer complete release package over an existing installation. Confirm
   it updates the installed binaries but still does not expose local onboarding.
9. Confirm double-clicking the tray icon opens read-only status and the tray menu
   has no **Open PC Onboarding** action.

Verify installed state:

```powershell
Get-ChildItem "$env:ProgramFiles\Kiloview\PC Agent"
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Kiloview PC Agent'
Get-Content "$env:LocalAppData\Kiloview\PC Agent\agent-state.json"
```

## Firewall and agent API

From elevated PowerShell, verify inbound UDP 8093 and TCP 8094 rules are enabled,
allow, bound to the selected local address, and restricted to the selected subnet
on all profiles. `RemoteAddress` must never be `Any`. The obsolete branded ICMP
rule must be absent.

From the Configurator PC, verify discovery advertises:

```text
status-v1
memberships-v1
open-onboarding-v1
remote-onboarding-v2
network-config-v1
```

Verify `/api/v1/status` includes selected adapter ID/address/prefix plus
`networkConfiguration.dhcpEnabled`, `defaultGateways`, and `dnsServers`. Verify
health, status, and memberships contain no secrets or NDI configuration content.
Off-subnet discovery and HTTP requests must fail.

## Remote approval boundary

In Job Configurator, stage an `unchanged` network configuration for this endpoint
and initiate onboarding.

1. Confirm the local Yes/No prompt names the actual TCP source and warns that NDI
   and IPv4/gateway/DNS settings may change.
2. First deny. Confirm HTTP 403, no UAC prompt, no registration change, and no
   local configuration change.
3. Repeat and approve. Confirm UAC appears.
4. Deny UAC once. Confirm the server does not mark onboarding complete.
5. Repeat, approve the local prompt and UAC, and confirm the old onboarding UI
   never appears.
6. Confirm the elevated process fetches the endpoint-specific configuration,
   applies it, registers the PC, and shows one final result message.
7. Confirm Job Configurator treats HTTP 202 only as started/pending and marks
   success only after the stable endpoint registration is updated.

## NDI acceptance

With current NDI Tools installed, confirm the final message reports the version
state and success. Verify the preferred interface, send/receive group, discovery
server, and NDI Discovery **Use Access Manager Settings** state.

Separately test a snapshot without NDI Tools, and an outdated version when
practical. Onboarding and registration must still complete. The final Windows
message must say NDI action is required and direct the user to
`https://ndi.video/tools/`. Job Configurator must receive `ndiToolsVersion` as
`not installed` when Tools is absent.

If the official version check cannot be reached, onboarding must complete and the
final message must say currency could not be confirmed and direct the user to the
NDI website.

## Managed network acceptance

Perform only with machine-owner approval and console access.

1. **Unchanged:** confirm IPv4, prefix, gateway, and DNS remain identical.
2. **Static:** stage an unused address in the Configurator subnet, prefix 1-30,
   same-subnet gateway, and approved DNS list. Confirm Windows applies them, the
   agent state/firewall move to the new address, TCP 8094 becomes reachable at
   that address, and registration updates by stable endpoint ID rather than
   creating a duplicate.
3. **DHCP:** on a DHCP-enabled test VLAN, stage DHCP and confirm address, gateway,
   and DNS are obtained automatically, followed by agent and registration update.
4. Confirm adapter-ID mismatch, off-subnet static address, subnet/broadcast
   address, invalid prefix, off-subnet gateway, and malformed DNS are rejected
   before Windows settings change.
5. Confirm a network operation that does not produce a usable address within 30
   seconds fails visibly and is not reported as successful by the server.

## Persistence and membership

Confirm `agent-state.json` contains the job membership only after registration
succeeds. Restart or sign out/in and verify the unelevated agent returns on ports
8093/8094 without UAC. The tray must list the job and open its Configurator.

If testing removal, use only the disposable job. Confirm successful server
deletion removes local membership but leaves the agent and NDI settings. When the
server is unavailable, removal must fail visibly and retain local membership.

No Kiloview Windows service or scheduled task may exist.

## Evidence

Return PASS/FAIL for package, bootstrap, post-install local lockout, startup,
firewall, discovery capabilities, status network fields, approval denial, UAC
denial, silent elevated flow, unchanged networking, static networking, DHCP,
network validation failures, NDI-current notification, NDI-missing/outdated
notification, registration, tray membership, restart, and optional removal.

Include command output with endpoint ID redacted, screenshots of the bootstrap
ready state, local approval prompt, final result message, tray menu, and server
device card, plus exact reproduction steps and exception text for every failure.
