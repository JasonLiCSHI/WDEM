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
      IReadOnlyList<string> arguments)
  {
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
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    return new TestProcessResult(
        process.ExitCode,
        await standardOutput.ConfigureAwait(false),
        await standardError.ConfigureAwait(false));
  }
}
