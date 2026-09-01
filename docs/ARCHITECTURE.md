# WDEM MVP Architecture

WDEM is built around a stable core: **Declarative Profile → Task DAG → Workflow Pipeline**. CLI and WPF are two clients of the same application model. Products such as Visual Studio and ReSharper never become special types in Core.

```text
Release-defined HTTPS Profile Source
                 │
                 v
       ProfileCatalog.List / Load
          │ remote-first  │ offline
          v               v
     validated data <── last-known-good cache
                 │
                 v
      Profile parser + content trust
                 │
                 v
 Optional selection ──> Task DAG
                           │
                           v
                Task state-machine graph
               Entry · Residence · Exit
                           │
                progress / output / report
                    │               │
                   CLI             WPF
```

## Deep modules and seams

- `ProfileCatalog` is the external seam for remote configuration. Its public API is limited to `ListAsync` and `LoadAsync`; HTTPS enforcement, redirect validation, size limits, UTF-8 decoding, atomic caching, offline fallback, and ID validation remain internal.
- `ProfileParser` converts versioned JSON into one domain model, so callers never handle JSON details.
- `TaskGraph` encapsulates Required/Optional selection, dependency closure, deduplication, topological sorting, and cycle detection.
- `EnvironmentManager.StartApply` compiles or selects a per-Task state graph and encapsulates graph execution, failure propagation, cancellation, and reporting.
- `ITaskRuntime` is the execution seam. The current Windows adapter starts an executable with an argument array directly. Future script downloaders, elevation brokers, or remote executors can be introduced here without teaching the DAG about specific products.

## Profile Source and cache

Default Source:

```text
https://raw.githubusercontent.com/JasonLiCSHI/WDEM/main/profiles/
```

The Source is release configuration, not a GUI or CLI option. Changing it requires a code change and a new release. `%LOCALAPPDATA%\Wdem\settings.json` stores only trust records, while the cache resides at `%LOCALAPPDATA%\Wdem\cache\profiles/<source-id>/`.

Every read is remote-first:

1. remote content must pass size, encoding, JSON, Schema, ID, and reference validation;
2. validated content atomically replaces the last-known-good cache;
3. only network and timeout failures fall back to the cache, so malformed remote content is never hidden by stale data;
4. both Remote and Cache content are trusted by `source-id + SHA-256`, and any content change requires fresh approval.

The installer does not contain `profiles/`. That repository directory is the content published by the official remote Source.

## Profile and runtime extensibility

Profile Schema v1 covers the compact default workflow: Profile version, Task description, Required/Optional behavior, dependencies, version requirements, preferred version, source, Detect/Pre/Apply/Post commands, human-readable step names, and Detect reuse for Verify. It is compiled into a state graph rather than handled by a separate runner. Commands always use an executable plus an argument array; shell command strings are never concatenated.

Profile Schema v2 optionally declares a Task `workflow`. A workflow names an initial state, a transition limit, and states. Every state maps to a stable `TaskExecutionState`, owns ordered Entry, Residence, and Exit Activity collections, and declares ordered transitions or a terminal outcome. Built-in declarative conditions cover Activity success/failure and detected compliance. The parser rejects missing initial states, duplicate state IDs, dangling targets, non-terminal states without transitions, and terminal states with transitions.

Extension happens at two levels:

- new software, parameters, versions, and post-install configuration require only Profile changes;
- new execution mechanisms use an `ITaskRuntime` adapter;
- new in-process work derives from `WorkflowActivity`;
- new workflow factories implement `ITaskWorkflowProvider`, and code-defined transitions may use custom predicates;
- declaration-format changes use a new `schemaVersion`.

The current engine is a deterministic sequential DAG scheduler. Parallel scheduling, persistent checkpoints, transactional rollback, restart resume, and signature policies are not implemented. These belong in future modules rather than as product-specific logic in Core.

## Task-driven state and reactive UI

Core's `WorkflowStateMachine` is the single source of truth for execution. For each DAG Task, it owns the current runtime state ID, enters that state, and only then executes its ordered Entry, Residence, and Exit Activities. Residence results are evaluated by ordered transition predicates, and the selected target becomes the next runtime state. The transition limit prevents accidental infinite cycles.

