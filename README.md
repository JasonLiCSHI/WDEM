# WDEM

[WDEM](https://github.com/JasonLiCSHI/WDEM) is an independent Windows
environment-management product under active development.

## Current status

`Wdem.Cli.exe` is the sole supported command-line surface for managing a
developer environment. It is profile-driven; transition source remains an
internal library and does not provide a supported executable or command
compatibility contract.

## Command line

Run the CLI from the build output, or use `dotnet run` while developing:

```powershell
dotnet run --project src/Wdem.Cli -- inspect --profile profiles/csharp-developer.yaml --json
dotnet run --project src/Wdem.Cli -- apply --profile profiles/csharp-developer.yaml --select resharper --max-concurrency 4
```

The supported grammar is:

```text
wdem inspect --profile <file> [--select <resourceId> ...] [--json]
wdem apply --profile <file> [--select <resourceId> ...] [--max-concurrency 1..32] [--json]
wdem retry --run <guid> --resource <resourceId> ... [--json]
wdem resume --run <guid> [--json]
wdem runs list [--json]
```

`--json` writes newline-delimited JSON. A successful run exits with `0`;
profile or plan validation errors use `2`, execution failures use `3`, and
cancellation uses `130`.

## Build and test

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

## Configuration and state

WDEM configuration is represented by `config.yaml`. The WDEM default state
location is `%LOCALAPPDATA%\WDEM\.wdem-state.json`; use
`WDEM_CONFIG_PATH` and `WDEM_STATE_PATH` to select configuration and state
paths:

```powershell
$env:WDEM_CONFIG_PATH = 'C:\WDEM\config.yaml'
$env:WDEM_STATE_PATH = 'C:\WDEM\.wdem-state.json'
```

`WINHOME_STATE_PATH` is a deliberate one-time migration input for legacy
state only; legacy `%LOCALAPPDATA%\WinHome` state is also read once and moved
aside. Neither is a WDEM public interface.

## Source provenance

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
[source provenance](docs/wdem/source-provenance.md) for the transitional
source boundary and license attribution.

## License

WDEM retains the [MIT License](LICENSE) required for source-derived material.
