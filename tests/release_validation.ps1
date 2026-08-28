$ErrorActionPreference = "Stop"

function Invoke-NativeCommand {
    param(
        [string]$Description,
        [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    Invoke-NativeCommand "dotnet restore" { dotnet restore Wdem.sln -p:EnableWindowsTargeting=true }
    Invoke-NativeCommand "dotnet build" { dotnet build Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-restore }
    Invoke-NativeCommand "dotnet test" { dotnet test Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-build }
}
finally {
    Pop-Location
}
