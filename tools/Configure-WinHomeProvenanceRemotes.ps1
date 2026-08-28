[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $RepositoryPath '.git'))) {
    throw "Repository path '$RepositoryPath' is not a Git worktree."
}

$originUrl = 'https://github.com/JasonLiCSHI/WDEM.git'
& git -C $RepositoryPath remote get-url origin *> $null
if ($LASTEXITCODE -eq 0) {
    & git -C $RepositoryPath remote set-url origin $originUrl
}
else {
    & git -C $RepositoryPath remote add origin $originUrl
}

& git -C $RepositoryPath remote set-url --push origin $originUrl

# Replace any additional URLs so origin has one exact fetch and push target.
& git -C $RepositoryPath config --unset-all remote.origin.url *> $null
& git -C $RepositoryPath config --add remote.origin.url $originUrl
& git -C $RepositoryPath config --unset-all remote.origin.pushurl *> $null
& git -C $RepositoryPath config --add remote.origin.pushurl $originUrl

$originFetchUrl = @(& git -C $RepositoryPath remote get-url --all origin)
$originPushUrl = @(& git -C $RepositoryPath remote get-url --push --all origin)
if ($originFetchUrl.Count -ne 1 -or
    $originPushUrl.Count -ne 1 -or
    $originFetchUrl[0].Trim() -ne $originUrl -or
    $originPushUrl[0].Trim() -ne $originUrl) {
    throw "Remote 'origin' was not configured for the WDEM repository."
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

Write-Host 'Configured and verified WDEM origin and WinHome provenance remotes.'
