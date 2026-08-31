[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',

    [Parameter()]
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [Parameter()]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $artifactRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact directory escaped the repository: $artifactRoot"
}

$publishRoot = Join-Path $artifactRoot "publish\$Runtime"
$appPublish = Join-Path $publishRoot 'app'
$cliPublish = Join-Path $publishRoot 'cli'
$installerOutput = Join-Path $artifactRoot 'installer'

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $appPublish, $cliPublish, $installerOutput -Force | Out-Null

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$Project,

        [Parameter(Mandatory)]
        [string]$Output
    )

    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $Output `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project."
    }
}

Invoke-DotnetPublish -Project (Join-Path $repoRoot 'src\Wdem.App\Wdem.App.csproj') -Output $appPublish
Invoke-DotnetPublish -Project (Join-Path $repoRoot 'src\Wdem.Cli\Wdem.Cli.csproj') -Output $cliPublish

Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishRoot

$innoCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($null -eq $innoCompiler) {
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

$installerScript = Join-Path $repoRoot 'installer\Wdem.iss'
& $innoCompiler.Source `
    "/DMyAppVersion=$Version" `
    "/DPublishRoot=$publishRoot" `
    "/DOutputDir=$installerOutput" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup compilation failed.'
}

$installer = Get-ChildItem -LiteralPath $installerOutput -Filter '*.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $installer) {
    throw 'Installer output was not created.'
}

$hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
$hashLine = "$($hash.Hash.ToLowerInvariant())  $($installer.Name)"
Set-Content -LiteralPath "$($installer.FullName).sha256" -Value $hashLine -Encoding ascii

Write-Host "Installer: $($installer.FullName)"
Write-Host "SHA256:   $($hash.Hash)"
