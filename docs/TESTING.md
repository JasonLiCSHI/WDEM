# Testing strategy

WDEM keeps tests close to the architectural boundary that owns each behavior. The suite is intentionally shaped as a test pyramid rather than a collection of end-to-end installer tests.

## Pyramid

1. **Domain unit tests** form the broad base. They cover version rules, Task DAG planning, immutable Profile state, workflow definitions, and transition decisions without I/O.
2. **Application unit tests** cover inspection, plan creation, workflow scheduling, state projection, cancellation, and failure propagation through deterministic fakes.
3. **Adapter contract tests** cover Profile parsing/cache behavior, JSONL logs, Autofac composition, Windows argument expansion, PowerShell task scripts, and real process-tree cancellation. Network and real product installation are replaced with controlled local boundaries.
4. **WPF smoke tests** remain deliberately thin and verify bindings, resources, and rendering on an STA thread. Full Visual Studio and ReSharper installation is release acceptance work on a disposable Windows VM, not a routine unit-test concern.

Architecture tests enforce dependency direction, Autofac ownership, centrally managed package versions, and .NET 10 for every production and test project.

## Test design

- Prefer `Subject_WhenCondition_ThenOutcome`. For a simple invariant with no meaningful trigger, use `Subject_ExpectedOutcome`; the test class or fixture supplies the Given context.
- Keep Given, When, and Then visibly separated in the test body. Prefer one behavior and one reason to fail per test.
- Assert observable outcomes. A test without a meaningful assertion, a tautological assertion, or a test written only to touch lines is not accepted.
- Use small fakes at owned ports such as `ITaskRuntime`; do not mock Domain objects or private implementation details.
- Prefer data-driven tests for equivalent input partitions. Keep distinct boundary cases separate when their failure meaning differs.
- Avoid shared mutable state, wall-clock sleeps, real network access, and machine installation changes. Synchronize concurrency tests with `TaskCompletionSource` or another deterministic signal.
- Avoid abstract test base classes. Use focused builders and local helpers; introduce a shared fixture only when it owns a real shared resource.
- Setup must create only state required by most tests in the class. Teardown must be idempotent and release processes, streams, temporary files, and directories even when an assertion fails.

## Commands

Run the same gates used by CI:

```powershell
dotnet restore Wdem.slnx
dotnet build Wdem.slnx --configuration Release --no-restore
dotnet test Wdem.slnx --configuration Release --no-build
dotnet format Wdem.slnx --verify-no-changes --no-restore
```

Task tests must use fake executables or fake runtimes and must never install or modify Visual Studio, ReSharper, or other user software.
