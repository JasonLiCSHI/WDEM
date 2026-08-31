$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

public sealed class WdemStreamInfo
{
    public WdemStreamInfo(string name, long length)
    {
        Name = name;
        Length = length;
    }

    public string Name { get; private set; }
    public long Length { get; private set; }
}

public static class WdemNativeStreams
{
    private const int ERROR_NO_MORE_FILES = 18;
    private const int ERROR_HANDLE_EOF = 38;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private enum StreamInfoLevels
    {
        FindStreamInfoStandard = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return FindClose(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstStreamW(
        string fileName,
        StreamInfoLevels infoLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        SafeFindHandle findStream,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    public static WdemStreamInfo[] Enumerate(string path)
    {
        var streams = new List<WdemStreamInfo>();
        Win32FindStreamData data;
        using (var handle = FindFirstStreamW(
            ToExtendedPath(path),
            StreamInfoLevels.FindStreamInfoStandard,
            out data,
            0))
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (IsEndOfStreams(error))
                {
                    return streams.ToArray();
                }

                throw new Win32Exception(error, "Could not enumerate data streams: " + path);
            }

            streams.Add(new WdemStreamInfo(data.StreamName, data.StreamSize));
            while (true)
            {
                if (FindNextStreamW(handle, out data))
                {
                    streams.Add(new WdemStreamInfo(data.StreamName, data.StreamSize));
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                if (IsEndOfStreams(error))
                {
                    break;
                }

                throw new Win32Exception(error, "Could not continue enumerating data streams: " + path);
            }
        }

        return streams.ToArray();
    }

    public static string ComputeSha256(string path, string streamName)
    {
        var streamPath = ToExtendedPath(path) + streamName;
        using (var handle = CreateFileW(
            streamPath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Could not open data stream: " + path + streamName);
            }

            using (var stream = new FileStream(handle, FileAccess.Read, 65536, false))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
            }
        }
    }

    private static bool IsEndOfStreams(int error)
    {
        return error == ERROR_HANDLE_EOF || error == ERROR_NO_MORE_FILES;
    }

    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath.Substring(2);
        }

        return @"\\?\" + fullPath;
    }
}
'@ -Language CSharp

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

function Get-StreamsFingerprint([string]$Path, [bool]$IsDirectory) {
    $streamsBefore = @([WdemNativeStreams]::Enumerate($Path))
    $metadataBefore = @(
        $streamsBefore |
            Where-Object { -not ($IsDirectory -and $_.Name -eq '::$DATA') } |
            ForEach-Object { "$(ConvertTo-FingerprintField $_.Name)|$($_.Length)" } |
            Sort-Object)
    $records = New-Object 'System.Collections.Generic.List[string]'
    foreach ($stream in $streamsBefore) {
        $streamName = [string]$stream.Name
        if ($IsDirectory -and $streamName -eq '::$DATA') {
            continue
        }
        $contentHash = [WdemNativeStreams]::ComputeSha256($Path, $streamName)
        $records.Add("$(ConvertTo-FingerprintField $streamName)|$($stream.Length)|$contentHash")
    }

    $streamsAfter = @([WdemNativeStreams]::Enumerate($Path))
    $metadataAfter = @(
        $streamsAfter |
            Where-Object { -not ($IsDirectory -and $_.Name -eq '::$DATA') } |
            ForEach-Object { "$(ConvertTo-FingerprintField $_.Name)|$($_.Length)" } |
            Sort-Object)
    if (($metadataBefore -join "`n") -ne ($metadataAfter -join "`n")) {
        throw "The monitored path's streams changed while they were being fingerprinted: $Path"
    }
    return @($records | Sort-Object) -join ';'
}

