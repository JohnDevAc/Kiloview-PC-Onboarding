# Suite ownership

This is an independent repository and deployment within the NDI Configurator
suite. The sibling `Job Setup - Kiloview Suite` workspace is responsible for both
codebases; its `suite.json` and `PC-ONBOARDING-CONTRACT.md` define the integration.
Read this repository's `CODEX-HANDOFF.md` before changes.

- Keep this Git, `dev`/`main` branches, versions, releases, and updater independent
  of the server's `development`/`main` branches and deployment.
- This application owns PC NDI configuration. Server-local onboarding uses the
  installed elevated utility's bounded stdin/stdout command; it needs no second
  confirmation after component installation. No network endpoint bypasses remote
  PC Yes/No and UAC approval.
- Publish complete packages through `scripts/Publish.ps1`; the server installer
  bundles that package with a manifest and a default-selected optional component.
- Validate both projects under `tests`, and run the server suites for contract
  changes. Tests use isolated state/configuration paths and ephemeral ports.
- Keep the blue PC Agent icon distinct from the server icon. See `assets/ICON.md`.
- Commit and release this repository separately before a server release bundles
  it. Building packages does not install them or change live network settings.
