# WDEM

[WDEM](https://github.com/JasonLiCSHI/WDEM) is an independent Windows
environment-management product under active development.

## Current status

The product boundary is being established around `Wdem.Core`. Source-derived
implementation remains available during the transition as the
`Wdem.LegacySource` library. WDEM does not publish `WinHome.exe` and does not
make a long-term compatibility commitment for the legacy command-line
interface.

## Build and test

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

## Configuration and state

WDEM configuration is represented by `config.yaml`. The WDEM default state
path is `wdem.state.json`; use the `WDEM_CONFIG_PATH` environment variable to
select a configuration file:

```powershell
$env:WDEM_CONFIG_PATH = 'C:\WDEM\config.yaml'
```

## Source provenance

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
[source provenance](docs/wdem/source-provenance.md) for the transitional
source boundary and license attribution.

## License

WDEM retains the [MIT License](LICENSE) required for source-derived material.