function Get-TreeFingerprintOnce([string]$Path) {
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
        throw "The monitored root is a reparse point and cannot be fingerprinted safely: $fullRoot"
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
                throw "A monitored reparse point changed while it was being fingerprinted: $current"
            }
            $records.Add(
                "R|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$(ConvertTo-FingerprintField $aclBefore)|$(ConvertTo-FingerprintField $target)")
            continue
        }

        $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if ($isDirectory) {
            $itemBefore = Get-Item -LiteralPath $current -Force
            $aclBefore = Get-AclFingerprint $current
            $streamsFingerprint = Get-StreamsFingerprint $current $true
            $children = @([IO.Directory]::EnumerateFileSystemEntries($current))
            $itemAfter = Get-Item -LiteralPath $current -Force
            $aclAfter = Get-AclFingerprint $current
            if ($itemAfter.Attributes -ne $attributes -or
                $itemAfter.CreationTimeUtc.Ticks -ne $itemBefore.CreationTimeUtc.Ticks -or
                $itemAfter.LastWriteTimeUtc.Ticks -ne $itemBefore.LastWriteTimeUtc.Ticks -or
                $aclAfter -ne $aclBefore) {
                throw "A monitored directory changed while it was being fingerprinted: $current"
            }
            $records.Add(
                "D|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$(ConvertTo-FingerprintField $aclBefore)|$streamsFingerprint")
            foreach ($child in $children) {
                $pending.Push($child)
            }
            continue
        }

        $itemBefore = Get-Item -LiteralPath $current -Force
        $aclBefore = Get-AclFingerprint $current
        $streamsFingerprint = Get-StreamsFingerprint $current $false
        $itemAfter = Get-Item -LiteralPath $current -Force
        $aclAfter = Get-AclFingerprint $current
        if ($itemAfter.Attributes -ne $attributes -or
            $itemAfter.CreationTimeUtc.Ticks -ne $itemBefore.CreationTimeUtc.Ticks -or
            $itemAfter.LastWriteTimeUtc.Ticks -ne $itemBefore.LastWriteTimeUtc.Ticks -or
            $itemAfter.Length -ne $itemBefore.Length -or
            $aclAfter -ne $aclBefore) {
            throw "A monitored file changed while it was being fingerprinted: $current"
        }
        $records.Add(
            "F|$encodedPath|$([int]$attributes)|$($itemBefore.CreationTimeUtc.Ticks)|$($itemBefore.LastWriteTimeUtc.Ticks)|$($itemBefore.Length)|$(ConvertTo-FingerprintField $aclBefore)|$streamsFingerprint")
    }

    return 'PRESENT:' + (Get-TextSha256 @($records | Sort-Object))
}

