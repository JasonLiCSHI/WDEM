using System.Diagnostics;
using System.Text;
using Wdem.Testing;

namespace Wdem.Windows.Tests;

public abstract class PowerShellScriptTestBase : TemporaryDirectoryTestBase
{
  protected static string FindRepositoryRoot() => RepositoryLocator.FindRoot();

  protected static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

  protected static async Task<PowerShellResult> RunPowerShellAsync(string payload)
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

  protected sealed record PowerShellResult(
      int ExitCode,
      string StandardOutput,
      string StandardError)
  {
    public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
  }
}
