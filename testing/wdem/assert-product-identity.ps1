$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$release = Get-Content (Join-Path $root '.github\workflows\release.yaml') -Raw

foreach ($executable in 'Wdem.Cli.exe', 'Wdem.Desktop.exe', 'Wdem.ElevatedHost.exe') {
    if ($release -notmatch [regex]::Escape($executable)) {
        throw "Release workflow does not publish $executable."
    }
}

foreach ($required in 'Wdem.sln', 'Wdem-win-x64.zip', 'SHA256SUMS.txt', 'THIRD-PARTY-NOTICES.md', 'WindowsAppSDKSelfContained') {
    if ($release -notmatch [regex]::Escape($required)) {
        throw "Release workflow does not contain $required."
    }
}

$desktopPublish = [regex]::Match(
    $release,
    '(?m)^\s*dotnet publish[^\r\n]*Wdem\.Desktop[^\r\n]*$')
if (-not $desktopPublish.Success) {
    throw 'Release workflow does not publish the WinUI desktop host.'
}
if ($desktopPublish.Value -match 'PublishSingleFile') {
    throw 'The WinUI desktop host must not be published as a single-file executable.'
}

if ($release -match '(?m)^\s*dotnet publish[^\r\n]*Wdem\.LegacySource') {
    throw 'The transition-source library must not be published.'
}

foreach ($forbidden in 'WinHome.exe', 'WinHome.sln', 'src\WinHome.csproj') {
    if ($release -match [regex]::Escape($forbidden)) {
        throw "Release workflow must not contain $forbidden."
    }
}

$releaseAssetBlock = [regex]::Match(
    $release,
    '(?m)^[ \t]*files:[ \t]*\|[ \t]*\r?\n(?<assets>(?:[ \t]+publish[\\/][^\r\n]+\r?\n)+)')
if (-not $releaseAssetBlock.Success) {
    throw 'Release workflow does not declare its product assets.'
}

$actualAssets = @(
    $releaseAssetBlock.Groups['assets'].Value -split '\r?\n' |
        ForEach-Object { $_.Trim().Replace('\', '/') } |
        Where-Object { $_ }
)
$expectedAssets = @(
    'publish/Wdem-win-x64.zip',
    'publish/SHA256SUMS.txt',
    'publish/THIRD-PARTY-NOTICES.md'
)
if (Compare-Object $expectedAssets $actualAssets) {
    throw "Release assets must be exactly: $($expectedAssets -join ', ')."
}

foreach ($directory in "Join-Path `$root 'Cli'", "Join-Path `$root 'Desktop'", "Join-Path `$root 'ElevatedHost'") {
    if ($release -notmatch [regex]::Escape($directory)) {
        throw "Release archive does not preserve required directory expression: $directory."
    }
}

$allowed = @(
    'THIRD-PARTY-NOTICES.md',
    'docs/wdem/source-provenance.md',
    'docs/superpowers/plans/2026-08-28-wdem-complete-product.md',
    'testing/wdem/acceptance-checklist.md'
)
$matches = @(git -C $root grep -Iil -e WinHome -e 'DotDev262/WinHome' -- '*.md' '*.yml' '*.yaml')
if ($LASTEXITCODE -gt 1) {
    throw "git grep failed with exit code $LASTEXITCODE."
}

$unexpected = $matches | Where-Object { $_ -notin $allowed }
if ($unexpected) {
    throw "User-facing branding is not fully WDEM: $($unexpected -join ', ')"
}