function Get-TreeFingerprint([string]$Path) {
    $first = Get-TreeFingerprintOnce $Path
    $second = Get-TreeFingerprintOnce $Path
    if ($first -ne $second) {
        throw "The monitored tree changed while its fingerprint was being stabilized: $Path"
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

function ConvertTo-NativeArgument([AllowEmptyString()][string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Stop-BoundedProcessTree(
    [Diagnostics.Process]$Process,
    [string]$Scenario) {
    if ($Process.HasExited) {
        return
    }

    $taskkill = [Diagnostics.Process]::new()
    $taskkillStarted = $false
    $standardOutput = $null
    $standardError = $null
    try {
        $taskkillStartInfo = [Diagnostics.ProcessStartInfo]::new()
        $taskkillStartInfo.FileName = 'taskkill.exe'
        $taskkillStartInfo.UseShellExecute = $false
        $taskkillStartInfo.CreateNoWindow = $true
        $taskkillStartInfo.RedirectStandardOutput = $true
        $taskkillStartInfo.RedirectStandardError = $true
        $taskkillStartInfo.Arguments = "/PID $($Process.Id) /T /F"
        $taskkill.StartInfo = $taskkillStartInfo

        if (-not $taskkill.Start()) {
            throw "Could not start process-tree cleanup for Inspect scenario '$Scenario'."
        }
        $taskkillStarted = $true
        $standardOutput = $taskkill.StandardOutput.ReadToEndAsync()
        $standardError = $taskkill.StandardError.ReadToEndAsync()
        $exitedInTime = $taskkill.WaitForExit(5000)
        if (-not $exitedInTime) {
            $taskkill.Kill()
            if (-not $taskkill.WaitForExit(5000)) {
                throw "Process-tree cleanup for Inspect scenario '$Scenario' could not be stopped."
            }
        }
        $taskkillOutput = @(
            $standardOutput.GetAwaiter().GetResult(),
            $standardError.GetAwaiter().GetResult()) -join "`n"
        $taskkillOutput = $taskkillOutput.Trim()
        $outputDetail = if ([string]::IsNullOrWhiteSpace($taskkillOutput)) {
            ''
        }
        else {
            " Output: $taskkillOutput"
        }
        if (-not $exitedInTime) {
            throw "Process-tree cleanup for Inspect scenario '$Scenario' exceeded its five-second timeout.$outputDetail"
        }
        if ($taskkill.ExitCode -ne 0 -and -not $Process.HasExited) {
            throw "Process-tree cleanup for Inspect scenario '$Scenario' failed with exit code $($taskkill.ExitCode).$outputDetail"
        }
        if (-not $Process.WaitForExit(5000)) {
            throw "WDEM Inspect scenario '$Scenario' did not terminate after its process tree was stopped."
        }
    }
    finally {
        if ($taskkillStarted -and -not $taskkill.HasExited) {
            try {
                $taskkill.Kill()
                [void]$taskkill.WaitForExit(5000)
            }
            catch {
            }
        }
        $taskkill.Dispose()
    }
}

function Invoke-BoundedInspect(
    [string]$Root,
    [string]$Profile,
    [string]$Report,
    [string]$Scenario,
    [switch]$SelectCompanyVsExtension,
    [Threading.Tasks.Task]$NetworkAttempt) {
    $arguments = @(
        'run',
        '--project', 'src\Wdem.Cli\Wdem.Cli.csproj',
        '-p:BuildInParallel=false',
        '--',
        'inspect',
        '--profile', $Profile,
        '--json',
        '--report', $Report)
    if ($SelectCompanyVsExtension) {
        $arguments += @('--select', 'company-vs-extension')
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $Root
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = (@(
        $arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    $started = $false
    $primaryError = $null
    try {
        if (-not $process.Start()) {
            throw "Could not start WDEM Inspect scenario '$Scenario'."
        }
        $started = $true

        while (-not $process.WaitForExit(50)) {
            if ($null -ne $NetworkAttempt -and $NetworkAttempt.IsCompleted) {
                if ($NetworkAttempt.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {
                    $client = $NetworkAttempt.GetAwaiter().GetResult()
                    $client.Dispose()
                    throw "Inspect scenario '$Scenario' attempted a network connection for an acquisition-only source."
                }

                # Preserve unexpected accept failures; expected Stop failures are
                # handled only by the listener's finally block.
                $NetworkAttempt.GetAwaiter().GetResult()
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "WDEM Inspect scenario '$Scenario' exceeded its three-minute timeout."
            }
        }

        if ($null -ne $NetworkAttempt -and $NetworkAttempt.IsCompleted) {
            if ($NetworkAttempt.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {
                $client = $NetworkAttempt.GetAwaiter().GetResult()
                $client.Dispose()
                throw "Inspect scenario '$Scenario' attempted a network connection for an acquisition-only source."
            }
            $NetworkAttempt.GetAwaiter().GetResult()
        }
        if ($process.ExitCode -ne 0) {
            throw "WDEM Inspect scenario '$Scenario' failed with exit code $($process.ExitCode)."
        }
    }
    catch {
        $primaryError = $_
        throw
    }
    finally {
        try {
            if ($started -and -not $process.HasExited) {
                Stop-BoundedProcessTree -Process $process -Scenario $Scenario
            }
        }
        catch {
            if ($null -eq $primaryError) {
                throw
            }
            Write-Warning "Inspect cleanup also failed: $($_.Exception.Message)"
        }
        finally {
            $process.Dispose()
        }
    }
}

function Assert-InspectReport(
    [string]$Report,
    [string]$Scenario,
    [bool]$ExpectCompanyVsExtension) {
    if (-not (Test-Path $Report -PathType Leaf)) {
        throw "WDEM Inspect scenario '$Scenario' did not create its JSON report."
    }

    $rawReport = Get-Content $Report -Raw
    $run = $rawReport | ConvertFrom-Json
    if ($run.mode -ine 'inspect') {
        throw "Expected Inspect report mode for '$Scenario', received '$($run.mode)'."
    }
    if (-not $run.resourceResults.PSObject.Properties['git']) {
        throw "Git result was not reported for '$Scenario'."
    }
    $vsixResult = $run.resourceResults.PSObject.Properties['company-vs-extension']
    if ($ExpectCompanyVsExtension -and -not $vsixResult) {
        throw "The selected VSIX result was not reported for '$Scenario'."
    }
    if (-not $ExpectCompanyVsExtension -and $vsixResult) {
        throw "The optional VSIX was unexpectedly selected for '$Scenario'."
    }
    if ($rawReport -match '(?i)authorization\s*:\s*bearer|password\s*=|token\s*=') {
        throw "Inspect report for '$Scenario' is not redacted."
    }

    foreach ($property in $run.resourceResults.PSObject.Properties) {
        $result = $property.Value
        if (@($result.stepResults).Count -ne 0) {
            throw "Inspect executed a resource step for '$($property.Name)' in '$Scenario'."
        }
        if ($null -ne $result.detectedAfter) {
            throw "Inspect performed post-Apply detection for '$($property.Name)' in '$Scenario'."
        }
        if ($null -ne $result.restartRequirement -and $result.restartRequirement -ine 'noRestart') {
            throw "Inspect reported a restart for '$($property.Name)' in '$Scenario'."
        }
    }
    if (@($run.restartRequirements).Count -ne 0 -or @($run.restartReasons).Count -ne 0) {
        throw "Inspect reported a restart operation for '$Scenario'."
    }
}

function Get-InspectSafetySnapshot {
    return [PSCustomObject]@{
        Registry = Get-RegistryFingerprint
        Environment = Get-PersistentEnvironmentFingerprint
        Boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
        LegacyState = Get-TreeFingerprint (Join-Path $env:LOCALAPPDATA 'WinHome')
        PlanArtifacts = Get-TreeFingerprint (Join-Path $env:ProgramData 'Wdem\PlanArtifacts')
        SecureArtifacts = Get-TreeFingerprint (Join-Path $env:ProgramData 'Wdem\SecureArtifacts')
        BootstrapperDownloads = Get-TreeFingerprint (Join-Path ([IO.Path]::GetTempPath()) 'wdem\visual-studio')
    }
}

function Assert-InspectSafetySnapshot([PSCustomObject]$Before, [string]$Scenario) {
    $after = Get-InspectSafetySnapshot
    if ($after.LegacyState -ne $Before.LegacyState) {
        throw "Inspect scenario '$Scenario' changed the retired state root."
    }
    if ($after.PlanArtifacts -ne $Before.PlanArtifacts) {
        throw "Inspect scenario '$Scenario' staged or changed an elevated plan artifact."
    }
    if ($after.SecureArtifacts -ne $Before.SecureArtifacts) {
        throw "Inspect scenario '$Scenario' staged or changed a secure artifact."
    }
    if ($after.BootstrapperDownloads -ne $Before.BootstrapperDownloads) {
        throw "Inspect scenario '$Scenario' downloaded or changed a Visual Studio bootstrapper."
    }
    if ($after.Registry -ne $Before.Registry) {
        throw "Inspect scenario '$Scenario' changed a persistent Registry installation scope."
    }
    if ($after.Environment -ne $Before.Environment) {
        throw "Inspect scenario '$Scenario' changed a persistent Environment value."
    }
    if ($after.Boot -ne $Before.Boot) {
        throw "Inspect scenario '$Scenario' crossed a machine restart boundary."
    }
}

$root = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("wdem-inspect-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
$optionalReport = Join-Path $tempRoot 'optional-unselected-report.json'
$networkTrapReport = Join-Path $tempRoot 'acquisition-network-trap-report.json'
$originalVsixPath = $env:WDEM_COMPANY_VSIX_PATH
$originalVsixSha256 = $env:WDEM_COMPANY_VSIX_SHA256
$hadVsixPath = Test-Path Env:WDEM_COMPANY_VSIX_PATH
$hadVsixSha256 = Test-Path Env:WDEM_COMPANY_VSIX_SHA256

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Remove-Item Env:WDEM_COMPANY_VSIX_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:WDEM_COMPANY_VSIX_SHA256 -ErrorAction SilentlyContinue

    $optionalBefore = Get-InspectSafetySnapshot
    Invoke-BoundedInspect `
        -Root $root `
        -Profile (Join-Path $root 'profiles\csharp-developer.yaml') `
        -Report $optionalReport `
        -Scenario 'optional-unselected'
    Assert-InspectReport $optionalReport 'optional-unselected' $false
    Assert-InspectSafetySnapshot $optionalBefore 'optional-unselected'

    $networkAttempted = $false
    $networkTrap = $null
    $networkAttempt = $null
    try {
        $networkTrap = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $networkTrap.Start()
        $networkTrapPort = ([Net.IPEndPoint]$networkTrap.LocalEndpoint).Port
        $networkAttempt = $networkTrap.AcceptTcpClientAsync()

        $inspectProfile = Join-Path $tempRoot 'acquisition-network-trap-profile.yaml'
        $bootstrapperSourceUrl = 'https://c2rsetup.officeapps.live.com/c2r/downloadVS.aspx?sku=community&channel=stable&version=VS18&source=WDEM&cid=2500'
        $bootstrapperTrapUrl = "https://127.0.0.1:$networkTrapPort/wdem-inspect-must-not-download-bootstrapper.exe"
        $profileText = Get-Content (Join-Path $root 'profiles\csharp-developer.yaml') -Raw
        if (-not $profileText.Contains($bootstrapperSourceUrl)) {
            throw 'The Visual Studio bootstrapper source URL drifted; the network trap profile was not created.'
        }
        $profileText = $profileText.Replace($bootstrapperSourceUrl, $bootstrapperTrapUrl)
        if ($profileText.Contains($bootstrapperSourceUrl) -or -not $profileText.Contains($bootstrapperTrapUrl)) {
            throw 'The Visual Studio bootstrapper URL was not replaced in the network trap profile.'
        }
        [IO.File]::WriteAllText($inspectProfile, $profileText, [Text.UTF8Encoding]::new($false))

        # Both acquisition sources share the listener. The process monitor
        # disposes an accepted client and terminates the scenario immediately.
        $env:WDEM_COMPANY_VSIX_PATH = "https://127.0.0.1:$networkTrapPort/wdem-inspect-must-not-download.vsix"
        $env:WDEM_COMPANY_VSIX_SHA256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        $networkBefore = Get-InspectSafetySnapshot
        Invoke-BoundedInspect `
            -Root $root `
            -Profile $inspectProfile `
            -Report $networkTrapReport `
            -Scenario 'acquisition-network-trap' `
            -SelectCompanyVsExtension `
            -NetworkAttempt $networkAttempt
        Assert-InspectReport $networkTrapReport 'acquisition-network-trap' $true
        Assert-InspectSafetySnapshot $networkBefore 'acquisition-network-trap'
    }
    finally {
        if ($null -ne $networkTrap) {
            $networkTrap.Stop()
        }
        if ($null -ne $networkAttempt) {
            try {
                if (-not $networkAttempt.Wait(5000)) {
                    throw 'The network trap accept did not reach a terminal state after the listener stopped.'
                }
            }
            catch [AggregateException] {
                $unexpected = @($_.Exception.Flatten().InnerExceptions | Where-Object {
                    if ($_ -is [ObjectDisposedException]) {
                        return $false
                    }
                    if ($_ -is [Net.Sockets.SocketException]) {
                        return $_.SocketErrorCode -notin @(
                            [Net.Sockets.SocketError]::Interrupted,
                            [Net.Sockets.SocketError]::OperationAborted,
                            [Net.Sockets.SocketError]::NotSocket)
                    }
                    return $true
                })
                if ($unexpected.Count -ne 0) {
                    throw
                }
            }
            if ($networkAttempt.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {
                $client = $networkAttempt.GetAwaiter().GetResult()
                $client.Dispose()
                $networkAttempted = $true
            }
            elseif ($networkAttempt.Status -eq [Threading.Tasks.TaskStatus]::Canceled) {
                throw 'The network trap accept was unexpectedly canceled.'
            }
        }
    }
    if ($networkAttempted) {
        throw 'Inspect attempted a network connection for an acquisition-only source.'
    }

    if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'WDEM\runs') -PathType Container)) {
        throw 'WDEM did not persist Inspect state below %LOCALAPPDATA%\WDEM.'
    }

    Write-Host 'WDEM Inspect smoke passed both scenarios without Apply-side effects.'
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
