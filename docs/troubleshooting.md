# Troubleshooting WDEM development

WDEM does not yet ship a public executable. For development failures, first
run the supported solution validation:

```powershell
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

Use `%LOCALAPPDATA%\WDEM`, `WDEM_CONFIG_PATH`, and `WDEM_STATE_PATH` for
development of transition-library behavior. Old `%LOCALAPPDATA%\WinHome`
state is read once and moved aside; `WINHOME_STATE_PATH` is the only
deliberate legacy migration input. Neither is a supported WDEM interface.
