# DDD and Clean Architecture refactoring blueprint

## Objective

WDEM converges a Windows machine from observed state to a trusted Profile's desired state. The architecture therefore separates three concepts:

```text
Profile (desired state) → Plan (decision) → Execution (observed history)
```

The refactoring preserves the current Profile, DAG, workflow, cancellation, trust, CLI, WPF, and packaging behavior while moving responsibilities behind explicit domain and application interfaces. It is an incremental migration, not a rewrite.

## Target dependency direction

```text
WPF / CLI → Bootstrapper → Application → Domain
                         ↗             ↗
       Infrastructure / Windows adapters
```

- `Wdem.Domain` has no project or package dependencies and performs no I/O.
- `Wdem.Application` depends only on `Wdem.Domain` and owns use-case orchestration and ports.
- `Wdem.Infrastructure` implements remote Profile, JSON, cache, trust, and journal ports.
- `Wdem.Windows` implements process, elevation, and process-tree cancellation ports.
- `Wdem.Bootstrapper` is the only composition root and uses Autofac behind a narrow session boundary; clients and inner layers never receive the container.
- `Wdem.App` and `Wdem.Cli` consume application use cases and read models; neither recreates domain rules.

During migration, `Wdem.Core` remains a compatibility module. It must shrink in every migration PR and is removed only after all callers have moved.

## Domain model

| Capability | Model | Invariants |
|---|---|---|
| Profiles | `Profile`, `ProfileId`, `ProfileVersion`, `TaskDefinition` | Valid identity, immutable desired state, valid task references |
| Versions | `SoftwareVersion`, `VersionRequirement`, `ComplianceResult` | Exact, wildcard, minimum, and range semantics live in the domain |
| Planning | `Plan`, `PlannedTask`, `PlanAction`, `TaskPlanner` | Dependency closure, cycle rejection, deterministic ordering, blocked decisions |
| Execution | `TaskExecution`, `ExecutionId`, `ExecutionState`, `ExecutionFailure` | Definition and runtime instance are distinct |
| Workflows | `TaskWorkflow`, `WorkflowState`, `WorkflowTransition`, `WorkflowDecision` | Bounded transitions; state decides, application executes Activities |

## Application use cases and ports

Vertical slices are introduced only where the client has a real user intent:

- Profiles: list, load, and trust a Profile.
- Inspection: inspect the current environment.
- Planning: create and preview a Plan.
- Execution: apply a Plan, start one Task with dependencies, cancel, and retry.
- Queries: obtain immutable Plan, Execution, and Workflow snapshots.

Ports are owned by the use case that needs them. Initial ports are `IProfileRepository`, `IEnvironmentInspector`, `ITaskRuntime`, `IExecutionJournal`, `IContentTrustStore`, and `IClock`. A port is introduced only when an adapter actually varies or isolates I/O.

## Migration matrix

