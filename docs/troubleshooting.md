# Troubleshooting WDEM development

`Wdem.Cli.exe` is the sole supported profile-driven CLI, and `Wdem.Desktop` is
the WinUI host. Task 22 will provide a self-contained ZIP for end-user
distribution. For development failures, first run the supported solution
validation:

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

Supported WDEM hosts use an explicit `--profile` path and isolated state under
`%LOCALAPPDATA%\WDEM`. They intentionally ignore `WDEM_CONFIG_PATH`,
`WDEM_STATE_PATH`, and `WINHOME_STATE_PATH` as environment overrides.
`WINHOME_STATE_PATH` exists only as an isolated transition-library migration
input and is not a supported WDEM interface.
