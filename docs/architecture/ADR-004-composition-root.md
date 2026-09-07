# ADR-004: One composition root

- Status: Accepted
- Date: 2026-09-07

## Decision

Dependency injection belongs only in `Wdem.Bootstrapper`. Domain entities and value objects are created explicitly and never resolved from a container. Application handlers are transient; infrastructure adapters are shared where safe; execution state is scoped to one execution.

`Wdem.Bootstrapper` uses Autofac and exposes a narrow, disposable `WdemSession`; it never exposes `IContainer` or arbitrary resolution to a client. Process-level settings, Profile Catalog, Task Runtime, and session log instances are shared within one session and isolated across sessions. Workflow and Task execution state remains outside the process-level container so a retry always creates fresh execution state.

During the incremental migration, the Bootstrapper composes transitional Core services as well as Windows adapters. Application handlers replace those registrations as their ports stabilize; Autofac must not spread into Domain, Application, CLI, or WPF code.
