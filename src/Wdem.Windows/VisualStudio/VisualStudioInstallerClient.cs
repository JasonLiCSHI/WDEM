using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Resources;
using Wdem.Windows.Security;

namespace Wdem.Windows.VisualStudio;

public interface IVisualStudioInstallerClient
{
  Task<TrustedFileVerificationResult> AcquireBootstrapperAsync(
      Uri source,
      string expectedSha256,
      CancellationToken cancellationToken);

  Task<VisualStudioInstallerResult> InstallAsync(
      string executablePath,
      string productId,
      Uri? channelUri,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath,
      CancellationToken cancellationToken);

  Task<VisualStudioInstallerResult> ModifyAsync(
      string executablePath,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath,
      CancellationToken cancellationToken);
}

public sealed record VisualStudioInstallerResult(
    ProcessExecutionResult Process,
    RestartPolicy RestartRequirement,
    IReadOnlyDictionary<string, string> Evidence);

public sealed class VisualStudioInstallerClient : IVisualStudioInstallerClient
{
  private readonly IProcessExecutor _processExecutor;
  private readonly ITrustedFileVerifier _trustedFileVerifier;
  private readonly HttpClient _httpClient;

  public VisualStudioInstallerClient(
      IProcessExecutor processExecutor,
      ITrustedFileVerifier? trustedFileVerifier = null,
      HttpClient? httpClient = null)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _trustedFileVerifier = trustedFileVerifier ?? new TrustedFileVerifier();
    _httpClient = httpClient ?? new HttpClient();
  }

  public async Task<TrustedFileVerificationResult> AcquireBootstrapperAsync(
      Uri source,
      string expectedSha256,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(source);
    if (!source.IsAbsoluteUri ||
        !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
      return ConfigurationFailure("The Visual Studio bootstrapper URI must use HTTPS.");
    }

    var downloadDirectory = Path.Combine(Path.GetTempPath(), "wdem", "visual-studio");
    Directory.CreateDirectory(downloadDirectory);
    var localPath = Path.Combine(downloadDirectory, $"vs-bootstrapper-{Guid.NewGuid():N}.exe");
    try
    {
      await using (var destination = new FileStream(
                       localPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      await using (var sourceStream = await _httpClient.GetStreamAsync(source, cancellationToken)
                       .ConfigureAwait(false))
      {
        await sourceStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
      }

      var verification = await _trustedFileVerifier.VerifySha256Async(
          localPath,
          expectedSha256,
          cancellationToken).ConfigureAwait(false);
      if (!verification.IsTrusted)
      {
        File.Delete(localPath);
      }

      return verification;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      TryDelete(localPath);
      throw;
    }
    catch (Exception)
    {
      TryDelete(localPath);
      return new TrustedFileVerificationResult(
          false,
          null,
          null,
          new StructuredError(
              WdemErrorCode.DownloadError,
              "Visual Studio bootstrapper download failed.",
              "The trusted Visual Studio bootstrapper could not be downloaded."));
    }
  }

  public Task<VisualStudioInstallerResult> InstallAsync(
      string executablePath,
      string productId,
      Uri? channelUri,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath,
      CancellationToken cancellationToken) => ExecuteAsync(
          executablePath,
          CreateInstallArguments(
              productId,
              channelUri,
              installPath,
              workloads,
              components,
              vsconfigPath),
          cancellationToken);

  public Task<VisualStudioInstallerResult> ModifyAsync(
      string executablePath,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath,
      CancellationToken cancellationToken) => ExecuteAsync(
          executablePath,
          CreateModifyArguments(installPath, workloads, components, vsconfigPath),
          cancellationToken);

  public static IReadOnlyList<string> CreateInstallArguments(
      string productId,
      Uri? channelUri,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(productId);
    var arguments = new List<string> { "install", "--productId", productId };
    if (channelUri is not null)
    {
      if (!channelUri.IsAbsoluteUri ||
          !string.Equals(channelUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
      {
        throw new ArgumentException("The channel URI must be an absolute HTTPS URI.", nameof(channelUri));
      }

      arguments.AddRange(["--channelUri", channelUri.AbsoluteUri]);
    }

    AddConfigurationArguments(arguments, installPath, workloads, components, vsconfigPath);
    return arguments;
  }

  public static IReadOnlyList<string> CreateModifyArguments(
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath)
  {
    var arguments = new List<string> { "modify" };
    AddConfigurationArguments(arguments, installPath, workloads, components, vsconfigPath);
    return arguments;
  }

  private async Task<VisualStudioInstallerResult> ExecuteAsync(
      string executablePath,
      IReadOnlyList<string> arguments,
      CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
    if (!Path.IsPathFullyQualified(executablePath) ||
        !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException(
          "The Visual Studio installer path must be an absolute executable path.",
          nameof(executablePath));
    }

    var process = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest(executablePath, arguments),
        null,
        cancellationToken).ConfigureAwait(false);
    var restart = process.ExitCode switch
    {
      1641 => RestartPolicy.RestartRequired,
      3010 => RestartPolicy.RestartRecommended,
      _ => RestartPolicy.NoRestart
    };
    return new VisualStudioInstallerResult(
        process,
        restart,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["installerPath"] = Path.GetFullPath(executablePath),
          ["restartRequirement"] = restart.ToString()
        });
  }

  private static void AddConfigurationArguments(
      List<string> arguments,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
    ArgumentNullException.ThrowIfNull(workloads);
    ArgumentNullException.ThrowIfNull(components);
    if (!Path.IsPathFullyQualified(installPath))
    {
      throw new ArgumentException("The install path must be absolute.", nameof(installPath));
    }

    arguments.AddRange(["--installPath", Path.GetFullPath(installPath)]);
    foreach (var id in workloads.Concat(components))
    {
      if (string.IsNullOrWhiteSpace(id) || id.StartsWith("-", StringComparison.Ordinal))
      {
        throw new ArgumentException("Visual Studio workload and component IDs cannot be arguments.");
      }

      arguments.AddRange(["--add", id]);
    }

    if (vsconfigPath is not null)
    {
      if (!Path.IsPathFullyQualified(vsconfigPath))
      {
        throw new ArgumentException("The .vsconfig path must be absolute.", nameof(vsconfigPath));
      }

      arguments.AddRange(["--config", Path.GetFullPath(vsconfigPath)]);
    }

    arguments.AddRange(["--passive", "--wait", "--norestart"]);
  }

  private static TrustedFileVerificationResult ConfigurationFailure(string detail) => new(
      false,
      null,
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "Visual Studio bootstrapper is not trusted.",
          detail));

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }
}
