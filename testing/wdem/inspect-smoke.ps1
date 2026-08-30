$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

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

function ConvertTo-FingerprintField([AllowNull()][string]$Value) {
    if ($null -eq $Value) {
        return '-'
    }
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-AclFingerprint([string]$Path) {
    # SACL/Audit is deliberately excluded because reading it requires elevated privileges.
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    return $acl.GetSecurityDescriptorSddlForm($sections)
}

function Get-FileContentFingerprint([string]$Path) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $sharing = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            $sharing)
        return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        $sha256.Dispose()
    }
}

function Get-NamedStreamContentFingerprint([string]$Path, [string]$StreamName) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        Get-Content -LiteralPath $Path -Stream $StreamName -Encoding Byte -ReadCount 65536 -ErrorAction Stop |
            ForEach-Object {
                [byte[]]$buffer = $_
                [void]$sha256.TransformBlock($buffer, 0, $buffer.Length, $buffer, 0)
            }
        [void]$sha256.TransformFinalBlock([byte[]]@(), 0, 0)
        return [BitConverter]::ToString($sha256.Hash).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-FileStreamsFingerprint([string]$Path) {
    $streamsBefore = @(Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop)
    $metadataBefore = @(
        $streamsBefore |
            ForEach-Object { "$(ConvertTo-FingerprintField $_.Stream)|$($_.Length)" } |
            Sort-Object)
    $records = New-Object 'System.Collections.Generic.List[string]'
    foreach ($stream in $streamsBefore) {
        $streamName = [string]$stream.Stream
        $contentHash = if ($streamName -eq ':$DATA') {
            Get-FileContentFingerprint $Path
        }
        else {
            Get-NamedStreamContentFingerprint $Path $streamName
        }
        $records.Add("$(ConvertTo-FingerprintField $streamName)|$($stream.Length)|$contentHash")
    }

    $streamsAfter = @(Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop)
    $metadataAfter = @(
        $streamsAfter |
            ForEach-Object { "$(ConvertTo-FingerprintField $_.Stream)|$($_.Length)" } |
            Sort-Object)
    if (($metadataBefore -join "`n") -ne ($metadataAfter -join "`n")) {
        throw "Retired state file streams changed while they were being fingerprinted: $Path"
    }
    return @($records | Sort-Object) -join ';'
}

function Get-LegacyTreeFingerprintOnce([string]$Path) {
    $fullRoot = [IO.Path]::GetFullPath($Path)
    try {
        $rootAttributes = [IO.File]::GetAttributes($fullRoot)
    }
    catch [IO.FileNotFoundException] {
        return 'ABSENT'
    }
    catch [IO.DirectoryNotFoundException] {
        return 'ABSENT'
    }

    if (($rootAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The retired state root is a reparse point and cannot be monitored safely.'
    }

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $records = New-Object 'System.Collections.Generic.List[string]'
    $pending.Push($fullRoot)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($current)
        $relative = if ($current -eq $fullRoot) {
            '.'
        }
        else {
            $current.Substring($fullRoot.Length).TrimStart([char[]]@('\', '/'))
        }
        $encodedPath = ConvertTo-FingerprintField $relative
        $isReparsePoint = ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        if ($isReparsePoint) {
            $itemBefore = Get-Item -LiteralPath $current -Force
            $aclBefore = Get-AclFingerprint $current
            $targetProperty = $itemBefore.PSObject.Properties['Target']
            if ($null -eq $targetProperty) {
                throw "Cannot safely read reparse-point target: $current"
            }
            $target = @($targetProperty.Value) -join "`0"
            $itemAfter = Get-Item -LiteralPath $current -Force
            $aclAfter = Get-AclFingerprint $current
            if ($itemAfter.Attributes -ne $attributes -or
                $itemAfter.CreationTimeUtc.Ticks -ne $itemBefore.CreationTimeUtc.Ticks -or
                $itemAfter.LastWriteTimeUtc.Ticks -ne $itemBefore.LastWriteTimeUtc.Ticks -or
                $aclAfter -ne $aclBefore) {
                throw "Retired state reparse point changed while it was being fingerprinted: $current"
            }
            $records.Add(
                "R|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$(ConvertTo-FingerprintField $aclBefore)|$(ConvertTo-FingerprintField $target)")
            continue
        }

        $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if ($isDirectory) {
            $itemBefore = Get-Item -LiteralPath $current -Force
            $aclBefore = Get-AclFingerprint $current
            $children = @([IO.Directory]::EnumerateFileSystemEntries($current))
            $itemAfter = Get-Item -LiteralPath $current -Force
            $aclAfter = Get-AclFingerprint $current
            if ($itemAfter.Attributes -ne $attributes -or
                $itemAfter.CreationTimeUtc.Ticks -ne $itemBefore.CreationTimeUtc.Ticks -or
                $itemAfter.LastWriteTimeUtc.Ticks -ne $itemBefore.LastWriteTimeUtc.Ticks -or
                $aclAfter -ne $aclBefore) {
                throw "Retired state directory changed while it was being fingerprinted: $current"
            }
            $records.Add(
                "D|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$(ConvertTo-FingerprintField $aclBefore)")
            foreach ($child in $children) {
                $pending.Push($child)
            }
            continue
        }

        $itemBefore = Get-Item -LiteralPath $current -Force
        $aclBefore = Get-AclFingerprint $current
        $streamsFingerprint = Get-FileStreamsFingerprint $current
        $itemAfter = Get-Item -LiteralPath $current -Force
        $aclAfter = Get-AclFingerprint $current
        if ($itemAfter.Attributes -ne $attributes -or
            $itemAfter.CreationTimeUtc.Ticks -ne $itemBefore.CreationTimeUtc.Ticks -or
            $itemAfter.LastWriteTimeUtc.Ticks -ne $itemBefore.LastWriteTimeUtc.Ticks -or
            $itemAfter.Length -ne $itemBefore.Length -or
            $aclAfter -ne $aclBefore) {
            throw "Retired state file changed while it was being fingerprinted: $current"
        }
        $records.Add(
            "F|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$($itemBefore.Length)|$(ConvertTo-FingerprintField $aclBefore)|$streamsFingerprint")
    }

    return 'PRESENT:' + (Get-TextSha256 @($records | Sort-Object))
}

function Get-LegacyTreeFingerprint([string]$Path) {
    $first = Get-LegacyTreeFingerprintOnce $Path
    $second = Get-LegacyTreeFingerprintOnce $Path
    if ($first -ne $second) {
        throw 'The retired state root changed while its fingerprint was being stabilized.'
    }
    return $first
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
$legacyRoot = Join-Path $env:LOCALAPPDATA 'WinHome'
$legacyBefore = Get-LegacyTreeFingerprint $legacyRoot

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

    $legacyAfter = Get-LegacyTreeFingerprint $legacyRoot
    if ($legacyAfter -ne $legacyBefore) {
        throw 'Inspect changed the retired state root.'
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
