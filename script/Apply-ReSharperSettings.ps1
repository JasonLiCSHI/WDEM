[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $SettingsPath,
    [string] $TargetPath,
    [switch] $AllowRunningVisualStudio
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
        $SettingsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'settings\CT.DotSettings'
    }

    if ([string]::IsNullOrWhiteSpace($TargetPath)) {
        if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
            throw 'APPDATA is not available for the current user.'
        }

        $TargetPath = Join-Path $env:APPDATA 'JetBrains\Shared\vAny\GlobalSettingsStorage.DotSettings'
    }

    $resolvedSettingsPath = (Resolve-Path -LiteralPath $SettingsPath -ErrorAction Stop).Path
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)

    if (-not $AllowRunningVisualStudio -and (Get-Process -Name devenv -ErrorAction SilentlyContinue)) {
        throw 'Visual Studio is running. Close all Visual Studio instances before applying ReSharper settings.'
    }

    $xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
    $sourceDocument = [System.Xml.XmlDocument]::new()
    $sourceDocument.PreserveWhitespace = $true
    $sourceDocument.Load($resolvedSettingsPath)
    if ($sourceDocument.DocumentElement.LocalName -ne 'ResourceDictionary') {
        throw "ReSharper settings '$resolvedSettingsPath' must contain a ResourceDictionary root element."
    }

    $sourceSettings = @{}
    foreach ($node in $sourceDocument.DocumentElement.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
            continue
        }

        $keyAttribute = $node.Attributes.GetNamedItem('Key', $xamlNamespace)
        if ($null -eq $keyAttribute -or [string]::IsNullOrWhiteSpace($keyAttribute.Value)) {
            continue
        }

        if ($sourceSettings.ContainsKey($keyAttribute.Value)) {
            throw "ReSharper settings contain duplicate key '$($keyAttribute.Value)'."
        }

        $sourceSettings[$keyAttribute.Value] = $node
    }

    if ($sourceSettings.Count -eq 0) {
        throw "ReSharper settings '$resolvedSettingsPath' do not contain any keyed settings."
    }

    if (-not $PSCmdlet.ShouldProcess($targetFullPath, "Merge $($sourceSettings.Count) ReSharper settings")) {
        exit 0
    }

    $targetDirectory = Split-Path -Parent $targetFullPath
    [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    $targetExists = Test-Path -LiteralPath $targetFullPath -PathType Leaf

    if ($targetExists) {
        $targetDocument = [System.Xml.XmlDocument]::new()
        $targetDocument.PreserveWhitespace = $true
        $targetDocument.Load($targetFullPath)
        if ($targetDocument.DocumentElement.LocalName -ne 'ResourceDictionary') {
            throw "Existing ReSharper settings '$targetFullPath' must contain a ResourceDictionary root element."
        }
    }
    else {
        $targetDocument = $sourceDocument.Clone()
    }

    if ($targetExists) {
        foreach ($entry in $sourceSettings.GetEnumerator()) {
            $existingNodes = @($targetDocument.DocumentElement.ChildNodes | Where-Object {
                    if ($_.NodeType -ne [System.Xml.XmlNodeType]::Element) {
                        return $false
                    }

                    $existingKey = $_.Attributes.GetNamedItem('Key', $xamlNamespace)
                    return $null -ne $existingKey -and $existingKey.Value -eq $entry.Key
                })
            $importedNode = $targetDocument.ImportNode($entry.Value, $true)

            if ($existingNodes.Count -gt 0) {
                $targetDocument.DocumentElement.ReplaceChild($importedNode, $existingNodes[0]) | Out-Null
                foreach ($duplicateNode in $existingNodes | Select-Object -Skip 1) {
                    $targetDocument.DocumentElement.RemoveChild($duplicateNode) | Out-Null
                }
            }
            else {
                $targetDocument.DocumentElement.AppendChild(
                    $targetDocument.CreateWhitespace("`r`n`t")) | Out-Null
                $targetDocument.DocumentElement.AppendChild($importedNode) | Out-Null
            }
        }
    }

    $temporaryPath = Join-Path $targetDirectory (
        ".{0}.{1}.tmp" -f ([System.IO.Path]::GetFileName($targetFullPath)), [guid]::NewGuid().ToString('N'))
    $backupPath = $null

    try {
        $writerSettings = [System.Xml.XmlWriterSettings]::new()
        $writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
        $writerSettings.Indent = $false
        $writer = [System.Xml.XmlWriter]::Create($temporaryPath, $writerSettings)
        try {
            $targetDocument.Save($writer)
        }
        finally {
            $writer.Dispose()
        }

        if ($targetExists) {
            $timestamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
            $backupPath = "$targetFullPath.backup-$timestamp"
            [System.IO.File]::Replace($temporaryPath, $targetFullPath, $backupPath, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $targetFullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    Write-Host "Applied $($sourceSettings.Count) ReSharper settings to '$targetFullPath'."
    if ($null -ne $backupPath) {
        Write-Host "Previous settings were backed up to '$backupPath'."
    }

    exit 0
}
catch {
    Write-Error $_
    exit 1
}
