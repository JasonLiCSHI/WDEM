[CmdletBinding()]
param(
    [string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $RepositoryPath '.git'))) {
    throw "Repository path '$RepositoryPath' is not a Git worktree."
}

function Set-ExactRemote {
    param(
        [string]$Name,
        [string]$FetchUrl,
        [string]$PushUrl
    )

    & git -C $RepositoryPath remote get-url $Name *> $null
    if ($LASTEXITCODE -ne 0) {
        & git -C $RepositoryPath remote add $Name $FetchUrl
    }

    & git -C $RepositoryPath config --unset-all "remote.$Name.url" *> $null
    & git -C $RepositoryPath config --add "remote.$Name.url" $FetchUrl
    & git -C $RepositoryPath config --unset-all "remote.$Name.pushurl" *> $null
    & git -C $RepositoryPath config --add "remote.$Name.pushurl" $PushUrl

    $fetchUrls = @(& git -C $RepositoryPath remote get-url --all $Name)
    $pushUrls = @(& git -C $RepositoryPath remote get-url --push --all $Name)
    if ($fetchUrls.Count -ne 1 -or
        $pushUrls.Count -ne 1 -or
        $fetchUrls[0].Trim() -ne $FetchUrl -or
        $pushUrls[0].Trim() -ne $PushUrl) {
        throw "Remote '$Name' was not normalized to the required fetch and push URLs."
    }
}

$originUrl = 'https://github.com/JasonLiCSHI/WDEM.git'
Set-ExactRemote -Name 'origin' -FetchUrl $originUrl -PushUrl $originUrl

$remotes = @{
    'winhome-source' = 'https://github.com/DotDev262/WinHome.git'
    'winhome-fork' = 'https://github.com/JasonLiCSHI/WinHome.git'
}

foreach ($remote in $remotes.GetEnumerator()) {
    Set-ExactRemote -Name $remote.Key -FetchUrl $remote.Value -PushUrl 'DISABLED'
}

Write-Host 'Configured and verified WDEM origin and WinHome provenance remotes.'
