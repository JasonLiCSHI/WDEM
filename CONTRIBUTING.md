# Contributing to WDEM

WDEM is an independent product. Keep product hosts, namespaces, state paths,
environment inputs, documentation, and automation WDEM-branded. Source-derived
code remains isolated in `Wdem.LegacySource` and must not become a public host
or release artifact.

## Build and test

```powershell
dotnet restore Wdem.sln -m:1 -p:EnableWindowsTargeting=true
dotnet format Wdem.sln --verify-no-changes --verbosity diagnostic --no-restore
dotnet build Wdem.sln --no-restore -m:1 -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal -m:1 -p:EnableWindowsTargeting=true
```

Do not run an Apply flow on a development machine. Use focused tests, inspection
and planning, or a disposable Windows VM for privileged end-to-end validation.

The solution contains `Wdem.Core`, `Wdem.Windows`, `Wdem.Cli`,
`Wdem.Desktop`, `Wdem.ElevatedHost`, the transition-source library, and their
test projects. New UI belongs in WinUI 3 and uses BCL-based MVVM; do not
introduce WPF, a third-party UI framework, or third-party MVVM infrastructure.

## Product contracts

- Ordinary product state belongs under `%LOCALAPPDATA%\WDEM`.
- `%ProgramData%\Wdem\PlanArtifacts` is the narrow security exception used only
  for ACL-restricted, cross-integrity handoff of verified VSIX plan artifacts
  and their revocation metadata.
- `%ProgramData%\Wdem\SecureArtifacts` is the narrow security exception used
  only for short-lived, ACL-restricted staging of verified executables, VSIX
  packages, and Visual Studio configuration files.
- Profiles are supplied explicitly through `--profile`.
- Product-specific profile variables use the `WDEM_` prefix; the shipped company
  VSIX uses `WDEM_COMPANY_VSIX_PATH` and `WDEM_COMPANY_VSIX_SHA256`.
- Never log secrets or raw command lines containing sensitive values.
- Any Apply change requires a fresh Detect/Plan cycle and explicit confirmation.
- Releases contain only the ZIP, its checksum file, and third-party notices.

Use focused commits and add a regression test before changing behavior. Pull
requests must link an approved issue and describe verification performed.

For the licensing boundary, see [third-party notices](THIRD-PARTY-NOTICES.md)
and [source provenance](docs/wdem/source-provenance.md).
