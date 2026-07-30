# Codex Project Handoff

## Project

- Product: Kiloview PC Onboarding Utility
- Version: `0.1.0-dev.3`
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

- `artifacts/Kiloview-PC-Onboarding-win-x64.zip` — 63.294 MB,
  SHA-256 `49D74E554A74CD2BA472816F9F9B36A496D58BBAE94E756589234AF9C6F0C744`
- `artifacts/Kiloview-PC-Onboarding-win-x64-framework-dependent.zip` —
  0.415 MB, SHA-256
  `62D5F825D380885D0FDB8373088FFAF06E3AA45FC21B44D8F2C6A7D5D5C1D2C8`
