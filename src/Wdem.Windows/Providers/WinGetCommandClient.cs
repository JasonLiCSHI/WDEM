using Wdem.Core.Execution;
using Wdem.Core.Processes;

namespace Wdem.Windows.Providers;

public sealed record WinGetCommandResult(
    ProcessExecutionResult Process,
    StructuredError? Error);

public sealed class WinGetCommandClient
{
  public const string FileName = "winget";

  private readonly IProcessExecutor _processExecutor;
  private readonly string _logLocation;

  public WinGetCommandClient(IProcessExecutor processExecutor, string? logLocation = null)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _logLocation = logLocation ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
        "LocalState",
        "DiagOutputDir");
  }

  public Task<ProcessExecutionResult> ListAsync(
      string packageId,
      string? source,
      CancellationToken cancellationToken) => _processExecutor.ExecuteAsync(
          new ProcessExecutionRequest(
              FileName,
              WithSource(["list", "--id", packageId, "--exact"], source)),
          null,
          cancellationToken);

  public async Task<WinGetCommandResult> QueryAvailabilityAsync(
      string resourceId,
      string packageId,
      string? preferredVersion,
      string? source,
      CancellationToken cancellationToken)
  {
    var arguments = new List<string> { "show", "--id", packageId, "--exact" };
    if (!string.IsNullOrWhiteSpace(preferredVersion))
    {
      arguments.Add("--versions");
    }

    AddSource(arguments, source);
    arguments.Add("--accept-source-agreements");
    arguments.Add("--disable-interactivity");
    var result = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest(FileName, arguments),
        null,
        cancellationToken).ConfigureAwait(false);
    if (result.Started && result.ExitCode == 0 && result.Error is null &&
        (string.IsNullOrWhiteSpace(preferredVersion) ||
         ContainsExactVersionToken(result.StandardOutput, preferredVersion)))
    {
      return new WinGetCommandResult(result, null);
    }

    return new WinGetCommandResult(result, new StructuredError(
        WdemErrorCode.DownloadError,
        "WinGet package source is unavailable.",
        string.IsNullOrWhiteSpace(preferredVersion)
            ? $"Package '{packageId}' is unavailable from the configured WinGet sources."
            : $"Exact package version '{preferredVersion}' is unavailable for '{packageId}'.")
    {
      ResourceId = resourceId,
      ProcessExitCode = result.ExitCode,
      LogLocation = _logLocation,
      IsRetryable = true
    });
  }

  private static bool ContainsExactVersionToken(
      IReadOnlyList<string> output,
      string preferredVersion)
  {
    var expected = preferredVersion.Trim();
    return output.Any(line => string.Equals(
        line.Trim().Trim('"'),
        expected,
        StringComparison.Ordinal));
  }

  public async Task<WinGetCommandResult> InstallAsync(
      string resourceId,
      string stepId,
      string packageId,
      string? preferredVersion,
      string? source,
      IProgress<string>? output,
      CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
    var arguments = new List<string>
    {
      "install", "--id", packageId, "--exact"
    };
    if (!string.IsNullOrWhiteSpace(preferredVersion))
    {
      arguments.Add("--version");
      arguments.Add(preferredVersion);
    }

    AddSource(arguments, source);
    arguments.AddRange(
    [
      "--silent",
      "--accept-package-agreements",
      "--accept-source-agreements",
      "--disable-interactivity"
    ]);

    var result = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest(FileName, arguments),
        output,
        cancellationToken).ConfigureAwait(false);
    if (result.Started && result.ExitCode == 0 && result.Error is null)
    {
      return new WinGetCommandResult(result, null);
    }

    return new WinGetCommandResult(
        result,
        CreateInstallationError(resourceId, stepId, packageId, result.ExitCode));
  }

  internal StructuredError CreateInstallationError(
      string resourceId,
      string stepId,
      string packageId,
      int? exitCode) => new(
        WdemErrorCode.InstallationError,
        "WinGet package installation failed.",
        $"Package '{packageId}' did not reach its requested state after WinGet completed.")
      {
        ResourceId = resourceId,
        StepId = stepId,
        ProcessExitCode = exitCode,
        LogLocation = _logLocation,
        IsRetryable = true
      };

  private static IReadOnlyList<string> WithSource(
      IReadOnlyList<string> arguments,
      string? source)
  {
    var result = arguments.ToList();
    AddSource(result, source);
    return result;
  }

  private static void AddSource(List<string> arguments, string? source)
  {
    if (!string.IsNullOrWhiteSpace(source))
    {
      arguments.Add("--source");
      arguments.Add(source);
    }
  }
}