`DefaultTaskWorkflowProvider` compiles Schema v1 into the familiar path:

```text
Pending → Ready → Detecting → RunningPre* → Applying → RunningPost* → Verifying
                                                                    ├→ Succeeded
Detecting ──────────────────────────────────────────────────────────└→ Satisfied
Any active state ─→ Failed / Cancelling → Cancelled
Dependency failure or cancellation ─→ Blocked
```

Schema v2 may replace that path with any validated bounded graph. Runtime states are intentionally separate from presentation states: every `TaskWorkflowState` projects one stable `TaskExecutionState`. This mapping lets the graph evolve without teaching WPF or CLI about state IDs or transitions.

`WorkflowStateStore` publishes immutable `WorkflowSnapshot` values with monotonically increasing `Revision` numbers. Each `WorkflowTaskSnapshot` carries the runtime state ID, projected Task state, Activity ID and location, progress, result, Activity index, and `CanStart`, `CanCancel`, and `CanSelect` capabilities. Starting or cancelling a Workflow reprojects the capabilities of every Task.

WPF does not maintain an active-Task collection or interpret execution flow. It maps Task snapshots into presentation state and binds directly to their capabilities. Start All and Cancel All aggregate the corresponding Task capabilities. A small workspace state still handles Profile loading, trust, and inspection because they occur outside the Task Workflow.

CLI, WPF, and JSONL logging consume the same Core updates. Cancellation first moves a Task to `Cancelling` and disables duplicate actions. The Task becomes `Cancelled` only after the Runtime has stopped its process tree. Even if a Runtime command wins a cancellation race and returns success, the state machine does not run Exit Activities, take another transition, or start downstream Tasks. Custom Activities must honor the supplied cancellation token; command Activities delegate cancellation to the Windows Runtime, which terminates the process tree.

Core classifies Detect results as `Missing`, `UpgradeRequired`, `VersionMismatch`, or `Satisfied`. A detected version below a lower-bound requirement such as `>= 2.50` produces `UpgradeRequired`, and both CLI and WPF consume that same result. Presentation state distinguishes `Pending`, `Running`, `Satisfied`, `UpgradeRequired`, `NeedsAttention`, `Succeeded`, `Failed`, `Cancelled`, and `Blocked`. Version failures and blocked or failed Tasks use warning styling. All phase progress comes from `WorkflowProgress`, allowing future Runtime adapters to report finer-grained progress without changing UI button rules.

Task capability matrix:

| Task/Workflow state | `CanStart` | `CanCancel` | `CanSelect` |
|---|---|---|---|
| Idle Task | Yes | No | Optional only |
| Planned, non-terminal Task while Workflow is `Running` | No | Yes | No |
| Unplanned Task while Workflow is `Running` | No | No | No |
| Task is `Cancelling` | No | No | No |
| Workflow has completed | Yes | No | Optional only |

## Project responsibilities

- `Wdem.Core`: Source/Catalog models, Profile Schema, version requirements, DAG construction, inspection, Workflow state, and reports.
- `Wdem.Windows`: user settings, trust records, logs, the shared administrator requirement, Windows process execution, output forwarding, and process-tree cancellation.
- `Wdem.Cli`: Profile selection, trust confirmation, complete plan preview, retries, and terminal output.
- `Wdem.App`: installation-language-aware WPF workbench, unified button-state projection, Required/Optional sections, Task details, progress, cancellation, and logs.

## Release

`build/Build-Installer.ps1` publishes WPF and CLI as self-contained Windows x64 single-file applications and uses Inno Setup to create a bilingual installer. It installs the repository's `script/` and `settings/` directories as shared runtime assets beside both executables, persists the selected language, and does not copy any Profile. Neither executable requests automatic UAC elevation: each checks the shared administrator requirement, explains how to relaunch when necessary, and exits before loading a Profile. On the first elevated launch, WDEM creates user settings and connects to the remote Source.
