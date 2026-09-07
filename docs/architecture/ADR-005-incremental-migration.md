# ADR-005: Incremental migration without behavior drift

- Status: Accepted
- Date: 2026-09-07

## Decision

WDEM will migrate by tested vertical slices. Existing Profile, DAG, workflow, trust, cancellation, CLI, WPF, and packaging behavior remains operational after every PR. Compatibility adapters may exist temporarily, but new domain rules are not added to `Wdem.Core`.

## Consequences

Every production change starts with a failing behavioral or architecture test. Every PR runs the full test and build suite. `Wdem.Core` is removed only when no production project references it.
