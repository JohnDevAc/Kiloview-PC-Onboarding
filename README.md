# Kiloview PC Onboarding Utility

This standalone Windows utility joins a remote Windows NDI endpoint to an active
Kiloview Job Configurator job. It is maintained independently from the Job
Configurator source and communicates through the configurator's PC onboarding
HTTP API.

## What it does

1. requires acceptance of the Kiloview PC Onboarding Utility EULA;
2. opens a dedicated first onboarding screen and requires the user to select
   the primary production IPv4 network adapter before any checks or scans run;
3. detects the installed NDI Tools version and checks the current version on the
   official NDI website;
4. when required, downloads the installer directly from `downloads.ndi.tv`,
   verifies its Windows publisher signature, and launches the interactive NDI
   installer so the user can review NDI's own licence;
5. scans the selected adapter's local subnet for Kiloview Job Configurator on
   TCP port 8091;
6. writes the preferred NDI interface, job send/receive group, and NDI discovery
   server to the local NDI configuration after creating a backup; and
7. registers the Windows PC with the selected job so it appears in the main
   device monitor.

## Requirements

- Windows 10 or 11, x64
- administrator approval (required by NDI Tools and the shared NDI configuration)
- an active IPv4 production network
- a current Kiloview Job Configurator running with LAN access and its private or
  domain Windows Firewall rule enabled

The Job Configurator and this utility must be from the same compatible release.
Running NDI applications must be restarted after onboarding because NDI
applications load Access Manager settings at startup.

## Build

From the repository root:

```powershell
.\scripts\Publish.ps1
```

The self-contained executable and distribution ZIP are written beneath
`artifacts`.

For the much smaller package that requires the .NET 8 Desktop Runtime on the
destination PC:

```powershell
.\scripts\Publish.ps1 -FrameworkDependent
```
