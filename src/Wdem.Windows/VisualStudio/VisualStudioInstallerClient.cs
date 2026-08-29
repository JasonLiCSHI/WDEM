using System.Collections.Concurrent;
using System.Text.Json;
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
  private readonly ISecureArtifactStager _secureArtifactStager;
  private readonly ConcurrentDictionary<string, string> _verifiedBootstrappers =
      new(StringComparer.OrdinalIgnoreCase);

  public VisualStudioInstallerClient(
      IProcessExecutor processExecutor,
      ITrustedFileVerifier? trustedFileVerifier = null,
      HttpClient? httpClient = null,
      ISecureArtifactStager? secureArtifactStager = null)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _trustedFileVerifier = trustedFileVerifier ?? new TrustedFileVerifier();
    _httpClient = httpClient ?? new HttpClient();
    _secureArtifactStager = secureArtifactStager ?? new SecureArtifactStager(
        verifier: _trustedFileVerifier);
    SetupExecutablePath = GetDefaultSetupPath();
  }

  public string SetupExecutablePath { get; }

  public async Task<TrustedFileVerificationResult> AcquireBootstrapperAsync(
      Uri source,
      string expectedSha256,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(source);
    if (!VisualStudioInputValidation.IsSafeHttpsUri(source))
    {
      return ConfigurationFailure(
          "The Visual Studio bootstrapper URI must be an HTTPS URI without user information or control characters.");
    }

    if (!VisualStudioInputValidation.IsSha256(expectedSha256))
    {
      return ConfigurationFailure(
          "The Visual Studio bootstrapper SHA-256 must contain exactly 64 hexadecimal characters.");
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
        TryDelete(localPath);
      }
      else
      {
        _verifiedBootstrappers[verification.VerifiedPath!] = verification.Sha256!;
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
    VisualStudioInputValidation.ThrowIfInvalidId(productId, nameof(productId));
    var arguments = new List<string> { "install", "--productId", productId };
    if (channelUri is not null)
    {
      if (!VisualStudioInputValidation.IsSafeHttpsUri(channelUri))
      {
        throw new ArgumentException(
            "The channel URI must be an absolute HTTPS URI without user information or control characters.",
            nameof(channelUri));
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
    VisualStudioInputValidation.ThrowIfInvalidAbsolutePath(executablePath, nameof(executablePath));
    var fullPath = Path.GetFullPath(executablePath);
    if (!string.Equals(
            Path.GetExtension(executablePath),
            ".exe",
            StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException(
          "The Visual Studio installer path must be an absolute executable path.",
          nameof(executablePath));
    }

    SecureStagedArtifact? stagedArtifact = null;
    string? installerSha256 = null;
    if (!string.Equals(fullPath, SetupExecutablePath, StringComparison.OrdinalIgnoreCase))
    {
      if (!_verifiedBootstrappers.TryRemove(fullPath, out var expectedSha256))
      {
        throw new InvalidOperationException(
            "The Visual Studio installer executable has not been verified.");
      }

      SecureArtifactStageResult staged;
      try
      {
        staged = await _secureArtifactStager.StageVerifiedAsync(
            fullPath,
            expectedSha256,
            SecureArtifactKind.Executable,
            cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        TryDelete(fullPath);
      }

      if (staged.Artifact is null)
      {
        throw new InvalidOperationException(
            "The Visual Studio installer executable could not be staged as a verified artifact.");
      }

      stagedArtifact = staged.Artifact;
      fullPath = stagedArtifact.Path;
      installerSha256 = stagedArtifact.Sha256;
    }

    ProcessExecutionResult process;
    try
    {
      process = await _processExecutor.ExecuteAsync(
          new ProcessExecutionRequest(fullPath, arguments),
          null,
          cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      if (stagedArtifact is not null)
      {
        await stagedArtifact.DisposeAsync().ConfigureAwait(false);
      }
    }
    var restart = process.ExitCode switch
    {
      1641 => RestartPolicy.RestartRequired,
      3010 => RestartPolicy.RestartRecommended,
      _ => RestartPolicy.NoRestart
    };
    var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["installerPath"] = fullPath,
      ["restartRequirement"] = restart.ToString()
    };
    if (installerSha256 is not null)
    {
      evidence["installerSha256"] = installerSha256;
    }

    return new VisualStudioInstallerResult(process, restart, evidence);
  }

  private static void AddConfigurationArguments(
      List<string> arguments,
      string installPath,
      IReadOnlyList<string> workloads,
      IReadOnlyList<string> components,
      string? vsconfigPath)
  {
    VisualStudioInputValidation.ThrowIfInvalidAbsolutePath(installPath, nameof(installPath));
    ArgumentNullException.ThrowIfNull(workloads);
    ArgumentNullException.ThrowIfNull(components);

    arguments.AddRange(["--installPath", Path.GetFullPath(installPath)]);
    foreach (var id in workloads.Concat(components))
    {
      VisualStudioInputValidation.ThrowIfInvalidId(id, "id");

      arguments.AddRange(["--add", id]);
    }

    if (vsconfigPath is not null)
    {
      VisualStudioInputValidation.ThrowIfInvalidAbsolutePath(vsconfigPath, nameof(vsconfigPath));

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

  private static string GetDefaultSetupPath()
  {
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    if (string.IsNullOrWhiteSpace(programFiles))
    {
      programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ??
          @"C:\Program Files (x86)";
    }

    return Path.GetFullPath(Path.Combine(
        programFiles,
        "Microsoft Visual Studio",
        "Installer",
        "setup.exe"));
  }
}

public sealed record VisualStudioConfiguration(
    IReadOnlyList<string> Workloads,
    IReadOnlyList<string> Components);

public sealed record VisualStudioConfigurationParseResult(
    VisualStudioConfiguration? Configuration,
    StructuredError? Error);

public static class VisualStudioConfigurationParser
{
  public static async Task<VisualStudioConfigurationParseResult> ParseAsync(
      string path,
      CancellationToken cancellationToken)
  {
    try
    {
      VisualStudioInputValidation.ThrowIfInvalidAbsolutePath(path, nameof(path));
      await using var stream = new FileStream(
          Path.GetFullPath(path),
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 81920,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      using var document = await JsonDocument.ParseAsync(
          stream,
          cancellationToken: cancellationToken).ConfigureAwait(false);
      if (document.RootElement.ValueKind != JsonValueKind.Object ||
          !document.RootElement.TryGetProperty("version", out var version) ||
          version.ValueKind != JsonValueKind.String ||
          string.IsNullOrWhiteSpace(version.GetString()) ||
          !document.RootElement.TryGetProperty("components", out var componentsElement) ||
          componentsElement.ValueKind != JsonValueKind.Array ||
          componentsElement.GetArrayLength() == 0)
      {
        return Failure("The .vsconfig file must contain a version and at least one component ID.");
      }

      var workloads = new List<string>();
      var components = new List<string>();
      foreach (var element in componentsElement.EnumerateArray())
      {
        if (element.ValueKind != JsonValueKind.String ||
            !VisualStudioInputValidation.IsValidId(element.GetString()))
        {
          return Failure("Every .vsconfig component must be a valid Visual Studio identifier.");
        }

        var id = element.GetString()!;
        var destination = id.StartsWith(
            "Microsoft.VisualStudio.Workload.",
            StringComparison.OrdinalIgnoreCase)
            ? workloads
            : components;
        if (!destination.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
          destination.Add(id);
        }
      }

      return new VisualStudioConfigurationParseResult(
          new VisualStudioConfiguration(workloads, components),
          null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or
        IOException or UnauthorizedAccessException or JsonException)
    {
      return Failure("The .vsconfig file could not be read as a valid Visual Studio configuration.");
    }
  }

  private static VisualStudioConfigurationParseResult Failure(string detail) => new(
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "Visual Studio configuration is invalid.",
          detail));
}

internal static class VisualStudioInputValidation
{
  public static bool IsSha256(string? value) => value is not null &&
      value.Length == 64 &&
      value.All(Uri.IsHexDigit);

  public static bool IsSafeHttpsUri(Uri? value) => value is not null &&
      value.IsAbsoluteUri &&
      string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
      string.IsNullOrEmpty(value.UserInfo) &&
      !value.OriginalString.Any(char.IsControl);

  public static bool IsValidId(string? value)
  {
    if (string.IsNullOrEmpty(value) || !IsAsciiLetterOrDigit(value[0]))
    {
      return false;
    }

    return value.All(character =>
        IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
  }

  public static void ThrowIfInvalidId(string? value, string parameterName)
  {
    if (!IsValidId(value))
    {
      throw new ArgumentException(
          "Visual Studio identifiers may contain only ASCII letters, digits, periods, underscores, and hyphens, and must begin with a letter or digit.",
          parameterName);
    }
  }

  public static void ThrowIfInvalidAbsolutePath(string? value, string parameterName)
  {
    if (string.IsNullOrWhiteSpace(value) ||
        !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
        value.Any(char.IsControl) ||
        !Path.IsPathFullyQualified(value))
    {
      throw new ArgumentException("The path must be absolute and contain no control characters.", parameterName);
    }
  }

  private static bool IsAsciiLetterOrDigit(char value) =>
      value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
