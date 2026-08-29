# Getting started with WDEM development

WDEM currently supplies the `Wdem.Core` and `Wdem.LegacySource` libraries and
their automated tests. It does **not** currently ship `Wdem.Cli`,
`Wdem.Desktop`, or a downloadable product executable.

## Prerequisites

Install the .NET 10 SDK. The transition library targets Windows, so enable
Windows targeting when developing from another operating system.

## Restore, build, and test

From the repository root:

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

Supported WDEM hosts take a profile path explicitly through `--profile` and
store product state under `%LOCALAPPDATA%\WDEM`. They do not use
`WDEM_CONFIG_PATH`, `WDEM_STATE_PATH`, or `WINHOME_STATE_PATH` as environment
overrides. `WINHOME_STATE_PATH` remains only an isolated transition-library
migration input; it is not part of the WDEM public contract.

For license attribution and the transition boundary, see
[THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md) and
[source provenance](wdem/source-provenance.md).
