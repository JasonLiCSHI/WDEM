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
`%LOCALAPPDATA%\WDEM\runs`. After a crash or required restart, the desktop
shows recovery candidates before profile selection. In the CLI, use
`runs list` (which marks recoverable runs and lists their pending resource IDs),
then `resume --run <guid>` to recover or
`abandon --run <guid>` to mark an incomplete run cancelled without applying
anything.

Approved snapshots are bound to a current-user-protected revision index under
the runs directory and an authenticated freshness anchor under the WDEM root.
WDEM rejects an older snapshot, a changed snapshot, a replayed or missing
revision index, or an approval envelope removed from a committed snapshot.
Pending and committed index and anchor entries let WDEM recover the old or new
complete state if a process stops during an atomic upgrade. Simultaneously
deleting or rolling back the entire WDEM root, including every protected
freshness anchor, is outside the locally detectable threat model; the missing
run grants no authority to apply changes.

When the protected index and anchor are first created, WDEM performs one
bounded scan under the root state lock. It enrolls every structurally valid
legacy approved snapshot whose canonical filename matches its run ID, binding
the candidate's revision and digest into the authenticated index. The enrolled
set supports upgrading multiple legacy runs independently and is not expanded
on later reads. Malformed, mismatched, oversized, or subsequently injected
legacy snapshots are not authorized and are quarantined when read.
Older current-format snapshots are enrolled as committed only when their
current-user-protected per-run commitment authenticates the snapshot's exact
revision and digest and the protected approval envelope validates. A marker
without that revision and digest is not sufficient to authorize a snapshot.

Recovery never replays historical commands or trusts stale machine state. Core
reloads the profile, rebuilds the resource graph, runs a fresh Detect, and
creates a fresh Plan in a replacement run. Recovery may apply that fresh plan
only while it remains inside the prior approval seals and the permitted runtime
refinement boundary. If current state requires work outside that boundary,
recovery fails without broadening authority; start the normal inspect, plan,
confirm, and apply flow to approve a new plan. Retry is resource-scoped and
follows the same fresh detection, planning, and approval-boundary rules.

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
