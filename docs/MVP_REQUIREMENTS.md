# WDEM Minimum Viable Product Requirements

This document defines the WDEM MVP baseline from SRS v1.0 and subsequent product decisions. The latest product decisions take precedence over SRS descriptions that model Visual Studio or ReSharper as dedicated Resources or Providers.

## 1. Product definition

WDEM is a Windows environment configuration tool with both CLI and GUI clients. A release loads declarative Profiles from one HTTPS Profile Source selected in code, builds a DAG from Task dependencies, inspects the local environment, and converges it toward the declared state. Local storage contains only trust records and the last-known-good cache.

```text
Remote Profile Source / offline cache
            ↓
     Selected Tasks
            ↓
         Task DAG
            ↓
   Plan → Task state machine → Report
          Entry · Residence · Exit
```

Visual Studio, ReSharper, Git, and the .NET SDK are ordinary Profile Tasks. Core contains no product-specific installation logic.

## 2. Minimum MVP scope

### 2.1 Required

- Load Profiles from the release-defined HTTPS Profile Source.
- List and select among multiple Profiles.
- Use this WDEM repository's `main/profiles/` directory as the default Source.
- Do not expose Source selection or editing in either GUI or CLI; changing the Source requires a new release.
- Update the last-known-good cache after a successful remote load and use it only when the network is unavailable.
- Do not bundle Profiles in the installer.
- Require explicit, content-hash-based user trust for Remote and Cache Profiles before Detect runs.
- Support Profile metadata: unique ID, version, display name, and description.
- Support Task metadata: ID, display name, description, Required/Optional, dependencies, version requirement, preferred version, and source.
- Support Task detection and apply commands, `pre`/`post` commands, and a version extraction rule.
- Compile Schema v1 Tasks into the standard Detect/Pre/Apply/Post/Verify state graph.
- Allow Schema v2 Tasks to declare a bounded state graph with Entry, Residence, and Exit command Activities.
- Project runtime state into stable Task state and capabilities consumed by both CLI and GUI.
- Represent command arguments as arrays and launch executables directly without implicit shell concatenation.
- Support `{source}`, `{preferredVersion}`, and `{appDirectory}` command placeholders.
- Select Required Tasks automatically and prevent deselection.
- Allow users to select Optional Tasks.
- Add dependencies automatically, remove duplicates, detect cycles, and topologically sort the graph.
- Support exact, wildcard, minimum, and range version requirements.
- Inspect using only commands declared as read-only and return a compliance report.
- Apply by detecting again, creating a plan, executing in DAG order, and running Detect/Verify afterward.
- Mark downstream Tasks as `Blocked` after an upstream failure.
- Skip Tasks that already satisfy their requirements.
- Start and cancel individual Tasks while presenting their current phase, command output, and detailed progress.
- Start and cancel all selected Tasks as one plan.
- Inspect all Tasks immediately after a Profile loads and present their local installation status.
- Report progress, logs, cancellation, and final outcomes.
- Provide CLI `inspect` and `apply` commands.
- Provide a WPF GUI for loading a Profile, selecting Tasks, inspecting, applying, cancelling, and reviewing results.
- Make CLI and GUI share the same Core and Windows Task Runtime.
- Require an elevated administrator process at GUI and CLI startup; show a clear instruction and exit before loading or executing Tasks when elevation is absent.

### 2.2 Not included

- Hard-coded Providers for Visual Studio, ReSharper, or any other product.
- A Profile marketplace, search, authentication, or private remote Sources.
- Profile digital signatures, certificate chains, or organization policies.
- Parallel Task scheduling.
- Automatic UAC elevation.
- Resume after restart.
- Rollback or uninstall.
- Linux, macOS, or ARM64 support.
- Legacy WinHome plugins, WSL, registry, services, scheduled tasks, Dotfiles, drift remediation, or self-update features.

## 3. Profile Schema

