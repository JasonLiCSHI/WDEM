$ErrorActionPreference = "Stop"

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet build Wdem.sln -c Release --no-restore
    dotnet test Wdem.sln -c Release --no-build
}
finally {
    Pop-Location
}
