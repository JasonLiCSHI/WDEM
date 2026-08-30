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
        @"(?ms)- uses: actions/checkout@v7\s+with:\s+ref:\s*\$\{\{ inputs\.tag_name \|\| github\.ref \}\}",
        workflow);
    Assert.Matches(
        @"uses: softprops/action-gh-release@[0-9a-f]{40}\s+# v3\.\d+\.\d+",
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

    Assert.Contains("Wdem.Cli.csproj", script, StringComparison.Ordinal);
    Assert.Matches(@"(?i)\binspect\b.*--profile.*--json.*--report", script);
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
