# Server-local onboarding, version 0.7.0

The suite's server repository owns the integration contract in
`PC-ONBOARDING-CONTRACT.md`. This companion remains independently built,
installed, updated, and released from its own Git repository.

`NDI Configurator PC Agent Setup.exe --server-command` accepts one schema-1 JSON
document over stdin and writes one JSON response to stdout. `install` requires
`acceptLicense:true` from an elevated installer that displayed the PC Agent
license. It can install without selecting a network yet. `onboard` requires the
installed utility, existing license acceptance, administrator rights, and an
active `adapterId`/`address` pair, plus `jobName` and `ndiDiscoveryServerIp`.

Local onboarding retains Windows IP configuration, applies the existing NDI
configuration service, updates the agent and its membership, and returns a
verified registration. It uses no additional approval, UAC, or result dialog.
Unknown fields, invalid schema/operation, and stale adapters fail. Responses
include `schemaVersion`, `success`, `version`, and either `endpoint` or `error`.
Exit code is 0 for success and 1 for failure.

This is a local process entry point. The agent's network approval contract
remains unchanged: remote onboarding still requires a local Yes/No and UAC.
Agent status adds `ndiConfiguration` for live interface/group/discovery drift
monitoring. Server and remote PCs share the agent identity and multicast path.

The server installer offers this complete package as an optional component,
checked by default, and retains newer independently installed versions.
Publishing this repository does not publish or install the server.
