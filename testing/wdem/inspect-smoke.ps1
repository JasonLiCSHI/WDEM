$ErrorActionPreference = 'Stop'

function Get-TextSha256([string[]]$Lines) {
    $text = $Lines -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-RegistryFingerprint {
    $scopes = @(
        'HKCU\Environment',
        'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
        'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    $lines = foreach ($scope in $scopes) {
        $output = @(& reg.exe query $scope /s 2>&1 | ForEach-Object { $_.ToString() })
        "SCOPE=$scope;EXIT=$LASTEXITCODE"
        $output
    }
    return Get-TextSha256 $lines
}

function Get-PersistentEnvironmentFingerprint {
    $lines = foreach ($target in @('User', 'Machine')) {
        $variables = [Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::$target)
        foreach ($name in @($variables.Keys | ForEach-Object { $_.ToString() } | Sort-Object)) {
            "$target|$name=$($variables[$name])"
        }
    }
    return Get-TextSha256 $lines
}

$root = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("wdem-inspect-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
$report = Join-Path $tempRoot 'inspect-report.json'
$originalVsixPath = $env:WDEM_COMPANY_VSIX_PATH
$originalVsixSha256 = $env:WDEM_COMPANY_VSIX_SHA256
$hadVsixPath = Test-Path Env:WDEM_COMPANY_VSIX_PATH
$hadVsixSha256 = Test-Path Env:WDEM_COMPANY_VSIX_SHA256

$registryBefore = Get-RegistryFingerprint
$environmentBefore = Get-PersistentEnvironmentFingerprint
$bootBefore = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Remove-Item $report -ErrorAction SilentlyContinue
    Remove-Item Env:WDEM_COMPANY_VSIX_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:WDEM_COMPANY_VSIX_SHA256 -ErrorAction SilentlyContinue

    Push-Location $root
    try {
        & dotnet run --project src\Wdem.Cli\Wdem.Cli.csproj -p:BuildInParallel=false -- inspect --profile profiles\csharp-developer.yaml --json --report $report
        $inspectExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($inspectExitCode -ne 0) {
        throw "WDEM inspect failed with exit code $inspectExitCode."
    }
    if (-not (Test-Path $report -PathType Leaf)) {
        throw 'WDEM Inspect did not create its JSON report.'
    }

    $rawReport = Get-Content $report -Raw
    $run = $rawReport | ConvertFrom-Json
    if ($run.mode -ine 'inspect') {
        throw "Expected Inspect report mode, received '$($run.mode)'."
    }
    if (-not $run.resourceResults.PSObject.Properties['git']) {
        throw 'Git result was not reported.'
    }
    if ($rawReport -match '(?i)authorization\s*:\s*bearer|password\s*=|token\s*=') {
        throw 'Inspect report is not redacted.'
    }

    foreach ($property in $run.resourceResults.PSObject.Properties) {
        $result = $property.Value
        if (@($result.stepResults).Count -ne 0) {
            throw "Inspect executed a resource step for '$($property.Name)'."
        }
        if ($null -ne $result.detectedAfter) {
            throw "Inspect performed post-Apply detection for '$($property.Name)'."
        }
        if ($null -ne $result.restartRequirement -and $result.restartRequirement -ine 'noRestart') {
            throw "Inspect reported a restart for '$($property.Name)'."
        }
    }
    if (@($run.restartRequirements).Count -ne 0 -or @($run.restartReasons).Count -ne 0) {
        throw 'Inspect reported a restart operation.'
    }

    $retiredRuns = Join-Path $env:LOCALAPPDATA 'WinHome\Wdem\runs'
    if (Test-Path $retiredRuns) {
        throw 'WDEM wrote the retired state path.'
    }
    if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'WDEM\runs') -PathType Container)) {
        throw 'WDEM did not persist Inspect state below %LOCALAPPDATA%\WDEM.'
    }

    $registryAfter = Get-RegistryFingerprint
    $environmentAfter = Get-PersistentEnvironmentFingerprint
    $bootAfter = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    if ($registryAfter -ne $registryBefore) {
        throw 'Inspect changed a persistent Registry installation or Environment scope.'
    }
    if ($environmentAfter -ne $environmentBefore) {
        throw 'Inspect changed a persistent Environment value.'
    }
    if ($bootAfter -ne $bootBefore) {
        throw 'Inspect crossed a machine restart boundary.'
    }

    Write-Host 'WDEM Inspect smoke passed without Apply-side effects.'
}
finally {
    if ($hadVsixPath) {
        $env:WDEM_COMPANY_VSIX_PATH = $originalVsixPath
    }
    else {
        Remove-Item Env:WDEM_COMPANY_VSIX_PATH -ErrorAction SilentlyContinue
    }
    if ($hadVsixSha256) {
        $env:WDEM_COMPANY_VSIX_SHA256 = $originalVsixSha256
    }
    else {
        Remove-Item Env:WDEM_COMPANY_VSIX_SHA256 -ErrorAction SilentlyContinue
    }
    if (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
