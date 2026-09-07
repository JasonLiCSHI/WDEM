# ADR-001: Clean Architecture dependency direction

- Status: Accepted
- Date: 2026-09-07

## Decision

Domain is the innermost layer and has no outward dependencies. Application depends only on Domain. Infrastructure and Windows are adapters for application-owned interfaces. WPF and CLI invoke application use cases. Dependency injection is configured only in a composition root.

The temporary `Wdem.Core` compatibility module was removed once every production caller used Domain and Application directly.

## Consequences

Architecture tests enforce inner-layer references, forbid peripheral APIs in inner layers, and prevent reintroducing the compatibility project.
