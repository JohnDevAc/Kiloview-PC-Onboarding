# NDI Configurator PC Agent online-update deployment handover

Copyright © 2026 John Lightfoot. Proprietary software; free for
non-commercial use only. See `LICENSE.md`.

## User flow

The installed tray agent exposes **Check for updates** on its right-click menu.
It reads the latest non-draft, non-prerelease GitHub Release for
`JohnDevAc/Kiloview-PC-Onboarding`, confirms that the release targets `main`, and
compares its `vMAJOR.MINOR.PATCH` tag with the installed version.

When a newer release exists, the agent asks before downloading. It downloads the
self-contained ZIP and adjacent SHA-256 manifest, verifies the package against
both the GitHub release-asset digest and checksum file, safely extracts it under
the current user's local application data, verifies the setup and agent product
identity/version, then launches Setup with `runas`. Windows UAC remains the
privilege boundary; the tray agent never writes to Program Files.

## Required release assets

Every production release must contain these exact assets:

- `NDI-Configurator-PC-Agent-win-x64.zip`
- `NDI-Configurator-PC-Agent-win-x64.zip.sha256`

The framework-dependent package may also be attached but is not used by online
updates. The repository and Releases must remain public; no GitHub credential is
embedded in the agent.

## Main-branch deployment

`.github/workflows/release-main.yml` validates and publishes every new
production version pushed to `main`. `Directory.Build.props` is the release
version source. A version is immutable: if its `vMAJOR.MINOR.PATCH` release
already exists, the workflow fails and the version must be incremented.

`.github/workflows/release-dev.yml` performs the same validation for `dev` and
publishes `vMAJOR.MINOR.PATCH-dev.NUMBER` as a GitHub prerelease. Installed
agents deliberately ignore these development releases.

The workflow:

1. builds Setup;
2. runs onboarding and agent validation hosts;
3. publishes self-contained and framework-dependent packages;
4. creates a production GitHub Release targeting `main`; and
5. uploads both ZIPs and checksum manifests.

## Acceptance

1. Install the previous production agent.
2. Publish a higher version from `main` and wait for the release workflow.
3. Right-click the tray icon and select **Check for updates**.
4. Confirm the newer version is shown before any download.
5. Accept download, then approve UAC.
6. Confirm the final Setup message uses the NDI Configurator PC Agent name.
7. Confirm tray status and `GET /api/v1/status` report the new version.
8. Repeat the check and confirm the agent reports that it is up to date.
9. Corrupt a test package/checksum in an isolated test release and confirm the
   agent refuses to launch Setup.
