[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $RepositoryPath '.git'))) {
    throw "Repository path '$RepositoryPath' is not a Git worktree."
}

$remotes = @{
    'winhome-source' = 'https://github.com/DotDev262/WinHome.git'
    'winhome-fork' = 'https://github.com/JasonLiCSHI/WinHome.git'
}

foreach ($remote in $remotes.GetEnumerator()) {
    & git -C $RepositoryPath remote get-url $remote.Key *> $null
    if ($LASTEXITCODE -eq 0) {
        & git -C $RepositoryPath remote set-url $remote.Key $remote.Value
    }
    else {
        & git -C $RepositoryPath remote add $remote.Key $remote.Value
    }

    & git -C $RepositoryPath remote set-url --push $remote.Key DISABLED
}

foreach ($remote in $remotes.GetEnumerator()) {
    $fetchUrl = (& git -C $RepositoryPath remote get-url $remote.Key).Trim()
    $pushUrl = (& git -C $RepositoryPath remote get-url --push $remote.Key).Trim()

    if ($fetchUrl -ne $remote.Value -or $pushUrl -ne 'DISABLED') {
        throw "Remote '$($remote.Key)' was not configured as a disabled-push provenance remote."
    }
}

Write-Host 'Configured and verified WinHome provenance remotes with push URLs disabled.'
