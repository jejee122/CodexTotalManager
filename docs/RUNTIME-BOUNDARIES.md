# Runtime and data boundaries

## Source tree

The source tree contains code, tests, documentation, and explicitly approved
non-sensitive visual/script resources only. It never contains runtime data,
credentials, account state, server connection profiles, or old databases.

## Runtime data root

The candidate will expose one canonical runtime data root. Every persistent
store must bind all of the following before it can be opened:

1. canonical absolute path under the configured runtime root;
2. store schema version;
3. Manager instance identity;
4. source/owner identity;
5. explicit store purpose.

A missing, mismatched, or unknown store fails closed. The Manager does not
recursively search old directories, merge stores, silently migrate stores, or
create an empty database and report that recovery succeeded.

## Test data

Each test run creates exactly one temporary root containing its run identifier.
A test may delete only a directory it created and whose canonical path, marker,
and run identifier match the active test run. At teardown it verifies that the
directory no longer exists. Existing Codex, Manager, Moyuan, game, study-system,
and server databases are outside the test boundary.

## Custom extensions

Extensions are untrusted, out-of-process Windows executables discovered only from the
runtime `extensions/packages` directory. They are disabled until the user trusts the
current whole-package fingerprint. They do not receive Codex, account-pool, OAuth,
API-key, server or proxy configuration from the Manager. This process boundary protects
Manager stability but is not an OS sandbox: an extension still has the permissions of
the current Windows user. Reparse points, directory escape, shell command concatenation,
automatic startup and in-process DLL loading are rejected.

## Codex connection switch

The ordinary desktop UI starts detached unless `~/.codex/config.toml` contains
the complete, unmodified Total Manager-owned `native-routing v2` block. The block
keeps Codex on its built-in `openai` identity and owns only `openai_base_url` plus
`model_catalog_json`. Gateway display returns only the provider identifier and a
URL stripped of user information, query parameters and fragments. Connecting
requires explicit in-app confirmation; disconnecting removes only the owned block
and owned catalog artifacts. Neither action restarts Codex.

## External runtime resources

The following resources are intentionally absent from Git:

- real `ssh_config` and server identities;
- `cli-proxy-api.exe`;
- credentials, DPAPI payloads, cookies, tokens, and account state;
- usage ledgers and quota observations;
- local user configuration and backups.

The server health feature remains disabled until an explicitly configured,
hash-verified external connection profile is present. The CLIProxy feature
remains disabled until a package/runtime step injects the pinned executable and
verifies its SHA-256 before launch.

## Deployment

This source candidate is not deployable by itself. A candidate package must
include a source commit, build manifest, dependency manifest, test evidence,
runtime-resource manifest, rollback package, and an explicit `DEPLOYABLE`
decision. Replacing the currently installed Manager requires a separate user
approval.
