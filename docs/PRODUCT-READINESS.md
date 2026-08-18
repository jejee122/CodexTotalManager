# Windows software readiness

This file separates a usable Windows application shell from Codex-specific business acceptance.
Passing source tests never upgrades a candidate to a stable release by itself.

## Implemented in 3.0.0-rc.29 source

- single-instance desktop process;
- global crash capture, local rotation and credential redaction;
- first-run safety guide with Codex disconnected by default;
- in-app software center for version, privacy and license status;
- optional current-user Windows startup entry;
- system tray, restore window and explicit full exit;
- manual GitHub Release check with HTTPS host pinning, response-size limit and no silent install;
- sanitized diagnostic summary, diagnostic folder and user-data folder shortcuts;
- atomic local release installation with payload manifest verification and rollback state;
- Windows Installed Apps registration, Start menu entry, desktop shortcut and friendly uninstaller;
- uninstall choice to preserve user data or delete it after a second warning;
- settings, backups, restore, retention controls and recycle-bin deletion;
- custom extension discovery, trust, enable, disable, run and crash isolation.

## Still required before a stable Windows release

- verify and publish the exact rc.29 candidate package from a clean, reproducible source revision;
- validate install, upgrade, rollback and uninstall on a separate clean Windows test computer;
- complete the hash-bound real Codex acceptance matrix;
- verify current Codex model catalog, official/third-party tool calls, conversation continuity,
  real account charging attribution and current Dream Skin compatibility;
- keep the selected MIT repository license and ship a complete third-party notices inventory;
- obtain and apply an Authenticode code-signing certificate, or clearly document the unsigned warning;
- perform keyboard-only, screen-reader, text-scaling and high-DPI visual acceptance;
- decide whether a future stable release needs a full in-app download/install updater.

## Deliberate non-features

- no silent update or silent configuration migration;
- no automatic Codex restart or UI clicking;
- no automatic server discovery;
- no automatic plugin trust;
- no deletion of account ledgers as if they were disposable logs;
- no claim of production readiness without separate real-environment evidence.

## Promotion to DEPLOYABLE

`build.ps1 -Publish` may only create a candidate. A formal promotion requires a
clean source revision, passing build/security/integration evidence, the exact
candidate `payload-manifest.json`, and a dedicated-test-computer real Codex
acceptance file whose manifest SHA-256 matches that candidate. Only then may
`scripts/emit-evidence.ps1 -MarkDeployable` write a `DEPLOYABLE.json` beside the
package. The installer verifies both files and fails closed if any identity,
hash, test result or real-environment acceptance field is missing or different.
The evidence command must receive the same hash-locked CLIProxyAPI artifact used
for the candidate build through `-CliProxyApiArtifactPath`; the value is scoped
to the integration-test child process and is restored afterwards.
