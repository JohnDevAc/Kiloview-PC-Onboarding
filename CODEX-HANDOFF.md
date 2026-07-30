# Codex Project Handoff

## Project

- Product: Kiloview PC Onboarding Utility
- Version: `0.1.0-dev.5`
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
- Creates or updates the single branded `Kiloview PC Onboarding - ICMPv4 Echo`
  inbound rule after successful registration. It allows ICMPv4 Echo Request
  only on the Private profile and scopes the remote source to the Configurator
  IP, falling back to the selected subnet but never `Any`.
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

- `artifacts/Kiloview-PC-Onboarding-win-x64.zip` — 63.298 MB,
  SHA-256 `ADF9BC76E4D8806946FFD75B9879D6B95333C406E50C914CE40E1D30FCEA8A88`
- `artifacts/Kiloview-PC-Onboarding-win-x64-framework-dependent.zip` —
  0.419 MB, SHA-256
  `A48BECA78A783BB6F5367CE0EC09D8EC8C230ABA34DD5EDCE3F56AB59814417C`
