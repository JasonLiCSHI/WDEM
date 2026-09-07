# ADR-005: Incremental migration without behavior drift

- Status: Accepted
- Date: 2026-09-07

## Decision

WDEM migrates by tested vertical slices. Existing Profile, DAG, workflow, trust, cancellation, CLI, WPF, and packaging behavior remains operational after every PR. The temporary compatibility project was removed after its final callers migrated.

## Consequences

Every production change starts with a failing behavioral or architecture test. Every PR runs the full test and build suite. New behavior belongs directly in Domain, Application, Infrastructure, or Windows according to the dependency rules.