```json
{
  "schemaVersion": 1,
  "id": "csharp-developer",
  "version": "1.0.0",
  "displayName": "C# Developer",
  "description": "Minimal C# environment",
  "tasks": {
    "git": {
      "displayName": "Git",
      "description": "Version control client used by the developer toolchain",
      "required": true,
      "dependsOn": [],
      "version": ">= 2.50",
      "preferredVersion": "2.52.0",
      "source": "Git.Git",
      "detect": {
        "displayName": "Detect Git version",
        "executable": "git",
        "arguments": ["--version"],
        "versionPattern": "git version (?<version>\\d+(?:\\.\\d+)+)"
      },
      "pre": [
        {
          "displayName": "Prepare Git configuration",
          "executable": "powershell",
          "arguments": ["-NoProfile", "-File", "prepare-git.ps1"]
        }
      ],
      "apply": {
        "displayName": "Install Git with WinGet",
        "executable": "winget",
        "arguments": [
          "install", "--id", "{source}", "--exact",
          "--accept-package-agreements", "--accept-source-agreements", "--silent"
        ]
      },
      "post": [
        {
          "displayName": "Apply organization Git defaults",
          "executable": "powershell",
          "arguments": ["-NoProfile", "-File", "configure-git.ps1"]
        }
      ]
    }
  }
}
```

### 3.1 Profile Catalog

Every remote Profile Source contains an `index.json` and one `<id>.json` file per Profile in the same directory:

```json
{
  "profiles": [
    {
      "id": "csharp-developer",
      "version": "1.0.0",
      "displayName": "C# Developer",
      "description": "Minimal C# environment"
    }
  ]
}
```

- Listing requests remote `index.json` first and reads the cache only when the network is unavailable.
- Loading an ID requests remote `<id>.json` first and reads the cache only when the network is unavailable.
- Content updates the cache only after passing size, UTF-8, JSON, Schema, and reference validation.
- Cached content remains remote content and requires the same trust confirmation before execution.
- IDs allow only letters, digits, dots, underscores, and hyphens to prevent path traversal.

### 3.2 Schema v1 Task rules

- `detect` is required, and the Profile author guarantees that it is read-only.
- `apply` is required for installable Required and selectable Tasks.
- `pre` and `post` are ordered arrays of general commands and default to empty.
- A Task `description` and command `displayName` are optional human-readable details with no scheduling semantics.
- `pre` runs before Apply; `post` runs after Apply and before Verify. Failure in any step fails the Task.
- `source` is interpreted by the Task and may represent a WinGet ID, URL, file path, or enterprise source identifier.
- `versionPattern` must expose a named `version` capture group.
- Without a version requirement, compliance depends only on successful detection.

### 3.3 Schema v2 workflow rules

- A Task may declare `workflow.initialState`, `workflow.maxTransitions`, and a non-empty `workflow.states` array.
- Every state has a unique `id`, a projected `taskState`, ordered `entry`, `residence`, and `exit` command Activity arrays, and either transitions or a terminal `outcome`.
- A transition names a target state and one built-in condition: `always`, `activitiesSucceeded`, `activitiesFailed`, `taskSatisfied`, or `taskNotSatisfied`.
- State Entry runs first, Residence runs while the state is active, a matching transition is selected, and Exit runs before the target state is entered.
- Activity failure may select a recovery transition. An Exit failure fails the Task because the transition cannot complete safely.
- The state graph must reject duplicate or missing states, dangling targets, invalid Task-state projections, and unbounded execution past `maxTransitions`.
- Code extensions may derive from `WorkflowActivity`, supply custom transition predicates, or implement `ITaskWorkflowProvider`; these extensions must preserve cancellation and immutable Task snapshots.

## 4. Core business rules

1. A Profile is the configuration entry point, and a Task is the only scheduling unit.
2. A Task appears only once in a DAG.
3. Dependencies must verify successfully before downstream Tasks can run.
4. A dependency cycle prevents the entire run.
5. Detection failure and a missing Task are distinct results.
6. Inspect never invokes an `apply` command.
7. Apply exit code zero means only that the Apply phase completed; the Task succeeds only when Verify satisfies the version requirement.
8. Apply and retry must regenerate the plan and create fresh Task workflows at their declared initial states; the Schema v1 initial state is Detect.
9. Starting one Task automatically includes and first executes any unsatisfied dependencies.
10. Cancelling one Task terminates its active process tree. Dependents become `Blocked`, while unrelated Tasks may continue.
11. Cancel All terminates the active process tree and prevents any new Task from starting.
12. Detect, Pre, Apply, Post, and Verify cannot run until the current Remote/Cache Profile content hash is trusted.

