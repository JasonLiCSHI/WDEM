[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Detect', 'Pre', 'Apply')]
    [string] $Action,

    [string] $SourceUri,
    [string] $Sha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-InstalledVersion {
    $roots = @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    $versions = foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue |
            Get-ItemProperty -ErrorAction SilentlyContinue |
            Select-Object DisplayName, DisplayVersion |
            Where-Object { $_.DisplayName -eq 'JetBrains ReSharper in Visual Studio Professional 2026' } |
            ForEach-Object { $_.DisplayVersion }
    }

    $version = $versions |
        Where-Object { $_ -match '^\d+(?:\.\d+){1,3}$' } |
        Sort-Object { [version] $_ } -Descending |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw 'JetBrains ReSharper for Visual Studio Professional 2026 is not installed.'
    }

    return $version
}

function Assert-VisualStudioHost {
    $vsWherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vsWherePath -PathType Leaf)) {
        throw 'Visual Studio Locator (vswhere.exe) is not installed.'
    }

    $installationPath = & $vsWherePath `
        -latest `
        -products Microsoft.VisualStudio.Product.Professional `
        -version '[18.0,19.0)' `
        -property installationPath
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw 'Visual Studio Professional 2026 must be installed before ReSharper.'
    }
}

function Assert-Preflight {
    Assert-VisualStudioHost
    if (Get-Process -Name devenv -ErrorAction SilentlyContinue) {
        throw 'Visual Studio is running. Close all Visual Studio instances before continuing.'
    }

    $uri = [Uri] $SourceUri
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('download.jetbrains.com', 'download-cdn.jetbrains.com')) {
        throw "ReSharper source must be an official JetBrains HTTPS endpoint: '$SourceUri'."
    }
    if ($Sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'ReSharper installer SHA-256 must contain exactly 64 hexadecimal characters.'
    }
}

try {
    switch ($Action) {
        'Detect' {
            $version = Get-InstalledVersion
            Write-Output "JetBrains ReSharper version $version"
        }
        'Pre' {
            Assert-Preflight
            Write-Output 'ReSharper preflight passed; Visual Studio Professional 2026 is available and closed.'
        }
        'Apply' {
            Assert-Preflight
            if (-not $PSCmdlet.ShouldProcess('JetBrains ReSharper', "Install from '$SourceUri'")) {
                return
            }

            $installerPath = Join-Path ([IO.Path]::GetTempPath()) "WDEM-resharper-$([Guid]::NewGuid().ToString('N')).exe"
            try {
                Write-Output 'Downloading ReSharper from JetBrains.'
                Invoke-WebRequest -Uri $SourceUri -OutFile $installerPath -MaximumRedirection 10
                $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
                if (-not $actualHash.Equals($Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "ReSharper installer integrity check failed. Expected $Sha256 but received $actualHash."
                }

                Write-Output 'Opening JetBrains Installer. Complete the installation in the installer window.'
                $installerProcess = Start-Process `
                    -FilePath $installerPath `
                    -PassThru
                $installerProcess | Wait-Process
                $installerExitCode = $installerProcess.ExitCode
                if ($installerExitCode -notin @(0, 1641, 3010)) {
                    throw "ReSharper Installer failed with exit code $installerExitCode."
                }
                if ($installerExitCode -ne 0) {
                    Write-Warning "ReSharper installed successfully; a Windows restart is required (exit code $installerExitCode)."
                }
            }
            finally {
                Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
