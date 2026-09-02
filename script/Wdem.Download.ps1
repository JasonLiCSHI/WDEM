function Save-WdemRemoteFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $SourceUri,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $DestinationPath,

        [ValidateRange(1, 10)]
        [int] $MaximumAttempts = 3,

        [ValidateRange(0, 30000)]
        [int] $InitialRetryDelayMilliseconds = 1000
    )

    $source = [Uri] $SourceUri
    if (-not $source.IsAbsoluteUri -or $source.Scheme -ne 'https') {
        throw "Download source must be an absolute HTTPS URI: '$SourceUri'."
    }

    $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $curl) {
        throw 'The Windows curl.exe downloader is required but was not found.'
    }

    $destinationDirectory = Split-Path -Parent $DestinationPath
    if ([string]::IsNullOrWhiteSpace($destinationDirectory) -or
        -not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        throw "Download destination directory does not exist: '$destinationDirectory'."
    }

    $curlArguments = @(
        '--disable'
        '--fail'
        '--location'
        '--max-redirs'
        '10'
        '--proto'
        '=https'
        '--proto-redir'
        '=https'
        '--connect-timeout'
        '30'
        '--silent'
        '--show-error'
        '--output'
        $DestinationPath
        '--url'
        $SourceUri
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $curlOutput = @(& $curl.Source @curlArguments 2>&1)
            $curlExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($curlExitCode -eq 0 -and
            (Test-Path -LiteralPath $DestinationPath -PathType Leaf) -and
            (Get-Item -LiteralPath $DestinationPath).Length -gt 0) {
            return
        }

        Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
        $detail = ($curlOutput | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join ' '
        if ([string]::IsNullOrWhiteSpace($detail)) {
            $detail = "curl.exe exited with code $curlExitCode."
        }

        if ($attempt -eq $MaximumAttempts) {
            throw "Download failed after $MaximumAttempts attempts: $detail"
        }

        Write-Warning "Download attempt $attempt failed; retrying: $detail"
        $delay = $InitialRetryDelayMilliseconds * [Math]::Pow(2, $attempt - 1)
        if ($delay -gt 0) {
            Start-Sleep -Milliseconds ([int] $delay)
        }
    }
}
