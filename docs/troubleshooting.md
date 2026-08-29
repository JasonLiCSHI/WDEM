# Troubleshooting WDEM development

WDEM does not yet ship a public executable. For development failures, first
run the supported solution validation:

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
