[CmdletBinding()]
param([switch]$Confirmed)

$ErrorActionPreference = 'Stop'
if (-not $Confirmed) {
    throw 'Refusing to apply outside an explicitly confirmed disposable VM.'
}

$os = Get-CimInstance Win32_OperatingSystem
if ($os.Caption -notmatch 'Windows 11' -or
    -not [Environment]::Is64BitOperatingSystem -or
    $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw 'This acceptance script requires a Windows 11 x64 VM.'
}

$productRoot = Join-Path $PSScriptRoot 'WDEM'
$cli = Join-Path $productRoot 'Cli\Wdem.Cli.exe'
$shippedProfiles = Join-Path $productRoot 'Desktop\profiles'
$expectedHosts = @(
    $cli,
    (Join-Path $productRoot 'Desktop\Wdem.Desktop.exe'),
    (Join-Path $productRoot 'ElevatedHost\Wdem.ElevatedHost.exe')
)
foreach ($hostPath in $expectedHosts) {
    if (-not (Test-Path $hostPath -PathType Leaf)) {
        throw "The extracted WDEM product release is incomplete: $hostPath"
    }
}
if (-not (Test-Path $shippedProfiles -PathType Container)) {
    throw "The extracted WDEM product release has no Desktop profiles directory: $shippedProfiles"
}
if (Get-ChildItem -LiteralPath $productRoot -Recurse -File -Filter 'WinHome.exe') {
    throw 'A retired executable was included in the WDEM release layout.'
}
if ([string]::IsNullOrWhiteSpace($env:WDEM_COMPANY_VSIX_PATH) -or
    [string]::IsNullOrWhiteSpace($env:WDEM_COMPANY_VSIX_SHA256)) {
    throw 'Set WDEM_COMPANY_VSIX_PATH and WDEM_COMPANY_VSIX_SHA256 to a trusted VSIX before the full clean-VM run.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("WDEM-clean-vm-work-{0}" -f [Guid]::NewGuid().ToString('N'))
$profiles = Join-Path $work 'profiles'
$profile = Join-Path $profiles 'csharp-developer.yaml'
$report = Join-Path $work 'apply-report.json'
New-Item -ItemType Directory -Force -Path $profiles | Out-Null
Copy-Item -Path (Join-Path $shippedProfiles '*') -Destination $profiles -Recurse -Force

Push-Location $work
try {
    & $cli apply --profile $profile --select resharper resharper-settings company-vs-extension visual-studio-settings --json --report $report
    $applyExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($applyExitCode -ne 0) {
    throw "WDEM apply failed with exit code $applyExitCode. Report/work directory: $work"
}
if (-not (Test-Path $report -PathType Leaf)) {
    throw "WDEM Apply did not create its JSON report. Work directory: $work"
}

$run = Get-Content $report -Raw | ConvertFrom-Json
if ($run.mode -ine 'apply') {
    throw "Expected Apply report mode, received '$($run.mode)'."
}
foreach ($resourceId in @('git', 'dotnet-sdk', 'visual-studio',
    'resharper', 'resharper-settings', 'company-vs-extension',
    'visual-studio-settings')) {
    $resource = $run.resourceResults.PSObject.Properties[$resourceId]
    if (-not $resource) {
        throw "Required resource result '$resourceId' was not reported."
    }
    if ($resource.Value.finalCompliance -ine 'satisfied') {
        throw "Required resource '$resourceId' did not reach final compliance."
    }
}

Write-Host "WDEM clean-VM Apply acceptance completed. Preserve evidence from: $work"