| Current source | Destination | Action |
|---|---|---|
| `Core/Versions/VersionConstraint.cs` | `Domain/Versions/VersionRequirement.cs`, `SoftwareVersion.cs` | Split parsing/comparison from compliance result |
| ~~`Core/Tasks/TaskDefinition.cs`~~ | `Domain/Tasks/TaskDefinition.cs` | Migrated immutable desired-state definition; it references only pure Domain workflow definitions |
| ~~`Core/Tasks/CommandDefinition.cs`~~ | `Domain/Tasks/CommandDefinition.cs` | Migrated; keeps the executable plus argument-array invariant |
| ~~`Core/Profiles/EnvironmentProfile.cs`~~ | `Domain/Profiles/EnvironmentProfile.cs` | Migrated immutable Profile aggregate; a later vocabulary-only rename is optional |
| ~~`Core/Profiles/ProfileSourceDefinition.cs`~~ | `Domain/Profiles/ProfileSourceDefinition.cs` | Migrated validated identity and URI value semantics |
| ~~`Core/Profiles/ProfileCatalogEntry.cs`~~ | `Application/Profiles/ProfileCatalogEntry.cs` | Migrated catalog projection out of Domain |
| ~~`Core/Profiles/LoadedProfile.cs`~~ | `Application/Profiles/LoadedProfile.cs` | Migrated origin/hash load result |
| ~~`Core/Profiles/ProfileOrigin.cs`~~ | `Application/Profiles/ProfileOrigin.cs` | Migrated transport origin outside Domain |
| ~~`Core/Profiles/ProfileParser.cs`~~ | `Infrastructure/Profiles/ProfileParser.cs` | Migrated JSON adapter; deserializer/validator/mapper decomposition remains internal follow-up work |
| ~~`Core/Profiles/ProfileCatalog.cs`~~ | `Infrastructure/Profiles/ProfileCatalog.cs` | Migrated implementation behind Application `IProfileRepository` |
| ~~`Core/Graph/TaskGraph.cs`~~ | `Domain/Planning/TaskPlanner.cs` | Migrated: public graph replaced by immutable Plan |
| ~~`Core/Runs/EnvironmentInspector.cs`~~ | `Application/Inspection/InspectEnvironmentHandler.cs` | Migrated use-case handler over the runtime port |
| `Core/Runs/EnvironmentManager.cs` | `Application/Execution` | Replace facade with explicit apply/start handlers |
| `Core/Runs/EnvironmentRun.cs` | `Application/Execution` | Rename to execution handle |
| ~~`Core/Runs/InspectReport.cs`~~ | `Application/Inspection/InspectReport.cs` | Migrated use-case result/read model |
| `Core/Runs/RunReport.cs`, `StepReport.cs`, `TaskReport.cs` | `Domain/Execution` | Model immutable execution history |
| ~~`Core/Runs/TaskComplianceEvaluator.cs`, `TaskComplianceState.cs`~~ | `Application/Inspection`, `Domain/Versions` | Migrated command-output interpretation and compliance rules |
| ~~`Core/Runs/TaskExecutionState.cs`, `TaskOutcome.cs`~~ | `Domain/Execution` | Migrated stable lifecycle vocabulary |
| `Core/Runs/TaskCapabilities.cs` | `Application/Queries` | Project capabilities from domain state |
| ~~`Core/Runs/TaskInspection.cs`~~ | `Application/Inspection/TaskInspection.cs` | Migrated inspection read model |
| `Core/Runs/WorkflowStateMachine.cs` | `Application/Execution` | Coordinate Activities using domain decisions |
| `Core/Runs/WorkflowStateStore.cs` | `Application/Queries` | Project serialized immutable snapshots |
| `Core/Runs/Workflow*.cs` snapshot files | `Application/Queries` | Preserve client-facing read models |
| ~~`Core/Runs/WorkflowProgress.cs`~~ | `Application/Execution/WorkflowProgress.cs` | Migrated shared runtime progress contract |
| ~~`Core/Runtime/*.cs`~~ | `Application/Runtime` | Migrated; Application owns the runtime port and transport results |
| ~~`Core/Workflows/TaskWorkflow*.cs`~~ | `Domain/Workflows` | Migrated state graph definitions, validation, transition facts, and decisions |
| ~~`Core/Workflows/WorkflowActivity*.cs`~~ | split Domain/Application | Migrated: Domain keeps Activity definitions; Application executes them through a port |
| `Core/Workflows/DefaultTaskWorkflowProvider.cs` | `Application/Execution` | Compile Schema v1 into the domain workflow |
| `Windows/Configuration/*` | `Infrastructure/Configuration` | Implement settings/trust persistence ports |
| `Windows/Logging/*` | `Infrastructure/Logging` | Implement execution journal port |
| `Windows/Processes/*`, `Windows/Runtime/*`, `Windows/Security/*` | `Windows` | Retain Windows-specific adapters |
| `App/MainWindow.xaml.cs` | WPF vertical presentation slices | Replace orchestration with application handlers |
| `App/TaskRow.cs`, `WorkspaceActionState.cs` | WPF projections | Bind only to application snapshots/capabilities |
| `Cli/Program.cs` | CLI commands | Replace orchestration with application handlers |

## Pull-request sequence

1. Architecture baseline: decisions, dependency tests, and inner-layer project anchors.
2. Domain extraction: versions, compliance, Task identity, and Profile identity.
3. Planning: immutable `Plan` and `TaskPlanner`; remove `TaskGraph` from client interfaces.
4. Application: inspection and execution use cases plus runtime ports; migrate CLI/WPF.
5. Adapters and composition: isolate Profile I/O, Windows runtime, logging, trust, and composition.
6. Cleanup and release: remove `Wdem.Core`, update documentation, validate installer contents, and publish `0.1.2`.

Each PR must preserve executable behavior, pass the full solution, and leave `main` releasable.
