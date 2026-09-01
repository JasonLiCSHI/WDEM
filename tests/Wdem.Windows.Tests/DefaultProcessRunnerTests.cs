using System.Diagnostics;
using System.Text;
using Wdem.Core.Runs;
using Wdem.Windows.Processes;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class DefaultProcessRunnerTests
{
  [Fact]
  public async Task VisualStudioApply_GuiInstallerReportsItsExitCode()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(
        repositoryRoot,
        "script",
        "Invoke-VisualStudioProfessionalTask.ps1");
    var configPath = Path.Combine(repositoryRoot, "settings", ".vsconfig");
    var payload = $$"""
        function Get-Process {
            param([string] $Name, $ErrorAction)
            if ($Name -eq 'devenv') { return }
            Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
        }
        function Invoke-WebRequest {
            param($Uri, $OutFile, $MaximumRedirection)
            Add-Type -TypeDefinition 'using System; public static class FakeInstaller { [STAThread] public static void Main(string[] args) { Environment.Exit(23); } }' -OutputAssembly $OutFile -OutputType WindowsApplication
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://aka.ms/fake-vs-installer' -ConfigPath '{{EscapePowerShellLiteral(configPath)}}'
        """;
    var result = await RunPowerShellAsync(payload);

    Assert.Equal(1, result.ExitCode);
    Assert.Contains("Visual Studio Installer failed with exit code 23", result.StandardError);
    Assert.DoesNotContain(
        "LASTEXITCODE",
        result.StandardError,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains(
        "Downloading the Visual Studio Professional bootstrapper",
        result.StandardOutput);
  }

  [Fact]
  public async Task ReSharperApply_GuiInstallerReportsItsExitCode()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-ReSharperTask.ps1");
    var fakeVsWherePath = Path.Combine(
        Path.GetTempPath(),
        $"WDEM-fake-vswhere-{Guid.NewGuid():N}.exe");
    var payload = $$"""
        Add-Type -TypeDefinition 'using System; public static class FakeVsWhere { public static void Main(string[] args) { Console.WriteLine(@"C:\Fake VS"); } }' -OutputAssembly '{{EscapePowerShellLiteral(fakeVsWherePath)}}' -OutputType ConsoleApplication
        function Join-Path {
            param([string] $Path, [string] $ChildPath)
            if ($ChildPath -eq 'Microsoft Visual Studio\Installer\vswhere.exe') { return '{{EscapePowerShellLiteral(fakeVsWherePath)}}' }
            Microsoft.PowerShell.Management\Join-Path @PSBoundParameters
        }
        function Get-Process {
            param([string] $Name, $ErrorAction)
            if ($Name -eq 'devenv') { return }
            Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
        }
        function Invoke-WebRequest {
            param($Uri, $OutFile, $MaximumRedirection)
            Add-Type -TypeDefinition 'using System; public static class FakeInstaller { [STAThread] public static void Main(string[] args) { Environment.Exit(23); } }' -OutputAssembly $OutFile -OutputType WindowsApplication
        }
        function Get-FileHash {
            param($LiteralPath, $Algorithm)
            [pscustomobject] @{ Hash = ('A' * 64) }
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://download.jetbrains.com/fake-resharper.exe' -Sha256 ('A' * 64)
        """;

    try
    {
      var result = await RunPowerShellAsync(payload);

      Assert.Equal(1, result.ExitCode);
      Assert.Contains("ReSharper Installer failed with exit code 23", result.StandardError);
      Assert.DoesNotContain(
          "LASTEXITCODE",
          result.StandardError,
          StringComparison.OrdinalIgnoreCase);
      Assert.Contains("Downloading ReSharper from JetBrains", result.StandardOutput);
    }
    finally
    {
      File.Delete(fakeVsWherePath);
    }
  }

  [Fact]
  public async Task RunAsync_CancellationTerminatesTheStartedProcessTree()
  {
    var childProcessId = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new InlineProgress<ProcessOutput>(line =>
    {
      if (line.Stream == WorkflowOutputStream.StandardOutput &&
          int.TryParse(line.Message, out var processId))
      {
        childProcessId.TrySetResult(processId);
      }
    });
    var script =
        "$child = Start-Process -FilePath $env:ComSpec " +
        "-ArgumentList '/d','/c','ping -t 127.0.0.1' -WindowStyle Hidden -PassThru; " +
        "[Console]::Out.WriteLine($child.Id); Wait-Process -Id $child.Id";
    var request = new ProcessRequest(
        "powershell.exe",
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
    using var cancellation = new CancellationTokenSource();
    var runner = new DefaultProcessRunner();

    var running = runner.RunAsync(request, output, cancellation.Token);
    var childId = await childProcessId.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    Assert.True(
        await WaitUntilExitedAsync(childId, TimeSpan.FromSeconds(10)),
        $"Child process {childId} was still running after cancellation.");
  }

  [Fact]
  public async Task RunAsync_PreCancelledTokenDoesNotStartAProcess()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var runner = new DefaultProcessRunner();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        runner.RunAsync(
            new ProcessRequest("this-command-must-never-start.exe", []),
            output: null,
            cancellation.Token));
  }

  private static async Task<bool> WaitUntilExitedAsync(int processId, TimeSpan timeout)
  {
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
      try
      {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
          return true;
        }
      }
      catch (ArgumentException)
      {
        return true;
      }

      await Task.Delay(100);
    }

    return false;
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(
              directory.FullName,
              "script",
              "Invoke-VisualStudioProfessionalTask.ps1")))
      {
        return directory.FullName;
      }

      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the WDEM repository root.");
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

  private sealed record PowerShellResult(
      int ExitCode,
      string StandardOutput,
      string StandardError);

  private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
