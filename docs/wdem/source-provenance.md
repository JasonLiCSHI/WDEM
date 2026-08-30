# WDEM source provenance

WDEM is an independent product repository. Its transitional
`Wdem.LegacySource` library contains source-derived material from
[DotDev262/WinHome](https://github.com/DotDev262/WinHome), copyright (c) 2025
Aryan Madhusudhanan, under the MIT License.

The repository retains `LICENSE` and `THIRD-PARTY-NOTICES.md` for that source
provenance. The `winhome-source` and `winhome-fork` Git remotes are fetch-only
and may be used to inspect provenance only: their push URLs are `DISABLED`.
WDEM is an independent private repository, not a branch, pull request, or
merge target of either WinHome repository.

The upstream repositories and their executables are not supported WDEM
products, release channels, or compatibility targets.

`Wdem.Core` is the WDEM-owned core. `Wdem.LegacySource` is a transitional
library boundary, not a WDEM command-line product or compatibility promise.
WDEM releases, branding, namespaces, state paths, environment variables,
documentation, CI configuration, solution names, and project names are
independent of the provenance sources.

## One-time state import

For migration only, the product may read old state once from
`%LOCALAPPDATA%\WinHome` when `%LOCALAPPDATA%\WDEM\migration-v1.json` does not
exist. It writes the marker atomically under the WDEM directory, never writes
to the old directory, and does not read the old directory again after the
marker exists. Imported step names are non-authoritative history: every WDEM
operation still performs a fresh Detect and Plan.
