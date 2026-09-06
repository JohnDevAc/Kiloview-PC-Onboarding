# Suite interoperability

PC Agent is an independently installed Windows endpoint. Job Configurator, KiloLink and NDI Discovery can all be remote. Configure this PC's production adapter locally; remote job onboarding retains its Yes/No prompt and UAC. Server-local onboarding retains schema 1 and uses the server's elevation and optional-component licence without a second confirmation.

The strengthened remote contract advertises `onboarding-attempt-v1`. Approval, configuration fetch and registration carry the same attempt ID, job ID and revision. An older Job Configurator cannot use the strengthened operation until updated. Registration retries retain their identity. Network configuration requires a preferred usable IPv4 address and a single IPv4 address on the chosen adapter for changes. Failed configuration/registration restores the original network/DNS, NDI files and Agent state using a separate bounded recovery deadline. Setup and multicast serialize writes through the same lock.

Monitoring follows DHCP address changes on the saved adapter and preserves the endpoint GUID. Missing/ambiguous addresses suspend the listener and report network unavailable. It does not select another adapter. NDI address drift remains visible and must be reapplied through approved companion onboarding.

Setup resolves the desktop owner from Windows process tokens and the trusted profile registry. Per-user configuration and startup remain in that user's profile even when another administrator supplies UAC credentials. Agent runs at user logon; a machine with nobody signed in does not have an unattended Agent service.

Agent and Setup are installed as a complete matching pair. Either newer binary prevents an older package from replacing a mixed installation. Stable versions follow equal-core prereleases. Files are staged before stopping Agent, failed replacements restore the previous pair, and a recovery journal allows the next Setup to repair an interrupted replacement. If recovery itself fails, Setup reports local repair instead of success.

NDI Tools acquisition checks the actual HTTPS package source before downloading or launching installation. Unavailable sources turn the action into Retry; installed NDI and LAN configuration remain usable when currency cannot be checked. Transfers have stalled-read deadlines; installer signature and publisher validation remain mandatory. A complete local companion package installs without fetching .NET. Its online updater independently gates and verifies its release assets.

Ports remain UDP 8093 and TCP 8094 for Agent, TCP 8091 for Job Configurator, and TCP 5959 for NDI Discovery. No custom Discovery port is accepted. Discovery covers /20 through /30 with bounded concurrency and a four-minute deadline, reporting incomplete coverage explicitly.

Validation uses both projects under `tests/` with isolated configuration. Do not run a live install or change production network/NDI settings as part of build validation. Actual alternate-account UAC, DHCP changes, reboot/logoff, Public/second-NIC firewall behavior and Arena/device operation require controlled deployment acceptance. Local rollback handles caught failures; abrupt power loss during network onboarding may require local repair.

Build through `scripts/Publish.ps1`. Publish this repository separately before a compatible server release is built from a clean checkout. Historical handovers describe earlier capabilities; this document describes the interoperability changes.

