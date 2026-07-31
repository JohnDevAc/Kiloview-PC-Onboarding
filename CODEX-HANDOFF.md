# Codex Project Handoff

## Project

- Product: Kiloview PC Onboarding Utility
- Version: `0.1.0-dev.7`
- Intended default branch: `main`
- Owner/copyright: John Lightfoot
- Licence: proprietary, free for unmodified non-commercial use

This is a standalone companion to Kiloview Job Configurator. Do not copy its
source back into the Job Configurator repository. The main application retains
only the `/api/pc-onboarding/profile` and `/api/pc-onboarding/register`
compatibility endpoints, persistent remote-PC records, and monitor cards.

## Current behavior

- Shows the EULA before use.
- Handles primary IPv4 network-adapter selection and all onboarding work on one
  main screen.
- Checks NDI Tools against the official NDI release.
- Downloads only from `downloads.ndi.tv` and verifies the Windows publisher
  signature before launching NDI's interactive installer.
- Scans the selected subnet for Job Configurator on TCP 8091.
- Automatically rescans for Job Configurator whenever the selected adapter
  changes.
- Checks every compatible discovered Configurator's state for this endpoint ID,
  selected address, or hostname, marks matching jobs as already onboarded, and
  offers an idempotent registration update.
- Detects older Configurators through their state API and labels them as
  requiring an update; they can apply local NDI settings but cannot register.
- Backs up and applies NDI preferred-interface, job-group, and discovery-server
  settings, and tells NDI Discovery to use Access Manager settings.
- Detects current and legacy Access Manager/NDI Discovery processes and refuses
  to write while either application can overwrite the configuration.
- Uses Per-Monitor-V2 DPI scaling and a two-column responsive layout for
  high-scaling laptop displays.
- Uses per-section spinner/tick activity indicators instead of a progress bar.
- Uses the same compact clockwise refresh control in all three cards; step 3
  has no separate scan button.
- Always opens the Job Configurator page after onboarding and keeps the utility
  in the foreground.
- Reclaims foreground focus when the elevated UI first appears.
- Replaces completion popups with a line-by-line result in step 3 and a compact
  green `SUCCESS` text in the disabled grey lower-right action button after
  completion.
- Sends the current Windows version as `operatingSystemVersion` during endpoint
  registration for the Configurator's Windows device card.
- Atomically creates or replaces the single branded
  `Kiloview PC Onboarding - ICMPv4 Echo`
  inbound rule after successful registration. It allows ICMPv4 Echo Request
  only on the Private profile and scopes the remote source to the Configurator
  IP, falling back to the selected subnet but never `Any`.
- Reads the branded firewall rule back before reporting success, avoiding false
  setup-failure warnings after Windows has accepted the rule.
- Surfaces firewall failures as onboarding warnings; it does not modify
  unrelated rules or install a service/background heartbeat.
- Registers the endpoint with the selected Job Configurator job.

## Validation

Before committing:

```powershell
dotnet build .\Kiloview.PcOnboarding.csproj --configuration Release
dotnet format .\Kiloview.PcOnboarding.csproj --verify-no-changes --no-restore
```

Use `scripts\Publish.ps1` for self-contained packaging or add
`-FrameworkDependent` for the lightweight package.

Current local packages:

- `artifacts/Kiloview-PC-Onboarding-win-x64.zip` — 63.300 MB,
  SHA-256 `24708D70755CB752DD51D4861C0CEE93801E43A07DE48496DACD7F44955FCA41`
- `artifacts/Kiloview-PC-Onboarding-win-x64-framework-dependent.zip` —
  0.420 MB, SHA-256
  `11E2880658380F25A1E06D151E79C4FA77A3FCD0889B7AA27384A2121680C394`
