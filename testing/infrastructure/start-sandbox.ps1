$ErrorActionPreference = "Stop"

Push-Location (Resolve-Path (Join-Path $PSScriptRoot "..\.."))
try {
    dotnet build Wdem.sln -c Release --no-restore
    dotnet test Wdem.sln -c Release --no-build
    Write-Host "Validated WDEM transition libraries. Sandbox execution is disabled until product hosts exist."
}
finally {
    Pop-Location
}
