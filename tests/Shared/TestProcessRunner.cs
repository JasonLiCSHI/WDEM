using System.ComponentModel;
using System.Diagnostics;

namespace Wdem.Tests;

internal sealed record TestProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
  public string Output => StandardOutput + StandardError;
}

internal static class TestProcessRunner
{
  public static async Task<TestProcessResult> RunAsync(
      string fileName,
      string workingDirectory,
      IReadOnlyList<string> arguments,
      TimeSpan? timeout = null)
  {
    TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(3);
    if (effectiveTimeout <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
    }

    var startInfo = new ProcessStartInfo(fileName)
    {
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    foreach (string argument in arguments)
    {
      startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
    Task<string> standardError = process.StandardError.ReadToEndAsync();
    using var timeoutSource = new CancellationTokenSource(effectiveTimeout);
    try
    {
      await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
    {
      await TerminateAsync(process).ConfigureAwait(false);
      await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
      throw new TimeoutException(
          $"Process '{fileName}' did not exit within {effectiveTimeout}.");
    }

    return new TestProcessResult(
        process.ExitCode,
        await standardOutput.ConfigureAwait(false),
        await standardError.ConfigureAwait(false));
  }

  private static async Task TerminateAsync(Process process)
  {
    if (!HasExited(process))
    {
      try
      {
        process.Kill(entireProcessTree: true);
      }
      catch (PlatformNotSupportedException)
      {
        KillSingleProcess(process);
      }
      catch (NotSupportedException)
      {
        KillSingleProcess(process);
      }
      catch (InvalidOperationException) when (HasExited(process))
      {
      }
      catch (Win32Exception) when (HasExited(process))
      {
      }
    }

    if (!HasExited(process))
    {
      try
      {
        await process.WaitForExitAsync().ConfigureAwait(false);
      }
      catch (InvalidOperationException) when (HasExited(process))
      {
      }
    }
  }

  private static void KillSingleProcess(Process process)
  {
    if (HasExited(process))
    {
      return;
    }

    try
    {
      process.Kill();
    }
    catch (InvalidOperationException) when (HasExited(process))
    {
    }
    catch (Win32Exception) when (HasExited(process))
    {
    }
  }

  private static bool HasExited(Process process)
  {
    try
    {
      return process.HasExited;
    }
    catch (InvalidOperationException)
    {
      return true;
    }
  }
}
