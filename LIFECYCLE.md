# PC Agent software lifecycle

The complete package includes `toolbox-lifecycle.json`, schema 1, product
`pc-agent`, entry point `NDI Configurator PC Agent Setup.exe`. Invoke
`--lifecycle-request <absolute request.json>` only when a verified package or
installed descriptor advertises support. The native executable runs inspection
unelevated and owns elevation/licence prompts for mutation. Onboarding's
`--server-command` schema 1 is a separate contract and requires the server's existing
administrator context; remote consent and UAC are unchanged.

Actions: inspect, install, update, repair, uninstall. Result file is `result.json`
beside the request in an owned operation directory. Protect its DACL to allow
writes only to the original desktop user, SYSTEM and Administrators. Both files
are UTF-8; final results echo schema/product/action/operation identity and are
atomic/durable. Exit 0 means succeeded/no-change, 1602 cancelled, 1618 busy,
3010 restart-required, and nonzero means failure/partial/blocked. A process exit
without a matching final result does not establish success.

Installed descriptor: `%ProgramFiles%\NDI Configurator\PC Agent\toolbox-lifecycle.json`.
It resolves `Maintenance/NDI Configurator PC Agent Setup.exe`, a retained complete
maintenance pair used for inspect, repair and uninstall after app removal.
Installed Apps points to the same owned utility with `--uninstall`. Installation
ownership is recorded from the real desktop account; alternate-admin elevation
does not change that profile. A different desktop owner cannot remove it.

Inspect reports nullable installed/configured/running/healthy and component
versions. Pair matching ignores build metadata but retains prerelease ordering.
An invalid current configuration does not fall back to a legacy file. A valid
saved network can be configured while unavailable. Memberships, network
availability and pending recovery are separate observations. Healthy stays unknown
when not independently verified. safeToRetry requires no active lease and no
pending package/removal recovery; activeOperationId may be unknown.

Fresh lifecycle installation is network-neutral and requests the owned licence
if not accepted. Configuration/onboarding is a later explicit flow. Updates retain
saved identity, memberships and NDI configuration. A complete newer installed pair
is retained unless a standalone lifecycle request explicitly supplies
`requestedVersion` for the admitted complete package. That target is the binary
release version (for example `0.7.5-dev.1`, without a Git-tag `v` prefix); build
metadata is ignored, but the channel/prerelease suffix must match exactly.
Both payload binaries must match before package effects begin. This explicit
target permits a channel switch or downgrade of a complete matching installed
pair; mixed or missing newer components still require repair. Success requires
both observed installed binaries to match the target. Requests without a target,
bundled server installs, normal Setup and onboarding retain the newer-pair policy.
Installation at the same version may repair files/registration and
therefore reports succeeded rather than a false no-change. The tray updater uses
Production releases and explicitly labels transitions from Development.

Uninstall preserves saved state, endpoint identity, NDI Tools and NDI settings.
It removes only this installation's application files, current-owner startup,
owned firewall rules and Installed Apps registration. Partial failure retains a
durable removal journal and maintenance utility for retry. Legacy installations
without an ownership receipt first require repair through a matching/newer package.
Other products must invoke this owner rather than recursively delete its files.

Validation uses both projects under `tests`. The Setup validation executable also
offers `--lifecycle-fixture <request.json> <outcome>` for cross-product protocol
checks with no native mutations. Real UAC/registration/reboot/removal acceptance
requires a disposable VM coordinated by the master; production changes and
publication are outside the current development pass.
