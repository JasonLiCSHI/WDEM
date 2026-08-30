# Troubleshoot WDEM

## The desktop executable does not start

Extract the whole `Wdem-win-x64.zip` again. Do not move
`Desktop\Wdem.Desktop.exe` away from its Windows App SDK and runtime companion
files. Confirm the archive hash against `SHA256SUMS.txt` before retrying.

## A profile is rejected

From the extracted `Desktop` directory, run
`..\Cli\Wdem.Cli.exe inspect --profile <path> --json` and use the JSON pointer
in the structured error. Running from `Desktop` preserves the shipped
application root used to resolve relative profile assets. Check the schema
version, resource/provider pair, dependency IDs, version syntax, source path,
and 64-hex SHA-256 fields. See [profile authoring](wdem/profile-authoring.md).

## Apply reports a stale plan

The machine, selected Visual Studio instance, source artifact, or destination
changed after planning. This is a safety failure. Run a fresh Detect, review a
new Plan, and confirm it; do not edit the snapshot to bypass the check.

## A run was cancelled, interrupted, or requires restart

From the same `Desktop` directory, use `..\Cli\Wdem.Cli.exe runs list`, then
inspect the relevant run under
`%LOCALAPPDATA%\WDEM\runs`. Resume or retry only through the supported CLI or
desktop flow. WDEM may finish an atomic finalization after cancellation. See
[recovery and security](wdem/recovery-and-security.md).

## Contributor validation

```powershell
dotnet restore Wdem.sln -m:1 -p:EnableWindowsTargeting=true
dotnet build Wdem.sln --no-restore -m:1 -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal -m:1 -p:EnableWindowsTargeting=true
```
