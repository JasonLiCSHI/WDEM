> **Development status:** WDEM currently provides transition libraries and automated unit tests only. `Wdem.Cli` and `Wdem.Desktop` have not been implemented, so no supported product, sandbox, container, or release integration workflow exists. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).

# Testing Guide

## Available validation: .NET unit tests

Run the solution's automated unit tests from the repository root:

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-build
```

These tests have no intended system side effects and are the only supported Task1 validation.

## Unavailable integration entry points

The historical scripts below intentionally report that integration testing is unavailable and exit non-zero. They must not be interpreted as passing integration checks:

- `test-data/run-test.ps1`
- `test-data/run-test-full.ps1`
- `test-data/run-test-gha.ps1`
- `test-data/run-test-container.ps1`
- `testing/infrastructure/start-sandbox.ps1`
- `testing/infrastructure/run-sandbox-test.ps1`
- `testing/infrastructure/run-sandbox-plugins.ps1`

They will be replaced when `Wdem.Cli` can apply a configuration in an isolated environment and the resulting state can be verified. Until then, do not run the Pester files in `test-data/` against a development machine: they describe product integration behavior that Task1 cannot execute.

## CI

CI runs the solution's direct `dotnet restore`, `dotnet build`, and `dotnet test` commands. It does not invoke the unavailable integration entry points.
