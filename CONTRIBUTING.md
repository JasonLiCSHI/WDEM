# Contributing to WDEM

WDEM is establishing its product boundary around `Wdem.Core`. The repository
currently builds transition libraries and tests; do not add a product host as
part of this work unless explicitly scoped.

## Build and test

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
dotnet format Wdem.sln
```

The current projects are:

- `src\Wdem.Core`
- `src\Wdem.LegacySource`
- `tests\Wdem.Core.Tests`
- `tests\Wdem.LegacySource.Tests`

Use focused commits and add tests for changes in either library. Configuration
and state use the WDEM contract: `%LOCALAPPDATA%\WDEM`,
`WDEM_CONFIG_PATH`, `WDEM_STATE_PATH`, and `.wdem-state.json`. Legacy values
are migration fallbacks only.

For source attribution and the transition boundary, see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
[source provenance](docs/wdem/source-provenance.md).
