using System.Diagnostics;
using Wdem.Application.Runtime;
using Wdem.Windows.Processes;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class DefaultProcessRunnerTests : PowerShellScriptTestBase
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
    var capturedArgumentsPath = TestPath("visual-studio-arguments.txt");
    var childProcessIdPath = TestPath("visual-studio-child.txt");
    var payload = $$"""
        function Get-Process {
            param([string] $Name, $ErrorAction)
            if ($Name -eq 'devenv') { return }
            Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
        }
        function Save-WdemRemoteFile {
            param($SourceUri, $DestinationPath)
            Add-Type -TypeDefinition 'using System; using System.Diagnostics; using System.IO; public static class FakeInstaller { [STAThread] public static void Main(string[] args) { File.WriteAllLines(Environment.GetEnvironmentVariable("WDEM_FAKE_ARGS_PATH"), args); var child = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec"), "/d /c ping -t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false }); File.WriteAllText(Environment.GetEnvironmentVariable("WDEM_FAKE_CHILD_PATH"), child.Id.ToString()); Environment.Exit(23); } }' -OutputAssembly $DestinationPath -OutputType WindowsApplication
        }
        function Invoke-WebRequest { throw 'Apply must use the shared reliable downloader.' }
        $env:WDEM_FAKE_ARGS_PATH = '{{EscapePowerShellLiteral(capturedArgumentsPath)}}'
        $env:WDEM_FAKE_CHILD_PATH = '{{EscapePowerShellLiteral(childProcessIdPath)}}'
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://aka.ms/fake-vs-installer' -ConfigPath '{{EscapePowerShellLiteral(configPath)}}'
        """;

    try
    {
      var result = await RunPowerShellAsync(payload).WaitAsync(TimeSpan.FromSeconds(30));
      var installerArguments = await File.ReadAllLinesAsync(capturedArgumentsPath);

      Assert.Equal(1, result.ExitCode);
      Assert.Contains("Visual Studio Installer failed with exit code 23", result.StandardError);
      Assert.DoesNotContain(
          "LASTEXITCODE",
          result.StandardError,
          StringComparison.OrdinalIgnoreCase);
      Assert.Contains(
          "Downloading the Visual Studio Professional bootstrapper",
          result.StandardOutput);
      Assert.DoesNotContain("--quiet", installerArguments);
      Assert.DoesNotContain("--passive", installerArguments);
      Assert.DoesNotContain("--norestart", installerArguments);
      Assert.DoesNotContain("--allowUnsignedExtensions", installerArguments);
      Assert.Contains("--wait", installerArguments);
      Assert.Contains("--config", installerArguments);
      Assert.Contains(configPath, installerArguments);
    }
    finally
    {
      if (File.Exists(childProcessIdPath) &&
          int.TryParse(await File.ReadAllTextAsync(childProcessIdPath), out var childProcessId))
      {
        try
        {
          using var childProcess = Process.GetProcessById(childProcessId);
          childProcess.Kill(entireProcessTree: true);
          await childProcess.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
          // The fake installer's child already exited.
        }
      }
    }
  }

  [Fact]
  public async Task ReSharperApply_GuiInstallerReportsItsExitCode()
  {
    var repositoryRoot = FindRepositoryRoot();
    var scriptPath = Path.Combine(repositoryRoot, "script", "Invoke-ReSharperTask.ps1");
    var fakeVsWherePath = TestPath("fake-vswhere.exe");
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
        function Save-WdemRemoteFile {
            param($SourceUri, $DestinationPath)
            Add-Type -TypeDefinition 'using System; public static class FakeInstaller { [STAThread] public static void Main(string[] args) { Environment.Exit(23); } }' -OutputAssembly $DestinationPath -OutputType WindowsApplication
        }
        function Invoke-WebRequest { throw 'Apply must use the shared reliable downloader.' }
        function Get-FileHash {
            param($LiteralPath, $Algorithm)
            [pscustomobject] @{ Hash = ('A' * 64) }
        }
        & '{{EscapePowerShellLiteral(scriptPath)}}' -Action Apply -SourceUri 'https://download.jetbrains.com/fake-resharper.exe' -Sha256 ('A' * 64)
        """;

    var result = await RunPowerShellAsync(payload);

    Assert.Equal(1, result.ExitCode);
    Assert.Contains("ReSharper Installer failed with exit code 23", result.StandardError);
    Assert.DoesNotContain(
        "LASTEXITCODE",
        result.StandardError,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Downloading ReSharper from JetBrains", result.StandardOutput);
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
    using var cancellation = new CancellationTokenSource();
    var runner = new DefaultProcessRunner();

    var running = runner.RunAsync(CreateProcessTreeRequest(), output, cancellation.Token);
    var childId = await childProcessId.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    Assert.True(
        await WaitUntilExitedAsync(childId, TimeSpan.FromSeconds(10)),
        $"Child process {childId} was still running after cancellation.");
  }

  [Fact]
  public async Task RunAsync_WhenCancellationIsRequested_WaitsForTerminationConfirmation()
  {
    var processStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new InlineProgress<ProcessOutput>(_ => processStarted.TrySetResult());
    var terminator = new ControlledProcessTreeTerminator();
    using var cancellation = new CancellationTokenSource();
    var runner = new DefaultProcessRunner(terminator);

    var running = runner.RunAsync(CreateBlockingProcessRequest(), output, cancellation.Token);
    await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cancellation.Cancel();
    await terminator.Started.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(running.IsCompleted);

    terminator.AllowTermination();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    Assert.True(terminator.ProcessHasExited);
  }

  [Fact]
  public async Task RunAsync_WhenProcessTreeTerminationFails_ReportsTerminationFailure()
  {
    var processStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new InlineProgress<ProcessOutput>(_ => processStarted.TrySetResult());
    var terminator = new FailingProcessTreeTerminator();
    using var cancellation = new CancellationTokenSource();
    var runner = new DefaultProcessRunner(terminator);

    try
    {
      var running = runner.RunAsync(CreateBlockingProcessRequest(), output, cancellation.Token);
      await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
      cancellation.Cancel();

      var exception = await Assert.ThrowsAsync<ProcessTerminationException>(() => running);

      Assert.Equal(terminator.ProcessId, exception.ProcessId);
      Assert.Contains("could not be confirmed", exception.Message);
    }
    finally
    {
      await terminator.CleanupAsync();
    }
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
    try
    {
      using var process = Process.GetProcessById(processId);
      await process.WaitForExitAsync().WaitAsync(timeout);
      return true;
    }
    catch (ArgumentException)
    {
      return true;
    }
    catch (TimeoutException)
    {
      return false;
    }
  }

  private static ProcessRequest CreateBlockingProcessRequest()
  {
    const string script =
        "[Console]::Out.WriteLine('started'); " +
        "Start-Sleep -Seconds 300";
    return new ProcessRequest(
        "powershell.exe",
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
  }

  private static ProcessRequest CreateProcessTreeRequest()
  {
    const string script =
        "$child = Start-Process -FilePath $env:ComSpec " +
        "-ArgumentList '/d','/c','ping -t 127.0.0.1' -WindowStyle Hidden -PassThru; " +
        "[Console]::Out.WriteLine($child.Id); Wait-Process -Id $child.Id";
    return new ProcessRequest(
        "powershell.exe",
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
  }

  private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }

  private sealed class ControlledProcessTreeTerminator : IProcessTreeTerminator
  {
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _continue = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Started => _started.Task;

    public bool ProcessHasExited { get; private set; }

    public void AllowTermination() => _continue.TrySetResult();

    public async Task TerminateAsync(Process process, TimeSpan timeout)
    {
      _started.TrySetResult();
      await _continue.Task.WaitAsync(timeout);
      process.Kill(entireProcessTree: true);
      await process.WaitForExitAsync().WaitAsync(timeout);
      ProcessHasExited = process.HasExited;
    }
  }

  private sealed class FailingProcessTreeTerminator : IProcessTreeTerminator
  {
    public int ProcessId { get; private set; } = -1;

    public Task TerminateAsync(Process process, TimeSpan timeout)
    {
      ProcessId = process.Id;
      throw new ProcessTerminationException(
          process.Id,
          "Process tree termination could not be confirmed.");
    }

    public async Task CleanupAsync()
    {
      if (ProcessId < 0)
      {
        return;
      }

      try
      {
        using var process = Process.GetProcessById(ProcessId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
      }
      catch (ArgumentException)
      {
        // The process exited between the failed cancellation and cleanup.
      }
    }
  }
}
