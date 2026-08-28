# WDEM Provider SDK

Providers translate a declarative `ResourceDefinition` into safe, auditable work. They are UI-independent and are resolved by the case-insensitive pair `(ResourceType, ProviderName)`.

## Lifecycle contract

Every provider implements the complete lifecycle below:

```csharp
ValueTask<ProviderValidationResult> ValidateAsync(
    ResourceDefinition resource,
    CancellationToken cancellationToken);

ValueTask<DetectedState> DetectAsync(
    ResourceDefinition resource,
    CancellationToken cancellationToken);

ValueTask<ResourcePlan> PlanAsync(
    ResourceDefinition resource,
    DetectedState currentState,
    CancellationToken cancellationToken);

ValueTask<ResourceApplyResult> ApplyAsync(
    ResourceDefinition resource,
    ResourcePlan plan,
    IProgress<ProviderProgress>? progress,
    CancellationToken cancellationToken);

ValueTask<VerificationResult> VerifyAsync(
    ResourceDefinition resource,
    CancellationToken cancellationToken);
```

The host calls these operations in order:

1. `ValidateAsync` rejects unknown, missing, unsafe, or unsupported parameters before any machine access.
2. `DetectAsync` observes the machine without modifying it. A missing executable or package is a successful detection with `Exists == false`; inability to inspect is `Failed`, and an unsupported platform or resource is `Unsupported`. Cancellation requested through the supplied token propagates as `OperationCanceledException`.
3. The host performs centralized compliance evaluation. Providers supply evidence; an installer exit code never proves compliance.
4. `PlanAsync` creates deterministic steps from the desired resource and detected state. It must not modify the machine. Plans retain `ResourceId`, `ResourceType`, `ProviderName`, and `DesiredStateFingerprint` so stale or substituted plans can be rejected.
5. `ApplyAsync` executes only an executable, matching plan. It reports each completed or failed operation through `ProviderStepResult` and observes the cancellation token.
6. `VerifyAsync` detects again after apply. Apply success is provisional; only verification with `ComplianceStatus.Satisfied` completes the resource successfully.

Inspect mode stops after planning and must never call `ApplyAsync`.

## Detection and compliance evidence

`DetectedState` records the observation time, existence, a compatibility `Version`, zero or more parsed `InstalledVersions`, configuration hash, evidence, and structured failure. For multi-version tools such as the .NET SDK, any installed version satisfying the desired constraint is sufficient. Unparseable detected versions never satisfy a version constraint.

Central compliance uses this closed vocabulary:

- `Satisfied`
- `Missing`
- `VersionMismatch`
- `ConfigurationMismatch`
- `DetectionFailed`
- `Unsupported`

Only `Missing`, `VersionMismatch`, and `ConfigurationMismatch` may produce modifying steps. Detection failure and cancellation are never treated as missing or satisfied.

## Capabilities and concurrency

`ProviderCapabilities` declares support for sources, version constraints, installer parameters, and in-progress cancellation. `MaxConcurrentOperations` defaults to one. `ConcurrencyGroup` lets providers that share an underlying installer serialize their work; when absent, the scheduler derives a group from resource type and provider name.

A provider must reject requested behavior that its capabilities do not support. Capability flags describe real behavior—they are not feature requests or optimistic hints. Registration rejects blank resource/provider identities, null capabilities, and `MaxConcurrentOperations` values less than one.

## Progress, logs, and errors

`ProviderProgress` contains a lifecycle stage, a normalized percent in `[0, 1]`, a safe summary message, an optional step ID, and `ProviderLogLevel`. Providers should use stable step IDs from the plan and emit enough progress to connect logs with a resource operation.

Provider-facing failures must include `StructuredError`. The diagnostic identifies a `WdemErrorCode`, summary, sanitized detail, resource and optional step, process exit code when available, retryability, log location, and suggested action. Never put credentials, access tokens, authorization headers, secrets, or user-specific paths into messages. Do not expose raw exception text directly; attach the exception to `UnderlyingException` so WDEM records sanitized metadata.

Provider model collections are immutable snapshots: callers may not mutate a model through a retained source collection or a concrete collection interface. Null collections and null collection entries violate the provider contract and are rejected.

`ResourceApplyResult.Diagnostics` contains sanitized, non-fatal apply diagnostics that do not change the operation outcome. For example, the legacy adapter records one bounded diagnostic and detaches a failing progress observer while allowing the installation itself to continue.

Compatibility string fields (`DetectedState.Error`, `ProviderValidationResult.Errors`, and `ResourcePlan.Error`) remain available during migration. New providers should populate structured diagnostics; `ProviderValidationResult.IsValid` is false when either error collection is non-empty.

Cancellation requested by the supplied token propagates as `OperationCanceledException` from validation, detection, and planning. `DetectionOutcome.Cancelled` is reserved for providers that are translating an already materialized, persisted, or externally reported state; centralized compliance maps that outcome to a detection failure with cancellation diagnostics. Apply operations that have begun may return `ApplyOutcome.Cancelled` with a `CancellationError` and per-step error metadata when they can safely translate the cancellation.

## Legacy package adapter

`LegacyPackageManagerProviderAdapter` is a transition boundary for existing package managers. It supports package existence detection and installation, supplies structured metadata, and rejects stale or substituted plans. It intentionally rejects version-constrained resources and does not claim to enforce package versions. Product-specific providers provide version-aware behavior.
