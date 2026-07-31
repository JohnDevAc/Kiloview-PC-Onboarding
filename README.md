# Kiloview PC Onboarding Utility

This standalone Windows utility joins a remote Windows NDI endpoint to an active
Kiloview Job Configurator job. It is maintained independently from the Job
Configurator source and communicates through the configurator's PC onboarding
HTTP API.

## What it does

1. requires acceptance of the Kiloview PC Onboarding Utility EULA;
2. selects the primary production IPv4 network adapter on the main onboarding
   screen before discovery and configuration;
3. detects the installed NDI Tools version and checks the current version on the
   official NDI website;
4. when required, downloads the installer directly from `downloads.ndi.tv`,
   verifies its Windows publisher signature, and launches the interactive NDI
   installer so the user can review NDI's own licence;
5. scans the selected adapter's local subnet for Kiloview Job Configurator on
   TCP port 8091, automatically rescanning whenever the selected adapter changes;
   each discovered compatible server is also checked for an existing
   registration matching this PC's stable endpoint ID, address, or hostname;
6. after confirming NDI Access Manager and NDI Discovery are closed, backs up
   and writes the preferred NDI interface, job send/receive group, and discovery
   server, then configures NDI Discovery to use Access Manager settings;
7. registers the Windows PC, including its current Windows version, with the
   selected job so the version appears on its device-monitor card; and
8. creates or updates one branded Windows Firewall inbound ICMPv4 Echo Request
   rule on the Private profile so Job Configurator can check availability after
   the utility exits.

Each main-screen section uses a compact activity spinner while it is working
and a green tick when it completes. Each section also has the same compact
clockwise refresh control, including manual Job Configurator rescanning in
step 3. The Job Configurator page always opens after onboarding, while the
utility remains in the foreground.

Completion does not use a popup. Step 3 is replaced with a line-by-line summary
of the PC, job, preferred interface, NDI discovery server, NDI group, firewall
monitoring scope, and restart reminder. A compact `SUCCESS` label appears
in Kiloview green inside the disabled grey lower-right action button, replacing
its previous text for the rest of that session. The elevated application also
reclaims foreground focus when its first window is shown.

When discovery finds this PC in one or more local Job Configurators, those jobs
are marked `already onboarded` in step 3 and the status line names the matching
jobs. Selecting one changes the action to `Update this PC`; registration remains
idempotent.

The firewall rule is named `Kiloview PC Onboarding - ICMPv4 Echo`. Rerunning
onboarding atomically replaces that same rule instead of adding duplicates and
does not alter unrelated firewall rules. The utility reads the branded rule
back before reporting success, preventing a successful rule from being shown as
a setup failure. Its remote scope is the selected Job Configurator IP
when available; otherwise it is limited to the selected adapter's IPv4 subnet.
It is never opened to `Any`. The utility requests administrator elevation at
startup and shows a warning if Windows rejects the rule. No service, background
heartbeat, or resident process is installed.

## Requirements

- Windows 10 or 11, x64
- administrator approval (required by NDI Tools, the shared NDI configuration,
  and the scoped Windows Firewall availability rule)
- an active IPv4 production network
- a current Kiloview Job Configurator running with LAN access and its private or
  domain Windows Firewall rule enabled

The Job Configurator and this utility must be from the same compatible release.
An older Configurator can still supply the job settings, but cannot register the
PC until it is updated. NDI Access Manager and NDI Discovery must be closed while
settings are applied so they cannot overwrite the updated JSON when they exit.
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
