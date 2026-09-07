# ADR-002: Profile, Plan, and Execution are distinct

- Status: Accepted
- Date: 2026-09-07

## Decision

A Profile is immutable desired state. A Plan is an immutable decision derived from a Profile, Task selection, dependencies, and environment observations. An Execution is one runtime attempt and its history.

## Consequences

Clients preview and apply Plans rather than raw graphs. Retries create fresh Executions and workflows. Reports and snapshots project Execution state and do not replace it.
