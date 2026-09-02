using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runs;
using Wdem.Core.Runtime;
using Wdem.Core.Tasks;
using Wdem.Windows.Processes;
using Wdem.Windows.Runtime;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class TaskContractTests
{
  [Fact]
  public async Task RepositoryProfile_AllCommandPathsExpandToPublishedAssets()
  {
    var repositoryRoot = FindRepositoryRoot();
    var profile = LoadRepositoryProfile(repositoryRoot);
    var commands = EnumerateCommands(profile).ToArray();
    var runner = new CapturingProcessRunner();
    var runtime = new WindowsTaskRuntime(runner, repositoryRoot);

    foreach (var command in commands)
    {
      await runtime.RunAsync(
          new CommandInvocation(
              command.Task.Id,
              command.Phase,
              command.Command,
              command.Task.Source,
              command.Task.PreferredVersion),
          output: null,
          CancellationToken.None);
    }

    Assert.Equal(8, commands.Length);
    Assert.Equal(commands.Length, runner.Requests.Count);
    foreach (var request in runner.Requests)
    {
      Assert.Equal("powershell.exe", request.FileName, StringComparer.OrdinalIgnoreCase);
      Assert.DoesNotContain(request.Arguments, argument => argument.Contains('{', StringComparison.Ordinal));
      AssertExistingRepositoryFile(request, "-File", repositoryRoot);

      if (request.Arguments.Contains("-ConfigPath", StringComparer.Ordinal))
      {
        AssertExistingRepositoryFile(request, "-ConfigPath", repositoryRoot);
      }
      if (request.Arguments.Contains("-SettingsPath", StringComparer.Ordinal))
      {
        AssertExistingRepositoryFile(request, "-SettingsPath", repositoryRoot);
      }
    }

    var visualStudioApply = FindRequest(commands, runner.Requests, "visual-studio-professional", "apply");
    Assert.Equal(profile.Tasks["visual-studio-professional"].Source, ValueAfter(visualStudioApply, "-SourceUri"));
    Assert.EndsWith(Path.Combine("settings", ".vsconfig"), ValueAfter(visualStudioApply, "-ConfigPath"));

    var reSharperApply = FindRequest(commands, runner.Requests, "resharper", "apply");
    Assert.Equal(profile.Tasks["resharper"].Source, ValueAfter(reSharperApply, "-SourceUri"));
    Assert.Matches("^[a-f0-9]{64}$", ValueAfter(reSharperApply, "-Sha256"));

    var reSharperPost = FindRequest(commands, runner.Requests, "resharper", "post");
    Assert.EndsWith(Path.Combine("settings", "CT.DotSettings"), ValueAfter(reSharperPost, "-SettingsPath"));
  }

  [Fact]
  public void InstallerDefinitions_PackageTaskAssetsAndExcludeProfiles()
  {
    var repositoryRoot = FindRepositoryRoot();
    var installerLines = File.ReadAllLines(Path.Combine(repositoryRoot, "installer", "Wdem.iss"));
    var packagedSources = installerLines
        .Where(line => line.TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var buildLines = File.ReadAllLines(Path.Combine(repositoryRoot, "build", "Build-Installer.ps1"));
    var copiedDirectories = buildLines
        .Where(line => line.Contains("Copy-Item", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    Assert.Contains(packagedSources, line =>
        line.Contains(@"{#PublishRoot}\script\*", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(packagedSources, line =>
        line.Contains(@"{#PublishRoot}\settings\*", StringComparison.OrdinalIgnoreCase));
    Assert.DoesNotContain(packagedSources, line =>
        line.Contains("profile", StringComparison.OrdinalIgnoreCase));

    Assert.Contains(copiedDirectories, line =>
        line.Contains("'script'", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(copiedDirectories, line =>
        line.Contains("'settings'", StringComparison.OrdinalIgnoreCase));
    Assert.DoesNotContain(copiedDirectories, line =>
        line.Contains("'profiles'", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task RepositoryProfile_ExecutesBothTaskPipelinesInDependencyOrder()
  {
    var profile = LoadRepositoryProfile(FindRepositoryRoot());
    var graph = TaskGraph.Build(profile, rootTaskIds: ["resharper"]);
    var runtime = new RepositoryContractRuntime();

    var report = await EnvironmentManager.StartApply(profile, graph, runtime).Completion;

    Assert.Equal(
        ["visual-studio-professional", "resharper"],
        graph.OrderedTaskIds);
    Assert.Equal(
        [
          ("visual-studio-professional", "detect"),
          ("visual-studio-professional", "pre"),
          ("visual-studio-professional", "apply"),
          ("visual-studio-professional", "post"),
          ("visual-studio-professional", "verify"),
          ("resharper", "detect"),
          ("resharper", "pre"),
          ("resharper", "apply"),
          ("resharper", "post"),
          ("resharper", "verify")
        ],
        runtime.Invocations);
    Assert.All(report.Tasks.Values, task => Assert.Equal(TaskOutcome.Succeeded, task.Outcome));
    Assert.Equal(
        ["detect", "pre", "apply", "post", "verify"],
        report.Tasks["visual-studio-professional"].Steps.Select(step => step.Phase));
    Assert.Equal(
        ["detect", "pre", "apply", "post", "verify"],
        report.Tasks["resharper"].Steps.Select(step => step.Phase));
  }

  [Fact]
  public async Task RepositoryProfile_VisualStudioFailureBlocksReSharperBeforeDetection()
  {
    var profile = LoadRepositoryProfile(FindRepositoryRoot());
    var graph = TaskGraph.Build(profile, rootTaskIds: ["resharper"]);
    var runtime = new RepositoryContractRuntime(failVisualStudioApply: true);

    var report = await EnvironmentManager.StartApply(profile, graph, runtime).Completion;

    Assert.Equal(TaskOutcome.Failed, report.Tasks["visual-studio-professional"].Outcome);
    Assert.Equal(TaskOutcome.Blocked, report.Tasks["resharper"].Outcome);
    Assert.Empty(report.Tasks["resharper"].Steps);
    Assert.DoesNotContain(runtime.Invocations, invocation => invocation.TaskId == "resharper");
    Assert.Equal(
        [
          ("visual-studio-professional", "detect"),
          ("visual-studio-professional", "pre"),
          ("visual-studio-professional", "apply")
        ],
        runtime.Invocations);
  }

  [Theory]
  [InlineData("Detect")]
  [InlineData("Post")]
  public async Task VisualStudioReadOnlyActions_UseVsWhereAndBundledConfiguration(string action)
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-VisualStudioProfessionalTask.ps1");
    var configPath = Path.Combine(repositoryRoot, "settings", ".vsconfig");
    var fakeVsWherePath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-vswhere-{Guid.NewGuid():N}.exe");
    var capturedArgumentsPath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-vswhere-args-{Guid.NewGuid():N}.txt");
    var payload = $$"""
        Add-Type -TypeDefinition 'using System; using System.IO; public static class FakeVsWhere { public static void Main(string[] args) { File.WriteAllLines(Environment.GetEnvironmentVariable("WDEM_VSWHERE_ARGS"), args); Console.WriteLine("18.9.2"); } }' -OutputAssembly '{{EscapePowerShellLiteral(fakeVsWherePath)}}' -OutputType ConsoleApplication
        function Join-Path {
            param([string] $Path, [string] $ChildPath)
            if ($ChildPath -eq 'Microsoft Visual Studio\Installer\vswhere.exe') { return '{{EscapePowerShellLiteral(fakeVsWherePath)}}' }
            Microsoft.PowerShell.Management\Join-Path @PSBoundParameters
        }
        function Test-Path {
            param($LiteralPath, $PathType)
            if ($LiteralPath -eq '{{EscapePowerShellLiteral(fakeVsWherePath)}}') { return $true }
            Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
        }
        $env:WDEM_VSWHERE_ARGS = '{{EscapePowerShellLiteral(capturedArgumentsPath)}}'
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action '{{action}}' -ConfigPath '{{EscapePowerShellLiteral(configPath)}}'
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);
      var arguments = await File.ReadAllLinesAsync(capturedArgumentsPath);

      Assert.Equal(0, result.ExitCode);
      Assert.Contains("-products", arguments);
      Assert.Contains("Microsoft.VisualStudio.Product.Professional", arguments);
      Assert.Contains("-version", arguments);
      Assert.Contains("[18.0,19.0)", arguments);
      Assert.Contains("Visual Studio Professional version 18.9.2", result.StandardOutput);

      if (action == "Post")
      {
        using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var requiredComponents = configuration.RootElement
            .GetProperty("components")
            .EnumerateArray()
            .Select(component => component.GetString())
            .ToArray();
        Assert.Contains("-requires", arguments);
        Assert.All(requiredComponents, component => Assert.Contains(component, arguments));
        Assert.Contains("contains all declared components", result.StandardOutput);
      }
      else
      {
        Assert.DoesNotContain("-requires", arguments);
      }
    }
    finally
    {
      File.Delete(fakeVsWherePath);
      File.Delete(capturedArgumentsPath);
    }
  }

  [Fact]
  public async Task VisualStudioPre_ValidatesTheBundledConfigurationWithoutDownloading()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-VisualStudioProfessionalTask.ps1");
    var configPath = Path.Combine(repositoryRoot, "settings", ".vsconfig");
    var payload = $$"""
        function Get-Process { param([string] $Name, $ErrorAction); if ($Name -eq 'devenv') { return } }
        function Invoke-WebRequest { throw 'Pre must not download an installer.' }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Pre -SourceUri 'https://aka.ms/vs/18/stable/vs_professional.exe' -ConfigPath '{{EscapePowerShellLiteral(configPath)}}'
        """;

    var result = await RunPowerShellAsync(payload);

    Assert.True(result.ExitCode == 0, result.CombinedOutput);
    Assert.Contains("Visual Studio preflight passed", result.StandardOutput);
    Assert.Contains("components are declared", result.StandardOutput);
    Assert.DoesNotContain("Downloading", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1641)]
  [InlineData(3010)]
  public async Task VisualStudioApply_UsesInteractiveInstallerAndAcceptsDocumentedSuccessCodes(int installerExitCode)
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-VisualStudioProfessionalTask.ps1");
    var configPath = Path.Combine(repositoryRoot, "settings", ".vsconfig");
    var capturedArgumentsPath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-vs-args-{Guid.NewGuid():N}.txt");
    var waitMarkerPath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-vs-wait-{Guid.NewGuid():N}.txt");
    var payload = $$"""
        function Get-Process { param([string] $Name, $ErrorAction); if ($Name -eq 'devenv') { return } }
        function Invoke-WebRequest { param($Uri, $OutFile, $MaximumRedirection); Set-Content -LiteralPath $OutFile -Value 'fake' }
        function Start-Process {
            param([string] $FilePath, [string[]] $ArgumentList, [switch] $NoNewWindow, [switch] $Wait, [switch] $PassThru)
            if ($Wait) { throw 'Start-Process must not own descendant-process waiting.' }
            if ($NoNewWindow) { throw 'The interactive installer must be allowed to create its window.' }
            if (-not $PassThru) { throw 'The installer process handle is required.' }
            Set-Content -LiteralPath '{{EscapePowerShellLiteral(capturedArgumentsPath)}}' -Value $ArgumentList
            [pscustomobject] @{ ExitCode = {{installerExitCode}} }
        }
        function Wait-Process {
            param([Parameter(ValueFromPipeline = $true)] $InputObject)
            process { Set-Content -LiteralPath '{{EscapePowerShellLiteral(waitMarkerPath)}}' -Value 'waited' }
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://aka.ms/vs/18/stable/vs_professional.exe' -ConfigPath '{{EscapePowerShellLiteral(configPath)}}'
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);
      var installerArguments = await File.ReadAllLinesAsync(capturedArgumentsPath);

      Assert.Equal(0, result.ExitCode);
      Assert.True(File.Exists(waitMarkerPath), "The script did not wait for the launched installer process.");
      Assert.DoesNotContain("--quiet", installerArguments);
      Assert.DoesNotContain("--passive", installerArguments);
      Assert.Contains("--wait", installerArguments);
      Assert.Contains("--norestart", installerArguments);
      Assert.Contains("--config", installerArguments);
      Assert.Contains(installerArguments, argument => argument.Contains(configPath, StringComparison.OrdinalIgnoreCase));
      Assert.DoesNotContain("--allowUnsignedExtensions", installerArguments);
      if (installerExitCode == 0)
      {
        Assert.DoesNotContain("restart is required", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
      }
      else
      {
        Assert.Contains($"exit code {installerExitCode}", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart is required", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
      }
    }
    finally
    {
      File.Delete(capturedArgumentsPath);
      File.Delete(waitMarkerPath);
    }
  }

  [Fact]
  public async Task ReSharperDetect_ReturnsTheHighestInstalledSupportedVersion()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-ReSharperTask.ps1");
    var payload = $$"""
        function Test-Path { param($LiteralPath, $PathType); return $true }
        function Get-ChildItem {
            param($LiteralPath, $ErrorAction)
            $version = if ($LiteralPath -like 'HKCU:*') { '2026.1.0.1' } elseif ($LiteralPath -like '*WOW6432Node*') { 'invalid' } else { '2026.2.1' }
            [pscustomobject] @{ DisplayName = 'JetBrains ReSharper in Visual Studio Professional 2026'; DisplayVersion = $version }
        }
        function Get-ItemProperty {
            param([Parameter(ValueFromPipeline = $true)] $InputObject)
            process { $InputObject }
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Detect
        """;

    var result = await RunPowerShellAsync(payload);

    Assert.True(result.ExitCode == 0, result.CombinedOutput);
    Assert.Contains("JetBrains ReSharper version 2026.2.1", result.StandardOutput);
    Assert.DoesNotContain("invalid", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ReSharperPre_RequiresTheDeclaredVisualStudioHostWithoutDownloading()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-ReSharperTask.ps1");
    var fakeVsWherePath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-rs-vswhere-{Guid.NewGuid():N}.exe");
    var payload = $$"""
        Add-Type -TypeDefinition 'using System; public static class FakeVsWhere { public static void Main(string[] args) { Console.WriteLine(@"C:\Fake VS"); } }' -OutputAssembly '{{EscapePowerShellLiteral(fakeVsWherePath)}}' -OutputType ConsoleApplication
        function Join-Path { param([string] $Path, [string] $ChildPath); if ($ChildPath -eq 'Microsoft Visual Studio\Installer\vswhere.exe') { return '{{EscapePowerShellLiteral(fakeVsWherePath)}}' }; Microsoft.PowerShell.Management\Join-Path @PSBoundParameters }
        function Test-Path { param($LiteralPath, $PathType); if ($LiteralPath -eq '{{EscapePowerShellLiteral(fakeVsWherePath)}}') { return $true }; Microsoft.PowerShell.Management\Test-Path @PSBoundParameters }
        function Get-Process { param([string] $Name, $ErrorAction); if ($Name -eq 'devenv') { return } }
        function Invoke-WebRequest { throw 'Pre must not download an installer.' }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Pre -SourceUri 'https://download.jetbrains.com/resharper/fake.exe' -Sha256 ('A' * 64)
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);

      Assert.Equal(0, result.ExitCode);
      Assert.Contains("ReSharper preflight passed", result.StandardOutput);
      Assert.DoesNotContain("Downloading", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      File.Delete(fakeVsWherePath);
    }
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1641)]
  [InlineData(3010)]
  public async Task ReSharperApply_UsesInteractiveInstallerAndAcceptsDocumentedSuccessCodes(int installerExitCode)
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-ReSharperTask.ps1");
    var fakeVsWherePath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-rs-vswhere-{Guid.NewGuid():N}.exe");
    var capturedArgumentsPath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-rs-args-{Guid.NewGuid():N}.txt");
    var waitMarkerPath = Path.Combine(Path.GetTempPath(), $"WDEM-contract-rs-wait-{Guid.NewGuid():N}.txt");
    var payload = $$"""
        Add-Type -TypeDefinition 'using System; public static class FakeVsWhere { public static void Main(string[] args) { Console.WriteLine(@"C:\Fake VS"); } }' -OutputAssembly '{{EscapePowerShellLiteral(fakeVsWherePath)}}' -OutputType ConsoleApplication
        function Join-Path { param([string] $Path, [string] $ChildPath); if ($ChildPath -eq 'Microsoft Visual Studio\Installer\vswhere.exe') { return '{{EscapePowerShellLiteral(fakeVsWherePath)}}' }; Microsoft.PowerShell.Management\Join-Path @PSBoundParameters }
        function Test-Path { param($LiteralPath, $PathType); if ($LiteralPath -eq '{{EscapePowerShellLiteral(fakeVsWherePath)}}') { return $true }; Microsoft.PowerShell.Management\Test-Path @PSBoundParameters }
        function Get-Process { param([string] $Name, $ErrorAction); if ($Name -eq 'devenv') { return } }
        function Invoke-WebRequest { param($Uri, $OutFile, $MaximumRedirection); Set-Content -LiteralPath $OutFile -Value 'fake' }
        function Get-FileHash { param($LiteralPath, $Algorithm); [pscustomobject] @{ Hash = ('A' * 64) } }
        function Start-Process {
            param([string] $FilePath, [string[]] $ArgumentList, [switch] $NoNewWindow, [switch] $Wait, [switch] $PassThru)
            if ($Wait) { throw 'Start-Process must not own descendant-process waiting.' }
            if ($NoNewWindow) { throw 'The interactive installer must be allowed to create its window.' }
            if (-not $PassThru) { throw 'The installer process handle is required.' }
            Set-Content -LiteralPath '{{EscapePowerShellLiteral(capturedArgumentsPath)}}' -Value @($ArgumentList)
            [pscustomobject] @{ ExitCode = {{installerExitCode}} }
        }
        function Wait-Process {
            param([Parameter(ValueFromPipeline = $true)] $InputObject)
            process { Set-Content -LiteralPath '{{EscapePowerShellLiteral(waitMarkerPath)}}' -Value 'waited' }
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://download.jetbrains.com/resharper/fake.exe' -Sha256 ('A' * 64)
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);
      var installerArguments = File.Exists(capturedArgumentsPath)
          ? await File.ReadAllLinesAsync(capturedArgumentsPath)
          : [];

      Assert.Equal(0, result.ExitCode);
      Assert.True(File.Exists(waitMarkerPath), "The script did not wait for the launched installer process.");
      Assert.Empty(installerArguments);
      if (installerExitCode == 0)
      {
        Assert.DoesNotContain("restart is required", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
      }
      else
      {
        Assert.Contains($"exit code {installerExitCode}", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart is required", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
      }
    }
    finally
    {
      File.Delete(fakeVsWherePath);
      File.Delete(capturedArgumentsPath);
      File.Delete(waitMarkerPath);
    }
  }

  [Fact]
  public async Task ReSharperPost_AppliesBundledSettingsToAnIsolatedTarget()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Apply-ReSharperSettings.ps1");
    var settingsPath = Path.Combine(repositoryRoot, "settings", "CT.DotSettings");
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"WDEM-contract-settings-{Guid.NewGuid():N}");
    var targetPath = Path.Combine(temporaryDirectory, "GlobalSettingsStorage.DotSettings");
    var payload = $$"""
        & '{{EscapePowerShellLiteral(scriptPath)}}' -SettingsPath '{{EscapePowerShellLiteral(settingsPath)}}' -TargetPath '{{EscapePowerShellLiteral(targetPath)}}' -AllowRunningVisualStudio
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);

      Assert.Equal(0, result.ExitCode);
      Assert.True(File.Exists(targetPath));
      Assert.Contains("Applied", result.StandardOutput);
      Assert.Contains(targetPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(ReadSettingEntries(settingsPath), ReadSettingEntries(targetPath));
      Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.backup-*"));
    }
    finally
    {
      if (Directory.Exists(temporaryDirectory))
      {
        Directory.Delete(temporaryDirectory, recursive: true);
      }
    }
  }

  private static EnvironmentProfile LoadRepositoryProfile(string repositoryRoot) =>
      ProfileParser.Parse(File.ReadAllText(
          Path.Combine(repositoryRoot, "profiles", "csharp-developer.json")));

  private static IEnumerable<ProfileCommand> EnumerateCommands(EnvironmentProfile profile)
  {
    foreach (var task in profile.Tasks.Values)
    {
      yield return new ProfileCommand(task, "detect", task.Detect);
      foreach (var command in task.Pre)
      {
        yield return new ProfileCommand(task, "pre", command);
      }
      if (task.Apply is not null)
      {
        yield return new ProfileCommand(task, "apply", task.Apply);
      }
      foreach (var command in task.Post)
      {
        yield return new ProfileCommand(task, "post", command);
      }
    }
  }

  private static ProcessRequest FindRequest(
      IReadOnlyList<ProfileCommand> commands,
      IReadOnlyList<ProcessRequest> requests,
      string taskId,
      string phase)
  {
    var index = Array.FindIndex(
        commands.ToArray(),
        command => command.Task.Id == taskId && command.Phase == phase);
    Assert.True(index >= 0, $"Command {taskId}/{phase} was not declared.");
    return requests[index];
  }

  private static void AssertExistingRepositoryFile(
      ProcessRequest request,
      string option,
      string repositoryRoot)
  {
    var path = ValueAfter(request, option);
    var fullPath = Path.GetFullPath(path);
    Assert.StartsWith(
        Path.TrimEndingDirectorySeparator(repositoryRoot) + Path.DirectorySeparatorChar,
        fullPath,
        StringComparison.OrdinalIgnoreCase);
    Assert.True(File.Exists(fullPath), $"Profile option {option} references missing file '{fullPath}'.");
  }

  private static string ValueAfter(ProcessRequest request, string option)
  {
    var index = request.Arguments.ToList().FindIndex(argument =>
        string.Equals(argument, option, StringComparison.Ordinal));
    Assert.True(index >= 0 && index + 1 < request.Arguments.Count, $"Missing value for {option}.");
    return request.Arguments[index + 1];
  }

  private static IReadOnlyList<string> ReadSettingEntries(string path)
  {
    var document = new XmlDocument();
    document.Load(path);
    var namespaceManager = new XmlNamespaceManager(document.NameTable);
    namespaceManager.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
    return document.DocumentElement!
        .SelectNodes("*[@x:Key]", namespaceManager)!
        .Cast<XmlElement>()
        .Select(element => string.Concat(
            element.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml"),
            "\u001f",
            element.OuterXml))
        .OrderBy(entry => entry, StringComparer.Ordinal)
        .ToArray();
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.slnx")))
      {
        return directory.FullName;
      }
      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Unable to locate the WDEM repository root.");
  }

  private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

  private static async Task<PowerShellResult> RunPowerShellAsync(string payload)
  {
    var encodedPayload = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
    var startInfo = new ProcessStartInfo(
        "powershell.exe",
        $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedPayload}")
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };

    using var process = Process.Start(startInfo)!;
    var standardOutput = await process.StandardOutput.ReadToEndAsync();
    var standardError = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return new PowerShellResult(process.ExitCode, standardOutput, standardError);
  }

  private sealed record ProfileCommand(
      TaskDefinition Task,
      string Phase,
      CommandDefinition Command);

  private sealed record PowerShellResult(
      int ExitCode,
      string StandardOutput,
      string StandardError)
  {
    public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
  }

  private sealed class CapturingProcessRunner : IProcessRunner
  {
    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return Task.FromResult(new ProcessResult(true, 0, string.Empty, string.Empty));
    }
  }

  private sealed class RepositoryContractRuntime(bool failVisualStudioApply = false) : ITaskRuntime
  {
    private readonly List<(string TaskId, string Phase)> _invocations = [];

    public IReadOnlyList<(string TaskId, string Phase)> Invocations => _invocations;

    public Task<CommandResult> RunAsync(
        CommandInvocation invocation,
        IProgress<CommandOutput>? output,
        CancellationToken cancellationToken)
    {
      _invocations.Add((invocation.TaskId, invocation.Phase));
      if (failVisualStudioApply &&
          invocation.TaskId == "visual-studio-professional" &&
          invocation.Phase == "apply")
      {
        return Task.FromResult(new CommandResult(23, string.Empty, "fake installer failure"));
      }

      return Task.FromResult(invocation.Phase switch
      {
        "detect" => new CommandResult(1, string.Empty, "not installed"),
        "verify" when invocation.TaskId == "visual-studio-professional" =>
            new CommandResult(0, "Visual Studio Professional version 18.9.2", string.Empty),
        "verify" when invocation.TaskId == "resharper" =>
            new CommandResult(0, "JetBrains ReSharper version 2026.2.1", string.Empty),
        _ => new CommandResult(0, string.Empty, string.Empty)
      });
    }
  }
}
