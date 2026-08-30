# WDEM

[WDEM](https://github.com/JasonLiCSHI/WDEM) is an independent Windows
developer-environment manager. It inspects a declarative profile, builds a
dependency-aware plan, and applies only the plan that the user confirms.

## Download and run

Each Windows x64 release has exactly three assets:

- `Wdem-win-x64.zip`, the complete self-contained product distribution;
- `SHA256SUMS.txt`, the archive checksum; and
- `THIRD-PARTY-NOTICES.md`, the source-attribution notice.

Verify the checksum before extracting the ZIP. Keep the complete extracted
`Cli`, `Desktop`, and `ElevatedHost` directories together: the unpackaged
WinUI 3 desktop host requires the Windows App SDK files beside its executable.
Start `Desktop\Wdem.Desktop.exe`, or use `Cli\Wdem.Cli.exe` for automation.

See the [WDEM getting-started guide](docs/wdem/getting-started.md) for exact
PowerShell commands.

## Supported command line

```text
wdem inspect --profile <file> [--select <resourceId> ...] [--json]
wdem apply --profile <file> [--select <resourceId> ...] [--max-concurrency 1..32] [--json]
wdem retry --run <guid> --resource <resourceId> ... [--json]
wdem resume --run <guid> [--json]
wdem runs list [--json]
```

`--json` writes newline-delimited JSON. Success exits `0`; host initialization
or output failure exits `1`, profile or plan validation exits `2`, execution
failure exits `3`, and cancellation exits `130`.

## Profiles, state, and safety

WDEM ships `profiles/csharp-developer.yaml` as its complete MVP profile.
Required resources are Visual Studio, the .NET SDK, and Git. Optional resources
are ReSharper, ReSharper settings, a company VSIX, and Visual Studio settings.
See [profile authoring](docs/wdem/profile-authoring.md) for the schema and
trusted-source requirements.

Execution snapshots and redacted logs live under `%LOCALAPPDATA%\WDEM\runs`.
Imported source-derived state is only migration history and never proof that a
current resource is compliant. See [recovery and security](docs/wdem/recovery-and-security.md).

## Build and test

```powershell
dotnet restore Wdem.sln -m:1 -p:EnableWindowsTargeting=true
dotnet format Wdem.sln --verify-no-changes --verbosity diagnostic --no-restore
dotnet build Wdem.sln --no-restore -m:1 -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal -m:1 -p:EnableWindowsTargeting=true
```

## License and provenance

WDEM retains the [MIT License](LICENSE) and explicit attribution required for
source-derived material. That material is not a separately supported product
or executable. See [third-party notices](THIRD-PARTY-NOTICES.md) and
[source provenance](docs/wdem/source-provenance.md).
