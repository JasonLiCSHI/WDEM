# ADR-003: Workflow decision and Activity execution ownership

- Status: Accepted
- Date: 2026-09-07

## Decision

Domain workflow objects own states, transition rules, limits, and decisions. Application coordinates Entry, Residence, and Exit Activities through runtime ports. Runtime state remains the source of Task state and capability projections.

## Consequences

Domain performs no I/O. UI clients consume immutable projections and never interpret state identifiers or duplicate transition rules. Cancellation still terminates the active process tree and prevents unsafe transitions and downstream work.
