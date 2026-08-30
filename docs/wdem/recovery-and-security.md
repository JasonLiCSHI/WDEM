# WDEM recovery and security

WDEM separates inspection, planning, confirmation, privileged execution, and
verification. A saved or imported result is never authority to make a current
machine change.

## Elevation boundary

The desktop and CLI remain at the current user's privilege level. When a
confirmed plan contains administrator steps, WDEM starts
`Wdem.ElevatedHost.exe` through UAC and sends only the narrowly scoped resource
work over an authenticated local named-pipe channel. Expect one consent prompt
for an elevated phase. Never start the broker directly or approve an
unexpected prompt.

## Trust, hashes, and signatures

WDEM binds file/HTTPS configuration inputs to explicit SHA-256 values and
rechecks them before commit. It rejects mismatched hashes, unsafe path
redirection, alternate data streams, untrusted bootstrapper signatures, VSIX
identity mismatch, and a Visual Studio instance that changed after planning.
Use release `SHA256SUMS.txt` before extraction and profile `expectedSha256`
values for configuration inputs. Hashes prove byte identity; Authenticode
signature policy additionally establishes the trusted publisher where a
provider requires it.

## Redaction

Run events, errors, exported reports, and persisted NDJSON logs pass through
WDEM's redactor. It removes recognized secrets, credentials, tokens, sensitive
query values, and user-specific paths. Still review a report before sharing it,
and never put secrets in profile IDs, filenames, or command arguments.

## Cancellation and finalization

Cancellation stops work that can be stopped safely. An atomic configuration
commit or installer finalization already in progress may finish inside a bounded
finalization window so WDEM does not leave a half-written destination. The run
records whether cancellation was requested, which steps finalized, and which
resources require verification.

## Restart and crash recovery

Snapshots and append-only event logs are stored under
`%LOCALAPPDATA%\WDEM\runs`. After a crash or required restart, use `runs list`
and `resume --run <guid>` to inspect recovery state. Resume replays only the
recorded recovery contract; it does not trust stale machine state or silently
continue a previously confirmed mutation.

Before any further Apply, WDEM must perform a fresh Detect and build a fresh
Plan. Review and confirm that new plan. Retry is resource-scoped and follows
the same detection and precondition rules.

## One-time source-state import

At first initialization WDEM may create a single migration marker under
`%LOCALAPPDATA%\WDEM`. It never writes to the retired source directory and does
not reread it after the marker exists. Imported step names are labelled as
migration history, not compliance. The exact legacy directory and marker are
documented in [source provenance](source-provenance.md#one-time-state-import).

## Provenance boundary

The two source-provenance Git remotes are fetch-only and have `DISABLED` push
URLs. They exist solely to audit MIT-licensed source history. They are not
supported WDEM products, release channels, merge targets, or Apply inputs. See
[source provenance](source-provenance.md) and
[third-party notices](../../THIRD-PARTY-NOTICES.md).
