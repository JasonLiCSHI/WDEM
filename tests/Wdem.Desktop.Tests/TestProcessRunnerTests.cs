using System.Diagnostics;
using System.Text;
using Wdem.Tests;
using Xunit;

namespace Wdem.Desktop.Tests;

public sealed class TestProcessRunnerTests
{
  [Fact]
  public async Task TimeoutKillsSpawnedProcessTreeBeforeReturning()
  {
    string testRoot = Path.Combine(
        Path.GetTempPath(),
        "wdem-process-runner-timeout",
        Guid.NewGuid().ToString("N"));
    string childPidPath = Path.Combine(testRoot, "child.pid");
    Directory.CreateDirectory(testRoot);
    int? childProcessId = null;

    try
    {
      string childCommand = EncodePowerShell("Start-Sleep -Seconds 30");
      string escapedPidPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
      string parentCommand = EncodePowerShell(
          "$child = Start-Process -FilePath 'powershell.exe' " +
          $"-ArgumentList @('-NoProfile','-EncodedCommand','{childCommand}') -PassThru; " +
          $"Set-Content -LiteralPath '{escapedPidPath}' -Value $child.Id; " +
          "Wait-Process -Id $child.Id");

      await Assert.ThrowsAsync<TimeoutException>(() => TestProcessRunner.RunAsync(
          "powershell.exe",
          testRoot,
          ["-NoProfile", "-EncodedCommand", parentCommand],
          TimeSpan.FromSeconds(5)));

      Assert.True(File.Exists(childPidPath), "The parent process did not report its child PID.");
      childProcessId = int.Parse(await File.ReadAllTextAsync(childPidPath));
      Assert.False(IsRunning(childProcessId.Value));
    }
    finally
    {
      if (childProcessId is int processId && IsRunning(processId))
      {
        using Process child = Process.GetProcessById(processId);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
      }

      try
      {
        Directory.Delete(testRoot, recursive: true);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
  }

  private static string EncodePowerShell(string command) =>
      Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

  private static bool IsRunning(int processId)
  {
    try
    {
      using Process process = Process.GetProcessById(processId);
      return !process.HasExited;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }
}
