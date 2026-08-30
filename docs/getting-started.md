# Build WDEM from source

This page is for contributors. For the product distribution, see
[Get started with WDEM](wdem/getting-started.md).

`Wdem.Cli.exe` is the supported profile-driven CLI. `Wdem.Desktop.exe` is the
unpackaged WinUI 3 host, and `Wdem.ElevatedHost.exe` is its narrowly scoped UAC
broker.

## Prerequisites

Install the .NET 10 SDK. On non-Windows build agents, enable Windows targeting;
running the desktop or elevation host still requires Windows.

## Restore, format, build, and test

From the repository root:

```powershell
dotnet restore Wdem.sln -m:1 -p:EnableWindowsTargeting=true
dotnet format Wdem.sln --verify-no-changes --verbosity diagnostic --no-restore
dotnet build Wdem.sln --no-restore -m:1 -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal -m:1 -p:EnableWindowsTargeting=true
```

Do not run Apply on a development workstation. Use inspection/planning tests or
a disposable Windows VM. Product state is isolated under
`%LOCALAPPDATA%\WDEM`; profiles are passed explicitly with `--profile`.

For license attribution and the transition boundary, see
[third-party notices](../THIRD-PARTY-NOTICES.md) and
[source provenance](wdem/source-provenance.md).