## 5. CLI

```text
wdem profiles
wdem inspect --profile <id> [--trust-profile]
wdem apply   --profile <id> [--select task1,task2 | --task task1] [--yes] [--retries N] [--trust-profile]
```

- A missing `--profile`, invalid Profile, or invalid DAG returns a non-zero exit code.
- `apply` presents the plan and asks for confirmation by default; `--yes` skips apply confirmation.
- After loading a Profile, CLI detects and displays every Task's local status before handling inspection or apply actions.
- `--retries N` starts each failed attempt again at Detect and skips Tasks that are already satisfied.

## 6. GUI

The minimum single-window interface contains:

- a read-only release Profile Source, Profile selection, and refresh action, with no Source editor;
- Profile ID, display name, and version;
- a Required Task section above an Optional Task section;
- Task description, source, version, dependencies, and Detect/Pre/Apply/Post/Verify command details;
- Start and Cancel for individual Tasks plus Start All and Cancel All;
- automatic local detection after Profile loading;
- overall progress, current Task phase, command-level progress, live logs, structured user-operation entries, and final statistics.

The Core state machine enters a runtime state before running its Entry, Residence, and Exit Activities. Activity results drive subsequent transitions, and each runtime state projects a stable Task state. Task snapshots directly expose start, cancel, and selection capabilities. The GUI only reacts to projected Task state and capabilities; it never interprets transitions. Global start/cancel actions only aggregate Task capabilities. If the Source is unavailable, only refresh is enabled. After cancellation is requested, duplicate cancellation is disabled immediately, and the Task becomes Cancelled only after its process tree exits.

Apply must present the execution plan before it begins.

## 7. Acceptance criteria

- Remote and Cache Profile content produce the same domain model.
- Changed remote content requires fresh trust confirmation.
- Required Tasks, selected Optional Tasks, and automatically included dependencies form a deterministic topological order.
- A cycle error includes the cycle path.
- All four version-expression forms match correctly, and an unparseable version is never considered satisfied.
- Inspect never invokes Apply.
- A satisfied Task is marked `NotRequired`.
- Apply detects again afterward and fails the Task if verification does not pass.
- `pre`, `apply`, and `post` run strictly in order, followed by Verify.
- Schema v2 runs state Entry, Residence, and Exit Activities in order and follows the first matching transition.
- Runtime state IDs and Activity locations are projected through progress, snapshots, and reports.
- Cancelling a custom workflow prevents Exit Activities, further transitions, and downstream Tasks from executing.
- Downstream Tasks do not execute after an upstream failure.
- Cancelling one Task stops its process tree and blocks dependents; Cancel All prevents all subsequent Tasks from starting.
- Starting one Task automatically handles its dependencies.
- Every Task can report phase, output, and progress.
- CLI and GUI use the same execution module for the same Profile.
- Tests verify Core with a Fake Task Runtime and never install real software.

## 8. Deep modules and test seams

| Module | Public test seam | Hidden complexity |
|---|---|---|
| Profile Source | `ProfileSourceDefinition` | HTTPS Source URL, identifier, and display metadata validation |
| Profile Catalog | `ProfileCatalog.ListAsync/LoadAsync` | Remote-first retrieval, last-known-good cache, size limits, ID validation, and content validation |
| Profile | `ProfileParser.Parse` | JSON, fields, commands, references, and version validation |
| Version | `VersionConstraint.Parse/IsSatisfiedBy` | Four expression forms and version comparison |
| Task DAG | `TaskGraph.Build` | Selection, dependency closure, deduplication, cycle detection, and topological sorting |
| Task Workflow | `ITaskWorkflowProvider`, `WorkflowActivity` | state validation, lifecycle Activities, transition selection, projection, and limits |
| Environment Run | `EnvironmentManager.StartApply` | DAG scheduling, blocking, cancellation, and reporting |
| Windows Runtime | `ITaskRuntime` | Safe argument passing, process output, process-tree cancellation, and version extraction |

Tests validate behavior exclusively through these seams.
