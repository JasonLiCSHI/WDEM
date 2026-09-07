# ADR-004: One composition root

- Status: Accepted
- Date: 2026-09-07

## Decision

Dependency injection belongs only in `Wdem.Bootstrapper`. Domain entities and value objects are created explicitly and never resolved from a container. Stateless Application handlers and infrastructure adapters are shared within a process session; execution state is created fresh for one execution.

`Wdem.Bootstrapper` uses Autofac and exposes a narrow, disposable `WdemSession`; it never exposes `IContainer` or arbitrary resolution to a client. The session exposes typed planning, inspection, and execution handlers plus Profile, trust, and logging services. Runtime adapters remain internal to the composition graph. Workflow and Task execution state stays outside the process-level container so a retry always creates fresh execution state.

Autofac must not spread into Domain, Application, Infrastructure, Windows, CLI, or WPF code.
