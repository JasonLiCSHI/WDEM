$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
    dotnet build Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-restore
    dotnet test Wdem.sln -c Release -p:EnableWindowsTargeting=true --no-build
    Write-Host "Validated Wdem.sln. Product binary publication remains disabled until Wdem.Cli and Wdem.Desktop exist."
}
finally {
    Pop-Location
}
