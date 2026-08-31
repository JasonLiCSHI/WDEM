using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace Wdem.Core.Tests.Identity;

public sealed class RepositoryIdentityTests
{
  private static readonly string RepositoryRoot = FindRepositoryRoot();

  [Fact]
  public void UsesWdemSolutionInsteadOfWinHomeSolution()
  {
    Assert.True(File.Exists(Path.Combine(RepositoryRoot, "Wdem.sln")));
    Assert.False(File.Exists(Path.Combine(RepositoryRoot, "WinHome.sln")));
  }

  [Fact]
  public void IncludesThirdPartyNotices()
  {
    Assert.True(File.Exists(Path.Combine(RepositoryRoot, "THIRD-PARTY-NOTICES.md")));
  }

  [Fact]
  public void DevContainerConfigurationIsTrackedAsARegularFile()
  {
    var result = RunProcess(
        RepositoryRoot,
        "git",
        "ls-files",
        "-s",
        "--",
        ".devcontainer.json");

    Assert.Equal(0, result.ExitCode);
    Assert.StartsWith("100644 ", result.Output, StringComparison.Ordinal);
  }

  [Fact]
  public void ProductGuidesDescribeCurrentSupportedHostsAndDistribution()
  {
    var guide = NormalizeWhitespace(File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "wdem",
        "getting-started.md")));

    Assert.Contains("`Wdem-win-x64.zip`", guide);
    Assert.Contains("`Desktop\\Wdem.Desktop.exe`", guide);
    Assert.Contains("`Cli\\Wdem.Cli.exe`", guide);
    Assert.Contains("`%LOCALAPPDATA%\\WDEM`", guide);
    Assert.Contains("`WDEM_COMPANY_VSIX_PATH`", guide);
    Assert.Contains("Retain every file", guide, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("WINHOME_", guide, StringComparison.Ordinal);
  }

  [Fact]
  public void ReleaseDocumentationRunsCliFromTheDesktopApplicationRoot()
  {
    var guide = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "wdem",
        "getting-started.md"));

    var push = guide.IndexOf("Push-Location .\\WDEM\\Desktop", StringComparison.Ordinal);
    var inspect = guide.IndexOf(
        "..\\Cli\\Wdem.Cli.exe inspect --profile .\\profiles\\csharp-developer.yaml",
        StringComparison.Ordinal);
    var pop = guide.IndexOf("Pop-Location", StringComparison.Ordinal);

    Assert.True(push >= 0, "The release CLI example must enter the Desktop application root.");
    Assert.True(inspect > push, "The release CLI must run from the Desktop application root.");
    Assert.True(pop > inspect, "The release CLI example must restore the caller's location.");
    Assert.DoesNotContain(
        ".\\WDEM\\Cli\\Wdem.Cli.exe inspect --profile .\\WDEM\\Desktop\\profiles",
        guide,
        StringComparison.Ordinal);

    var troubleshooting = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "troubleshooting.md"));
    Assert.Contains(
        "From the extracted `Desktop` directory",
        troubleshooting,
        StringComparison.Ordinal);
    Assert.Contains(
        "`..\\Cli\\Wdem.Cli.exe inspect --profile <path> --json`",
        troubleshooting,
        StringComparison.Ordinal);
    Assert.Contains(
        "`..\\Cli\\Wdem.Cli.exe runs list`",
        troubleshooting,
        StringComparison.Ordinal);
  }

  [Fact]
  public void RecoveryGuideRequiresFreshPlanningAndDocumentsTheSecurityBoundary()
  {
    var guide = NormalizeWhitespace(File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "wdem",
        "recovery-and-security.md")));

    Assert.Contains("UAC", guide, StringComparison.Ordinal);
    Assert.Contains("SHA-256", guide, StringComparison.Ordinal);
    Assert.Contains("redactor", guide, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("fresh Detect", guide, StringComparison.Ordinal);
    Assert.Contains("fresh Plan", guide, StringComparison.Ordinal);
    Assert.Contains("fetch-only", guide, StringComparison.Ordinal);
  }

  [Fact]
  public void ProvenanceDocumentsDefineTheStandaloneRepositoryBoundary()
  {
    var notices = NormalizeWhitespace(File.ReadAllText(
        Path.Combine(RepositoryRoot, "THIRD-PARTY-NOTICES.md")));
    var provenance = NormalizeWhitespace(File.ReadAllText(
        Path.Combine(RepositoryRoot, "docs", "wdem", "source-provenance.md")));

    Assert.Contains(
        "not a branch, pull request, or merge target of either WinHome repository",
        notices);
    Assert.Contains("fetch-only", provenance);
  }

  [Fact]
  public void CiBuildsPortableProjectsOnUbuntuAndTheFullSolutionOnWindows()
  {
    var workflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "ci.yml"));
    var crossPlatformJob = ExtractSection(workflow, "  wdem-cross-platform:", "  wdem-windows:");
    var windowsJob = workflow[workflow.IndexOf("  wdem-windows:", StringComparison.Ordinal)..];

    Assert.Contains(
        "dotnet restore tests/Wdem.Core.Tests/Wdem.Core.Tests.csproj -m:1",
        crossPlatformJob);
    Assert.Contains(
        "dotnet format tests/Wdem.Core.Tests/Wdem.Core.Tests.csproj",
        crossPlatformJob);
    Assert.Contains(
        "dotnet build tests/Wdem.Core.Tests/Wdem.Core.Tests.csproj --no-restore -m:1",
        crossPlatformJob);
    Assert.DoesNotContain("Wdem.sln", crossPlatformJob, StringComparison.Ordinal);

    Assert.Contains("dotnet format Wdem.sln", windowsJob);
    Assert.Matches(@"dotnet restore Wdem\.sln[^\r\n]*-m:1", windowsJob);
    Assert.Matches(@"dotnet build Wdem\.sln[^\r\n]*-m:1", windowsJob);
    Assert.Matches(@"dotnet test Wdem\.sln[^\r\n]*-m:1", windowsJob);
  }

  [Fact]
  public void CiRunsTheProductIdentityContractInWindowsPowerShell()
  {
    var workflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "ci.yml"));

    Assert.Matches(
        @"(?ms)- name: Verify WDEM product identity\s+shell: pwsh\s+run: .*testing/wdem/assert-product-identity\.ps1",
        workflow);
  }

  [Fact]
  public void ReleaseSupportsExplicitManualTagsAndPinsItsReleaseAction()
  {
    var workflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "release.yaml"));

    Assert.Matches(
        @"(?ms)workflow_dispatch:\s+inputs:\s+tag_name:.*?required:\s*true",
        workflow);
    Assert.Matches(
        @"(?m)^[ \t]*-[ \t]+uses:[ \t]+actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1[ \t]+#[ \t]+v7\.0\.1[ \t]*\r?\n" +
        @"[ \t]+with:[ \t]*\r?\n" +
        @"[ \t]+ref:[ \t]+\$\{\{ inputs\.tag_name \|\| github\.ref \}\}[ \t]*\r?$",
        workflow);
    Assert.Matches(
        @"(?m)^[ \t]*uses:[ \t]+actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68[ \t]+#[ \t]+v6\.0\.0[ \t]*\r?$",
        workflow);
    Assert.Matches(
        @"(?m)^[ \t]*uses:[ \t]+softprops/action-gh-release@efb35369e0ad2afab669f228072c1b0d510eae64[ \t]+#[ \t]+v3\.0\.3[ \t]*\r?$",
        workflow);
    Assert.Matches(
        @"tag_name:\s*\$\{\{ inputs\.tag_name \|\| github\.ref_name \}\}",
        workflow);
  }

  [Fact]
  public void ProfileAuthoringGuideMatchesSupportedExtensionsAndVsixPrivilegePolicy()
  {
    var guide = NormalizeWhitespace(File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "wdem",
        "profile-authoring.md")));

    Assert.Contains("YAML (`.yaml`) and JSON (`.json`)", guide);
    Assert.DoesNotContain("`.yml`", guide, StringComparison.Ordinal);
    Assert.Matches(
        @"visual-studio-extension.*privilegeRequirement: Administrator.*rejects CurrentUser",
        guide);
  }

  [Fact]
  public void DependabotDoesNotRequireARepositorySpecificLabel()
  {
    var configuration = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "dependabot.yml"));

    Assert.DoesNotContain("\"wdem\"", configuration, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("winhome")]
  [InlineData("WINHOME")]
  [InlineData("https://github.com/DotDev262/wInHoMe")]
  public void ProductIdentityScriptRejectsBrandingRegardlessOfCase(string forbiddenBranding)
  {
    using var repository = new TemporaryDirectory();
    var scriptDirectory = Path.Combine(repository.Path, "testing", "wdem");
    var workflowDirectory = Path.Combine(repository.Path, ".github", "workflows");
    Directory.CreateDirectory(scriptDirectory);
    Directory.CreateDirectory(workflowDirectory);
    File.Copy(
        Path.Combine(RepositoryRoot, "testing", "wdem", "assert-product-identity.ps1"),
        Path.Combine(scriptDirectory, "assert-product-identity.ps1"));
    File.Copy(
        Path.Combine(RepositoryRoot, ".github", "workflows", "release.yaml"),
        Path.Combine(workflowDirectory, "release.yaml"));
    File.WriteAllText(Path.Combine(repository.Path, "README.md"), forbiddenBranding);

    Assert.Equal(0, RunProcess(repository.Path, "git", "init").ExitCode);
    Assert.Equal(0, RunProcess(repository.Path, "git", "add", ".").ExitCode);

    var result = RunProcess(
        repository.Path,
        "pwsh",
        "-NoLogo",
        "-NoProfile",
        "-File",
        Path.Combine(scriptDirectory, "assert-product-identity.ps1"));

    Assert.NotEqual(0, result.ExitCode);
    Assert.Contains("README.md", result.Output, StringComparison.Ordinal);
  }

  [Fact]
  public void ProductIdentityScriptAcceptsCrLfReleaseWorkflow()
  {
    using var repository = new TemporaryDirectory();
    var scriptDirectory = Path.Combine(repository.Path, "testing", "wdem");
    var workflowDirectory = Path.Combine(repository.Path, ".github", "workflows");
    Directory.CreateDirectory(scriptDirectory);
    Directory.CreateDirectory(workflowDirectory);
    File.Copy(
        Path.Combine(RepositoryRoot, "testing", "wdem", "assert-product-identity.ps1"),
        Path.Combine(scriptDirectory, "assert-product-identity.ps1"));

    var workflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "release.yaml"));
    File.WriteAllText(
        Path.Combine(workflowDirectory, "release.yaml"),
        workflow.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal));

    Assert.Equal(0, RunProcess(repository.Path, "git", "init").ExitCode);
    Assert.Equal(0, RunProcess(repository.Path, "git", "add", ".").ExitCode);

    var result = RunProcess(
        repository.Path,
        "pwsh",
        "-NoLogo",
        "-NoProfile",
        "-File",
        Path.Combine(scriptDirectory, "assert-product-identity.ps1"));

    Assert.True(result.ExitCode == 0, result.Output);
  }

  [Fact]
  public void AcceptanceChecklistRecordsAutomatedAndCleanMachineEvidence()
  {
    var checklistPath = Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "acceptance-checklist.md");

    Assert.True(File.Exists(checklistPath), "Missing WDEM acceptance checklist.");
    var checklist = NormalizeWhitespace(File.ReadAllText(checklistPath));

    foreach (var required in new[]
             {
               "Repository identity",
               "State and inputs",
               "One-time migration",
               "Inspect safety",
               "Desktop",
               "Clean VM apply",
               "DISABLED",
               "%LOCALAPPDATA%\\WDEM\\migration-v1.json",
               "PermissionError",
               "fresh Detect",
               "fresh Plan",
               "restore the snapshot"
             })
    {
      Assert.Contains(required, checklist, StringComparison.OrdinalIgnoreCase);
    }

    Assert.Contains("manual evidence", checklist, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("not executed", checklist, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void InspectSmokeUsesTheInspectContractAndCleansItsTemporaryArtifacts()
  {
    var scriptPath = Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1");

    Assert.True(File.Exists(scriptPath), "Missing WDEM Inspect smoke script.");
    var script = File.ReadAllText(scriptPath);

    var boundedInspect = ExtractSection(
        script,
        "function Invoke-BoundedInspect(",
        "function Assert-InspectReport(");
    var arguments = NormalizeWhitespace(ExtractSection(
        boundedInspect,
        "    $arguments = @(",
        "    if ($SelectCompanyVsExtension)"));

    Assert.Equal(
        "$arguments = @( 'run', '--project', 'src\\Wdem.Cli\\Wdem.Cli.csproj', " +
        "'-p:BuildInParallel=false', '--', 'inspect', '--profile', $Profile, " +
        "'--json', '--report', $Report)",
        arguments);
    Assert.DoesNotContain("'apply'", arguments, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotMatch(@"(?i)&?\s*[^\r\n]*Wdem(?:\.Cli)?(?:\.exe)?\s+apply\b", script);
    Assert.Contains("resourceResults", script, StringComparison.Ordinal);
    Assert.Contains("stepResults", script, StringComparison.Ordinal);
    Assert.Contains("detectedAfter", script, StringComparison.Ordinal);
    Assert.Contains("restartRequirements", script, StringComparison.Ordinal);
    Assert.Contains("Registry", script, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Environment", script, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("GetTempPath", script, StringComparison.Ordinal);
    Assert.Contains("finally", script, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(
        "Join-Path $PSScriptRoot 'inspect-report.json'",
        script,
        StringComparison.Ordinal);
  }

  [Fact]
  public void InspectSmokeSeparatesOptionalUnsetAndNetworkTrapScenarios()
  {
    var script = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1"));

    Assert.Contains("optional-unselected", script, StringComparison.Ordinal);
    Assert.Contains("acquisition-network-trap", script, StringComparison.Ordinal);
    Assert.Contains("Remove-Item Env:WDEM_COMPANY_VSIX_PATH", script, StringComparison.Ordinal);
    Assert.Contains("Remove-Item Env:WDEM_COMPANY_VSIX_SHA256", script, StringComparison.Ordinal);
    Assert.Contains("AcceptTcpClientAsync", script, StringComparison.Ordinal);
    Assert.Contains("WaitForExit(50)", script, StringComparison.Ordinal);
    Assert.Contains("$networkAttempt.Wait(5000)", script, StringComparison.Ordinal);
    Assert.Contains("function Stop-BoundedProcessTree", script, StringComparison.Ordinal);
    Assert.Contains("taskkill.exe", script, StringComparison.OrdinalIgnoreCase);
    Assert.Matches(@"(?s)/PID.*\$Process\.Id.*/T.*/F", script);
    Assert.Contains("RedirectStandardOutput = $true", script, StringComparison.Ordinal);
    Assert.Contains("RedirectStandardError = $true", script, StringComparison.Ordinal);
    Assert.Contains("StandardOutput.ReadToEndAsync()", script, StringComparison.Ordinal);
    Assert.Contains("StandardError.ReadToEndAsync()", script, StringComparison.Ordinal);
    Assert.Contains("$started = $false", script, StringComparison.Ordinal);
    Assert.Contains("$started = $true", script, StringComparison.Ordinal);
    Assert.Matches(
        @"(?s)finally\s*\{.*if \(\$started\s+-and.*Stop-BoundedProcessTree.*finally\s*\{\s*\$process\.Dispose\(\)",
        script);
    Assert.Contains("$bootstrapperSourceUrl", script, StringComparison.Ordinal);
    Assert.Contains("$bootstrapperTrapUrl", script, StringComparison.Ordinal);
    Assert.Contains("Get-TreeFingerprint", script, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-LegacyTreeFingerprint", script, StringComparison.Ordinal);
    Assert.DoesNotContain("Retired state streams changed", script, StringComparison.Ordinal);
  }

  [Fact]
  public void ContributingDocumentsBothNarrowProgramDataSecurityExceptions()
  {
    var guide = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "CONTRIBUTING.md"));
    var planArtifacts = ExtractMarkdownBullet(
        guide,
        "%ProgramData%\\Wdem\\PlanArtifacts");
    var secureArtifacts = ExtractMarkdownBullet(
        guide,
        "%ProgramData%\\Wdem\\SecureArtifacts");

    Assert.Contains("cross-integrity", planArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("verified VSIX plan artifacts", planArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("revocation metadata", planArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("SecureArtifacts", planArtifacts, StringComparison.Ordinal);

    Assert.Contains("short-lived", secureArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ACL-restricted", secureArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("verified executables", secureArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("VSIX", secureArtifacts, StringComparison.Ordinal);
    Assert.Contains("Visual Studio configuration", secureArtifacts, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("PlanArtifacts", secureArtifacts, StringComparison.Ordinal);
  }

  [Fact]
  public void InspectSmokeTerminatesTheEntireProcessTreeAndPreservesStartFailures()
  {
    var script = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1"));
    var mainScript = script.IndexOf(
        "$root = Split-Path $PSScriptRoot",
        StringComparison.Ordinal);
    Assert.True(mainScript > 0, "Could not isolate the smoke process helpers.");

    using var directory = new TemporaryDirectory();
    var harnessPath = Path.Combine(directory.Path, "process-cleanup-harness.ps1");
    var testRoot = directory.Path.Replace("'", "''", StringComparison.Ordinal);
    var harness = script[..mainScript] + $$"""
        $testRoot = '{{testRoot}}'
        $rootProcess = $null
        $childId = 0
        try {
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = 'powershell.exe'
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.Arguments = '-NoLogo -NoProfile -Command "$childStartInfo = [Diagnostics.ProcessStartInfo]::new(); $childStartInfo.FileName = ''powershell.exe''; $childStartInfo.UseShellExecute = $false; $childStartInfo.CreateNoWindow = $true; $childStartInfo.Arguments = ''-NoLogo -NoProfile -Command Start-Sleep -Seconds 60''; $child = [Diagnostics.Process]::new(); try { $child.StartInfo = $childStartInfo; if (-not $child.Start()) { throw ''Could not start child process.'' }; [Console]::Out.WriteLine($child.Id); [Console]::Out.Flush(); $child.WaitForExit() } finally { $child.Dispose() }"'
            $rootProcess = [Diagnostics.Process]::new()
            $rootProcess.StartInfo = $startInfo
            if (-not $rootProcess.Start()) { throw 'Could not start process-tree harness.' }
            $childId = [int]$rootProcess.StandardOutput.ReadLine()

            Stop-BoundedProcessTree -Process $rootProcess -Scenario 'process-tree-harness'
            if (-not $rootProcess.HasExited) { throw 'The root process survived tree cleanup.' }
            try {
                $child = [Diagnostics.Process]::GetProcessById($childId)
                try {
                    if (-not $child.HasExited) { throw 'The child process survived tree cleanup.' }
                }
                finally {
                    $child.Dispose()
                }
            }
            catch [ArgumentException] {
            }
            Write-Output 'TREE_TERMINATED'
        }
        finally {
            if ($null -ne $rootProcess) {
                try {
                    if (-not $rootProcess.HasExited) {
                        & taskkill.exe /PID $rootProcess.Id /T /F | Out-Null
                    }
                }
                catch {
                }
                $rootProcess.Dispose()
            }
        }

        $missingRoot = Join-Path $testRoot 'missing-working-directory'
        try {
            Invoke-BoundedInspect `
                -Root $missingRoot `
                -Profile (Join-Path $testRoot 'missing-profile.yaml') `
                -Report (Join-Path $testRoot 'missing-report.json') `
                -Scenario 'start-failure-harness'
            throw 'Expected Process.Start to fail.'
        }
        catch {
            if ($_.Exception.Message -match 'No process is associated') {
                throw 'Process cleanup replaced the original Process.Start failure.'
            }
            Write-Output 'START_FAILURE_PRESERVED'
        }
        """;
    Assert.DoesNotContain(
        "Start-Process powershell.exe",
        harness,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains(
        "$childStartInfo.CreateNoWindow = $true",
        harness,
        StringComparison.Ordinal);
    Assert.Contains(
        "$childStartInfo.UseShellExecute = $false",
        harness,
        StringComparison.Ordinal);
    Assert.Contains(
        "finally { $child.Dispose() }",
        harness,
        StringComparison.Ordinal);
    File.WriteAllText(harnessPath, harness);

    var result = RunProcess(
        directory.Path,
        "powershell",
        "-NoLogo",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        harnessPath);

    Assert.True(result.ExitCode == 0, result.Output);
    Assert.Contains("TREE_TERMINATED", result.Output, StringComparison.Ordinal);
    Assert.Contains("START_FAILURE_PRESERVED", result.Output, StringComparison.Ordinal);
  }

  [Fact]
  public void TestProjectsCentralizeTheCoverageCollector()
  {
    var propsPath = Path.Combine(RepositoryRoot, "tests", "Directory.Build.props");
    Assert.True(File.Exists(propsPath), "Coverage collector configuration must be centralized.");

    var props = XDocument.Load(propsPath);
    var collector = Assert.Single(
        props.Descendants("PackageReference"),
        reference => reference.Attribute("Include")?.Value == "coverlet.collector");
    Assert.Equal("10.0.1", collector.Attribute("Version")?.Value);
    Assert.Equal("all", collector.Element("PrivateAssets")?.Value);
    Assert.Equal(
        "runtime; build; native; contentfiles; analyzers; buildtransitive",
        collector.Element("IncludeAssets")?.Value);

    foreach (var project in Directory.EnumerateFiles(
                 Path.Combine(RepositoryRoot, "tests"),
                 "*.csproj",
                 SearchOption.AllDirectories))
    {
      var document = XDocument.Load(project);
      Assert.DoesNotContain(
          document.Descendants("PackageReference"),
          reference => reference.Attribute("Include")?.Value == "coverlet.collector");
    }
  }

  [Fact]
  public void InspectSmokeFingerprintsTheEntireRetiredStateRoot()
  {
    var script = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1"));

    Assert.Contains(
        "Get-TreeFingerprint (Join-Path $env:LOCALAPPDATA 'WinHome')",
        script,
        StringComparison.Ordinal);
    Assert.Matches(
        @"LegacyState\s*=\s*Get-TreeFingerprint\s+\(Join-Path\s+\$env:LOCALAPPDATA\s+'WinHome'\)",
        script);
    Assert.Matches(
        @"\$after\.LegacyState\s+-ne\s+\$Before\.LegacyState",
        script);
    Assert.DoesNotContain(
        "'WinHome\\Wdem\\runs'",
        script,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("CreationTimeUtc", script, StringComparison.Ordinal);
    Assert.Matches(@"AccessControlSections\]::Owner", script);
    Assert.Matches(@"AccessControlSections\]::Group", script);
    Assert.Matches(@"AccessControlSections\]::Access", script);
    Assert.DoesNotContain("::Audit", script, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("LastAccessTime", script, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("FindFirstStreamW", script, StringComparison.Ordinal);
    Assert.Contains("FindNextStreamW", script, StringComparison.Ordinal);
    Assert.Contains("CreateFileW", script, StringComparison.Ordinal);
    Assert.Contains("FILE_FLAG_BACKUP_SEMANTICS", script, StringComparison.Ordinal);
    Assert.Contains("FILE_FLAG_OPEN_REPARSE_POINT", script, StringComparison.Ordinal);
  }

  [Fact]
  public void RetiredStateFingerprintDetectsWholeTreeMutationsWithoutFollowingJunctions()
  {
    var script = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1"));
    var mainScript = script.IndexOf(
        "$root = Split-Path $PSScriptRoot",
        StringComparison.Ordinal);
    Assert.True(mainScript > 0, "Could not isolate the smoke fingerprint helpers.");

    using var directory = new TemporaryDirectory();
    var harnessPath = Path.Combine(directory.Path, "fingerprint-harness.ps1");
    var testRoot = directory.Path.Replace("'", "''", StringComparison.Ordinal);
    var harness = script[..mainScript] + $$"""
        $testRoot = '{{testRoot}}'
        $legacyRoot = Join-Path $testRoot 'WinHome'

        function Assert-FingerprintChanged([string]$Before, [string]$After, [string]$Case) {
            if ($Before -eq $After) { throw "Fingerprint missed $Case." }
        }

        function Reset-LegacyRoot {
            if (Test-Path -LiteralPath $legacyRoot) {
                Remove-Item -LiteralPath $legacyRoot -Recurse -Force
            }
        }

        Reset-LegacyRoot
        $before = Get-TreeFingerprint $legacyRoot
        New-Item -ItemType Directory -Path $legacyRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $legacyRoot 'state.json') -Value 'alpha'
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'creation outside Wdem/runs'

        $state = Join-Path $legacyRoot 'state.json'
        $stableTime = [DateTime]::UtcNow.AddHours(-2)
        [IO.File]::WriteAllText($state, 'alpha')
        [IO.File]::SetLastWriteTimeUtc($state, $stableTime)
        $before = Get-TreeFingerprint $legacyRoot
        [IO.File]::WriteAllText($state, 'bravo')
        [IO.File]::SetLastWriteTimeUtc($state, $stableTime)
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'same-length content change'

        $before = Get-TreeFingerprint $legacyRoot
        $creationTime = [IO.File]::GetCreationTimeUtc($state)
        [IO.File]::SetCreationTimeUtc($state, $creationTime.AddMinutes(-5))
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'creation-time change'

        $before = Get-TreeFingerprint $legacyRoot
        $acl = Get-Acl -LiteralPath $state
        $acl.SetAccessRuleProtection(-not $acl.AreAccessRulesProtected, $true)
        Set-Acl -LiteralPath $state -AclObject $acl
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'DACL change'

        $before = Get-TreeFingerprint $legacyRoot
        $creationTime = [IO.File]::GetCreationTimeUtc($state)
        $writeTime = [IO.File]::GetLastWriteTimeUtc($state)
        $adsSupported = $true
        try {
            Set-Content -LiteralPath $state -Stream 'wdem-test' -Value 'ads-alpha'
        }
        catch [System.NotSupportedException] {
            $adsSupported = $false
            Write-Output "SKIP ADS: $($_.Exception.Message)"
        }
        if ($adsSupported) {
            [IO.File]::SetCreationTimeUtc($state, $creationTime)
            [IO.File]::SetLastWriteTimeUtc($state, $writeTime)
            Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'alternate data stream change'
            Write-Output 'ADS mutation detected'
        }

        $before = Get-TreeFingerprint $legacyRoot
        [IO.File]::SetLastWriteTimeUtc($state, $stableTime.AddMinutes(1))
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'write-time change'

        $before = Get-TreeFingerprint $legacyRoot
        Move-Item -LiteralPath $state -Destination (Join-Path $legacyRoot 'renamed.json')
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'rename'

        $renamed = Join-Path $legacyRoot 'renamed.json'
        $before = Get-TreeFingerprint $legacyRoot
        Remove-Item -LiteralPath $renamed
        Assert-FingerprintChanged $before (Get-TreeFingerprint $legacyRoot) 'deletion'

        Reset-LegacyRoot
        New-Item -ItemType Directory -Path $legacyRoot | Out-Null
        $outside = Join-Path $testRoot 'outside'
        New-Item -ItemType Directory -Path $outside | Out-Null
        $outsideState = Join-Path $outside 'state.json'
        Set-Content -LiteralPath $outsideState -Value 'alpha'
        $junction = Join-Path $legacyRoot 'external'
        New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
        $before = Get-TreeFingerprint $legacyRoot
        Set-Content -LiteralPath $outsideState -Value 'changed outside legacy root'
        $after = Get-TreeFingerprint $legacyRoot
        if ($before -ne $after) { throw 'Fingerprint followed a junction outside the legacy root.' }

        $outsideCreationTime = [IO.Directory]::GetCreationTimeUtc($outside)
        [IO.Directory]::SetCreationTimeUtc($outside, $outsideCreationTime.AddMinutes(-5))
        $after = Get-TreeFingerprint $legacyRoot
        if ($before -ne $after) { throw 'Fingerprint followed junction target creation metadata.' }

        $outsideAcl = Get-Acl -LiteralPath $outside
        $outsideAcl.SetAccessRuleProtection(-not $outsideAcl.AreAccessRulesProtected, $true)
        Set-Acl -LiteralPath $outside -AclObject $outsideAcl
        $after = Get-TreeFingerprint $legacyRoot
        if ($before -ne $after) { throw 'Fingerprint followed junction target access control.' }
        [IO.Directory]::Delete($junction)
        """;
    File.WriteAllText(harnessPath, harness);

    var result = RunProcess(
        directory.Path,
        "powershell",
        "-NoLogo",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        harnessPath);

    Assert.True(result.ExitCode == 0, result.Output);
    Assert.True(
        result.Output.Contains("ADS mutation detected", StringComparison.Ordinal) ||
        result.Output.Contains("SKIP ADS:", StringComparison.Ordinal),
        $"ADS coverage was silently skipped.{Environment.NewLine}{result.Output}");
  }

  [Fact]
  public void RetiredStateFingerprintDetectsDirectoryAlternateDataStreams()
  {
    var script = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "inspect-smoke.ps1"));
    var mainScript = script.IndexOf(
        "$root = Split-Path $PSScriptRoot",
        StringComparison.Ordinal);
    Assert.True(mainScript > 0, "Could not isolate the smoke fingerprint helpers.");

    using var directory = new TemporaryDirectory();
    var harnessPath = Path.Combine(directory.Path, "directory-ads-harness.ps1");
    var testRoot = directory.Path.Replace("'", "''", StringComparison.Ordinal);
    var harness = script[..mainScript] + $$"""
        $testRoot = '{{testRoot}}'
        $legacyRoot = Join-Path $testRoot 'WinHome'
        $stateDirectory = Join-Path $legacyRoot 'state-directory'
        New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

        $adsSupported = $true
        try {
            Set-Content -LiteralPath $stateDirectory -Stream 'wdem-directory-test' -Value 'alpha'
        }
        catch [System.NotSupportedException] {
            $adsSupported = $false
            Write-Output "SKIP DIRECTORY ADS: $($_.Exception.Message)"
        }
        if ($adsSupported) {
            $before = Get-TreeFingerprint $legacyRoot
            $item = Get-Item -LiteralPath $stateDirectory -Force
            $attributes = [IO.FileAttributes]$item.Attributes
            $creationTime = [DateTime]$item.CreationTimeUtc
            $writeTime = [DateTime]$item.LastWriteTimeUtc
            $aclFingerprint = Get-AclFingerprint $stateDirectory
            $rootItem = Get-Item -LiteralPath $legacyRoot -Force
            $rootAttributes = [IO.FileAttributes]$rootItem.Attributes
            $rootCreationTime = [DateTime]$rootItem.CreationTimeUtc
            $rootWriteTime = [DateTime]$rootItem.LastWriteTimeUtc
            $rootAclFingerprint = Get-AclFingerprint $legacyRoot

            Set-Content -LiteralPath $stateDirectory -Stream 'wdem-directory-test' -Value 'bravo'
            [IO.File]::SetAttributes($stateDirectory, $attributes)
            [IO.Directory]::SetCreationTimeUtc($stateDirectory, $creationTime)
            [IO.Directory]::SetLastWriteTimeUtc($stateDirectory, $writeTime)
            [IO.File]::SetAttributes($legacyRoot, $rootAttributes)
            [IO.Directory]::SetCreationTimeUtc($legacyRoot, $rootCreationTime)
            [IO.Directory]::SetLastWriteTimeUtc($legacyRoot, $rootWriteTime)

            $restoredItem = Get-Item -LiteralPath $stateDirectory -Force
            $restoredRootItem = Get-Item -LiteralPath $legacyRoot -Force
            if ($restoredItem.Attributes -ne $attributes -or
                $restoredItem.CreationTimeUtc -ne $creationTime -or
                $restoredItem.LastWriteTimeUtc -ne $writeTime -or
                (Get-AclFingerprint $stateDirectory) -ne $aclFingerprint -or
                $restoredRootItem.Attributes -ne $rootAttributes -or
                $restoredRootItem.CreationTimeUtc -ne $rootCreationTime -or
                $restoredRootItem.LastWriteTimeUtc -ne $rootWriteTime -or
                (Get-AclFingerprint $legacyRoot) -ne $rootAclFingerprint) {
                throw "Directory ADS test could not restore fingerprinted metadata: state=[$($restoredItem.Attributes),$($restoredItem.CreationTimeUtc.Ticks),$($restoredItem.LastWriteTimeUtc.Ticks),$((Get-AclFingerprint $stateDirectory) -eq $aclFingerprint)] expected=[$attributes,$($creationTime.Ticks),$($writeTime.Ticks),True]; root=[$($restoredRootItem.Attributes),$($restoredRootItem.CreationTimeUtc.Ticks),$($restoredRootItem.LastWriteTimeUtc.Ticks),$((Get-AclFingerprint $legacyRoot) -eq $rootAclFingerprint)] expected=[$rootAttributes,$($rootCreationTime.Ticks),$($rootWriteTime.Ticks),True]"
            }
            $after = Get-TreeFingerprint $legacyRoot
            if ($before -eq $after) { throw 'Fingerprint missed directory alternate data stream change.' }
            Write-Output 'Directory ADS mutation detected'
        }
        """;
    File.WriteAllText(harnessPath, harness);

    var result = RunProcess(
        directory.Path,
        "powershell",
        "-NoLogo",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        harnessPath);

    Assert.True(result.ExitCode == 0, result.Output);
    Assert.True(
        result.Output.Contains("Directory ADS mutation detected", StringComparison.Ordinal) ||
        result.Output.Contains("SKIP DIRECTORY ADS:", StringComparison.Ordinal),
        $"Directory ADS coverage was silently skipped.{Environment.NewLine}{result.Output}");
  }

  [Fact]
  public void CleanVmApplyRefusesBeforeAnyMachineOrFileOperation()
  {
    var sourceScript = Path.Combine(
        RepositoryRoot,
        "testing",
        "wdem",
        "clean-vm-apply.ps1");
    Assert.True(File.Exists(sourceScript), "Missing WDEM clean-VM script.");

    using var directory = new TemporaryDirectory();
    var script = Path.Combine(directory.Path, "clean-vm-apply.ps1");
    File.Copy(sourceScript, script);
    var before = Directory.EnumerateFileSystemEntries(directory.Path)
        .Select(Path.GetFileName)
        .Order(StringComparer.Ordinal)
        .ToArray();

    var result = RunProcess(
        directory.Path,
        "pwsh",
        "-NoLogo",
        "-NoProfile",
        "-File",
        script);
    var after = Directory.EnumerateFileSystemEntries(directory.Path)
        .Select(Path.GetFileName)
        .Order(StringComparer.Ordinal)
        .ToArray();

    Assert.NotEqual(0, result.ExitCode);
    Assert.Contains(
        "Refusing to apply outside an explicitly confirmed disposable VM.",
        result.Output,
        StringComparison.Ordinal);
    Assert.Equal(before, after);

    var source = File.ReadAllText(sourceScript);
    var refusal = source.IndexOf("if (-not $Confirmed)", StringComparison.Ordinal);
    Assert.True(refusal >= 0, "The confirmation refusal gate is missing.");
    foreach (var operation in new[]
             {
               "Get-CimInstance",
               "Test-Path",
               "New-Item",
               "Copy-Item",
               "Wdem.Cli.exe"
             })
    {
      Assert.True(
          source.IndexOf(operation, StringComparison.OrdinalIgnoreCase) > refusal,
          $"{operation} must occur after the confirmation gate.");
    }
    Assert.Contains("Cli\\Wdem.Cli.exe", source, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("publish\\Wdem.Cli", source, StringComparison.OrdinalIgnoreCase);
    Assert.Matches(
        @"foreach \(\$resourceId in @\('git', 'dotnet-sdk', 'visual-studio',\s*" +
        @"'resharper', 'resharper-settings', 'company-vs-extension',\s*" +
        @"'visual-studio-settings'\)\)",
        source);
  }

  [Fact]
  public void GettingStartedLimitsApplyAcceptanceToAConfirmedDisposableVm()
  {
    var guide = NormalizeWhitespace(File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "wdem",
        "getting-started.md")));

    Assert.Contains("disposable Windows 11 x64", guide, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("snapshot", guide, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("clean-vm-apply.ps1 -Confirmed", guide, StringComparison.Ordinal);
    Assert.Contains("WDEM product release", guide, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("no upstream merge or pull-request workflow", guide, StringComparison.OrdinalIgnoreCase);
  }

  private static string NormalizeWhitespace(string value) =>
      string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

  private static string ExtractMarkdownBullet(string text, string marker)
  {
    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var start = Array.FindIndex(
        lines,
        line => line.StartsWith("- ", StringComparison.Ordinal) &&
            line.Contains(marker, StringComparison.Ordinal));
    Assert.True(start >= 0, $"Missing Markdown bullet containing: {marker}");

    var bullet = new List<string> { lines[start] };
    for (var index = start + 1;
         index < lines.Length && lines[index].StartsWith("  ", StringComparison.Ordinal);
         index++)
    {
      bullet.Add(lines[index]);
    }

    return NormalizeWhitespace(string.Join('\n', bullet));
  }

  private static string ExtractSection(string text, string startMarker, string endMarker)
  {
    var start = text.IndexOf(startMarker, StringComparison.Ordinal);
    Assert.True(start >= 0, $"Missing section marker: {startMarker}");
    var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
    Assert.True(end > start, $"Missing section marker: {endMarker}");
    return text[start..end];
  }

  private static ProcessResult RunProcess(
      string workingDirectory,
      string executable,
      params string[] arguments)
  {
    var startInfo = new ProcessStartInfo(executable)
    {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (var argument in arguments)
    {
      startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ??
        throw new InvalidOperationException($"Could not start {executable}.");
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, standardOutput + standardError);
  }

  private sealed record ProcessResult(int ExitCode, string Output);

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(),
          $"wdem-identity-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
      foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
      {
        File.SetAttributes(file, FileAttributes.Normal);
      }

      Directory.Delete(Path, recursive: true);
    }
  }

  [Fact]
  public void ProjectIdentitiesDoNotUseWinHome()
  {
    foreach (var project in Directory.EnumerateFiles(
        RepositoryRoot,
        "*.csproj",
        SearchOption.AllDirectories))
    {
      var document = XDocument.Load(project);
      var assemblyName = document.Descendants("AssemblyName").Select(element => element.Value);
      var rootNamespace = document.Descendants("RootNamespace").Select(element => element.Value);

      Assert.DoesNotContain("WinHome", assemblyName);
      Assert.DoesNotContain("WinHome", rootNamespace);
    }
  }

  [Fact]
  public void SourceNamespacesDoNotUseWinHome()
  {
    var sourceDirectory = Path.Combine(RepositoryRoot, "src");

    foreach (var sourceFile in Directory.EnumerateFiles(
        sourceDirectory,
        "*.cs",
        SearchOption.AllDirectories))
    {
      var source = File.ReadAllText(sourceFile);
      Assert.DoesNotMatch(@"(?m)^\s*namespace\s+WinHome(?:\.|;)", source);
    }
  }

  private static string FindRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
        directory is not null;
        directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.sln")) ||
          File.Exists(Path.Combine(directory.FullName, "WinHome.sln")))
      {
        return directory.FullName;
      }
    }

    throw new DirectoryNotFoundException("Could not locate the repository root.");
  }
}
