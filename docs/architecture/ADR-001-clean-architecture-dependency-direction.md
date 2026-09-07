# ADR-001: Clean Architecture dependency direction

- Status: Accepted
- Date: 2026-09-07

## Decision

Domain is the innermost layer and has no outward dependencies. Application depends only on Domain. Infrastructure and Windows are adapters for application-owned interfaces. WPF and CLI invoke application use cases. Dependency injection is configured only in a composition root.

`Wdem.Core` is a temporary compatibility module during incremental migration, not a permanent layer.

## Consequences

Architecture tests enforce the inner-layer references and forbidden peripheral APIs. Each migration PR must reduce, never expand, the responsibility of `Wdem.Core`.
