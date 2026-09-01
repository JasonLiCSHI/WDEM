[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Detect', 'Pre', 'Apply', 'Post')]
    [string] $Action,

    [string] $SourceUri = 'https://aka.ms/vs/18/stable/vs_professional.exe',
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-VsWherePath {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    )

    $path = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw 'Visual Studio Locator (vswhere.exe) is not installed.'
    }

    return $path
}

function Get-Configuration {
    if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        $script:ConfigPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Settings\.vsconfig'
    }

    $resolvedPath = (Resolve-Path -LiteralPath $ConfigPath -ErrorAction Stop).Path
    $configuration = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    $components = @($configuration.components | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($components.Count -eq 0) {
        throw "Visual Studio configuration '$resolvedPath' does not declare any components."
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Components = $components
    }
}

function Get-InstalledVersion {
    param([string[]] $RequiredComponents = @())

    $arguments = @(
        '-latest'
        '-products', 'Microsoft.VisualStudio.Product.Professional'
        '-version', '[18.0,19.0)'
    )
    if ($RequiredComponents.Count -gt 0) {
        $arguments += '-requires'
        $arguments += $RequiredComponents
    }
    $arguments += @('-property', 'catalog_productDisplayVersion')

    $vsWherePath = Get-VsWherePath
    $version = & $vsWherePath @arguments 2>$null | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        if ($RequiredComponents.Count -gt 0) {
            throw 'Visual Studio Professional 2026 is missing one or more components declared by .vsconfig.'
        }
        throw 'Visual Studio Professional 2026 is not installed.'
    }

    return $version.Trim()
}

function Assert-Preflight {
    $configuration = Get-Configuration
    $uri = [Uri] $SourceUri
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('aka.ms', 'download.visualstudio.microsoft.com')) {
        throw "Visual Studio source must be an official Microsoft HTTPS endpoint: '$SourceUri'."
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Visual Studio Professional 2026 requires 64-bit Windows.'
    }
    if (Get-Process -Name devenv -ErrorAction SilentlyContinue) {
        throw 'Visual Studio is running. Close all Visual Studio instances before continuing.'
    }

    return $configuration
}

try {
    switch ($Action) {
        'Detect' {
            $version = Get-InstalledVersion
            Write-Output "Visual Studio Professional version $version"
        }
        'Pre' {
            $configuration = Assert-Preflight
            Write-Output "Visual Studio preflight passed; $($configuration.Components.Count) components are declared."
        }
        'Apply' {
            $configuration = Assert-Preflight
            if (-not $PSCmdlet.ShouldProcess('Visual Studio Professional 2026', "Install from '$SourceUri'")) {
                return
            }

            $bootstrapperPath = Join-Path ([IO.Path]::GetTempPath()) "WDEM-vs-professional-$([Guid]::NewGuid().ToString('N')).exe"
            try {
                Write-Output 'Downloading the Visual Studio Professional bootstrapper from Microsoft.'
                Invoke-WebRequest -Uri $SourceUri -OutFile $bootstrapperPath -MaximumRedirection 10
                & $bootstrapperPath --quiet --wait --norestart --config $configuration.Path
                $installerExitCode = $LASTEXITCODE
                if ($installerExitCode -notin @(0, 1641, 3010)) {
                    throw "Visual Studio Installer failed with exit code $installerExitCode."
                }
                if ($installerExitCode -ne 0) {
                    Write-Warning "Visual Studio installed successfully; a Windows restart is required (exit code $installerExitCode)."
                }
            }
            finally {
                Remove-Item -LiteralPath $bootstrapperPath -Force -ErrorAction SilentlyContinue
            }
        }
        'Post' {
            $configuration = Get-Configuration
            $version = Get-InstalledVersion -RequiredComponents $configuration.Components
            Write-Output "Visual Studio Professional version $version contains all declared components."
        }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
