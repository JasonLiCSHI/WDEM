# ADR-004: One composition root

- Status: Accepted
- Date: 2026-09-07

## Decision

Dependency injection belongs only in `Wdem.Bootstrapper`. Domain entities and value objects are created explicitly and never resolved from a container. Application handlers are transient; infrastructure adapters are shared where safe; execution state is scoped to one execution.

Autofac will be introduced only after application ports and lifetimes are stable.
