using System.ComponentModel;
using System.Security.Cryptography;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Resources;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.VisualStudio;

public sealed class VisualStudioInstallerClientTests
{
  [Fact]
  public async Task ModifyAsync_UsesSetupExecutableAndTokenizedArguments()
  {
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(process);

    await client.ModifyAsync(
        client.SetupExecutablePath,
        @"C:\VS",
        ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        ["Microsoft.NetCore.Component.Runtime.10.0"],
        null,
        CancellationToken.None);

    var request = Assert.Single(process.Requests);
    Assert.Equal(
        @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe",
        request.FileName);
    Assert.Equal(
        [
          "modify", "--installPath", @"C:\VS",
          "--add", "Microsoft.VisualStudio.Workload.ManagedDesktop",
          "--add", "Microsoft.NetCore.Component.Runtime.10.0",
          "--passive", "--wait", "--norestart"
        ],
        request.Arguments);
  }

  [Fact]
  public async Task ModifyAsync_UsesConfiguredInstallerOperationTimeout()
  {
    var process = new RecordingProcessExecutor();
    var timeout = TimeSpan.FromHours(6);
    var client = new VisualStudioInstallerClient(
        process,
        installerOperationTimeout: timeout);

    await client.ModifyAsync(
        client.SetupExecutablePath,
        @"C:\VS",
        [],
        [],
        null,
        CancellationToken.None);

    Assert.Equal(timeout, Assert.Single(process.Requests).Timeout);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(25)]
  public void Constructor_RejectsUnboundedInstallerOperationTimeout(int hours)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => new VisualStudioInstallerClient(
        new RecordingProcessExecutor(),
        installerOperationTimeout: TimeSpan.FromHours(hours)));
  }

  [Fact]
  public async Task InstallAsync_IncludesProductChannelConfigAndRequestedIdsAsTokens()
  {
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(process);

    await client.InstallAsync(
        client.SetupExecutablePath,
        "Microsoft.VisualStudio.Product.Community",
        new Uri("https://example.test/channel.json"),
        @"C:\VS",
        ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        ["Microsoft.NetCore.Component.Runtime.10.0"],
        @"C:\Profiles\developer.vsconfig",
        CancellationToken.None);

    var request = Assert.Single(process.Requests);
    Assert.Equal(client.SetupExecutablePath, request.FileName);
    Assert.Equal(
        [
          "install", "--productId", "Microsoft.VisualStudio.Product.Community",
          "--channelUri", "https://example.test/channel.json",
          "--installPath", @"C:\VS",
          "--add", "Microsoft.VisualStudio.Workload.ManagedDesktop",
          "--add", "Microsoft.NetCore.Component.Runtime.10.0",
          "--config", @"C:\Profiles\developer.vsconfig",
          "--passive", "--wait", "--norestart"
        ],
        request.Arguments);
  }

  [Fact]
  public async Task UpdateAsync_UsesNonInteractiveUpdateForExactInstallPath()
  {
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(process);

    await client.UpdateAsync(
        client.SetupExecutablePath,
        @"C:\VS",
        CancellationToken.None);

    Assert.Equal(
        ["update", "--installPath", @"C:\VS", "--passive", "--wait", "--norestart"],
        Assert.Single(process.Requests).Arguments);
  }

  [Fact]
  public async Task InstallerExitCode3010_ReportsActualRestartRecommendation()
  {
    var process = new RecordingProcessExecutor
    {
      Result = new ProcessExecutionResult(true, 3010, [], [])
    };
    var client = new VisualStudioInstallerClient(process);

    var result = await client.ModifyAsync(
        client.SetupExecutablePath,
        @"C:\VS",
        [],
        [],
        null,
        CancellationToken.None);

    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
  }

  [Fact]
  public async Task InstallAsync_UnverifiedCallerOwnedExecutableIsRejectedWithoutDeletion()
  {
    var executablePath = Path.Combine(
        Path.GetTempPath(),
        $"caller-owned-{Guid.NewGuid():N}.exe");
    await File.WriteAllTextAsync(executablePath, "caller owned");
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(process);

    try
    {
      await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          executablePath,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS",
          [],
          [],
          null,
          CancellationToken.None));

      Assert.True(File.Exists(executablePath));
      Assert.Empty(process.Requests);
    }
    finally
    {
      File.Delete(executablePath);
    }
  }

  [Theory]
  [InlineData("--add")]
  [InlineData("Microsoft.VisualStudio.Product.Community\r\n--quiet")]
  public void CreateInstallArguments_RejectsInjectedProductId(string productId)
  {
    Assert.Throws<ArgumentException>(() => VisualStudioInstallerClient.CreateInstallArguments(
        productId, null, @"C:\VS", [], [], null));
  }

  [Fact]
  public void CreateModifyArguments_RejectsControlCharactersInPathAndIds()
  {
    Assert.Throws<ArgumentException>(() => VisualStudioInstallerClient.CreateModifyArguments(
        "C:\\VS\n--quiet", [], [], null));
    Assert.Throws<ArgumentException>(() => VisualStudioInstallerClient.CreateModifyArguments(
        @"C:\VS", ["Microsoft.VisualStudio.Workload.ManagedDesktop\n--quiet"], [], null));
  }

  [Fact]
  public void CreateInstallArguments_RejectsChannelUriWithUserInfo()
  {
    Assert.Throws<ArgumentException>(() => VisualStudioInstallerClient.CreateInstallArguments(
        "Microsoft.VisualStudio.Product.Community",
        new Uri("https://user:secret@example.test/channel.json"),
        @"C:\VS", [], [], null));
  }

  [Fact]
  public async Task VerifiedBootstrapper_IsReverifiedAtLaunchAndRecordedInEvidence()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var verified = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);

    var result = await client.InstallAsync(
        verified.VerifiedPath!,
        "Microsoft.VisualStudio.Product.Community",
        null,
        @"C:\VS", [], [], null, CancellationToken.None);

    Assert.True(verified.IsTrusted);
    var request = Assert.Single(process.Requests);
    Assert.NotEqual(verified.VerifiedPath, request.FileName);
    Assert.Equal(request.FileName, result.Evidence["installerPath"]);
    Assert.Equal(hash, result.Evidence["installerSha256"]);
    Assert.False(File.Exists(request.FileName));
  }

  [Fact]
  public async Task AcquiredBootstrapper_SuccessDeletesSourceAndCannotBeReused()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var acquired = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);

    try
    {
      await client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None);

      Assert.False(File.Exists(acquired.VerifiedPath));
      await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None));
      Assert.Single(process.Requests);
    }
    finally
    {
      File.Delete(acquired.VerifiedPath!);
    }
  }

  [Fact]
  public async Task AcquiredBootstrapper_DisposalDeletesSourceAndCannotBeReused()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var acquired = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);
    var acquiredPath = acquired.VerifiedPath!;

    await acquired.DisposeAsync();
    await acquired.DisposeAsync();

    Assert.False(File.Exists(acquiredPath));
    await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
        acquiredPath,
        "Microsoft.VisualStudio.Product.Community",
        null,
        @"C:\VS", [], [], null, CancellationToken.None));
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task AcquiredBootstrapper_ExecutionFailureDeletesSourceWithoutMaskingFailure()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var primaryFailure = new InvalidOperationException("process failed");
    var process = new RecordingProcessExecutor { Failure = primaryFailure };
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var acquired = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);

    try
    {
      var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None));

      Assert.Same(primaryFailure, thrown);
      Assert.False(File.Exists(acquired.VerifiedPath));
      await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None));
      Assert.Single(process.Requests);
    }
    finally
    {
      File.Delete(acquired.VerifiedPath!);
    }
  }

  [Fact]
  public async Task AcquiredBootstrapper_CleanupFailureDoesNotMaskExecutionFailure()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var primaryFailure = new InvalidOperationException("process failed");
    var process = new RecordingProcessExecutor { Failure = primaryFailure };
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var acquired = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);
    File.SetAttributes(acquired.VerifiedPath!, FileAttributes.ReadOnly);

    try
    {
      var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None));

      Assert.Same(primaryFailure, thrown);
      Assert.True(File.Exists(acquired.VerifiedPath));
      await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
          acquired.VerifiedPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None));
      Assert.Single(process.Requests);
    }
    finally
    {
      File.SetAttributes(acquired.VerifiedPath!, FileAttributes.Normal);
      File.Delete(acquired.VerifiedPath!);
    }
  }

  [Fact]
  public async Task VerifiedBootstrapper_ModifiedAfterVerificationIsNotLaunched()
  {
    var bytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(bytes)),
        secureArtifactStager: new SecureArtifactStager(
            new RecordingSecureDirectoryPolicy()));
    var verified = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);
    await File.WriteAllTextAsync(verified.VerifiedPath!, "tampered");

    await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallAsync(
        verified.VerifiedPath!,
        "Microsoft.VisualStudio.Product.Community",
        null,
        @"C:\VS", [], [], null, CancellationToken.None));

    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task VerifiedBootstrapper_SourceReplacementCannotChangeRestrictedStagedLaunch()
  {
    var trustedBytes = "trusted bootstrapper"u8.ToArray();
    var hash = Convert.ToHexString(SHA256.HashData(trustedBytes));
    var policy = new RecordingSecureDirectoryPolicy();
    string? acquiredPath = null;
    byte[]? launchedBytes = null;
    var process = new RecordingProcessExecutor
    {
      BeforeExecute = request =>
      {
        File.WriteAllText(acquiredPath!, "replacement payload");
        launchedBytes = File.ReadAllBytes(request.FileName);
      }
    };
    var client = new VisualStudioInstallerClient(
        process,
        httpClient: new HttpClient(new ContentHandler(trustedBytes)),
        secureArtifactStager: new SecureArtifactStager(policy));
    var acquired = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"), hash, CancellationToken.None);
    acquiredPath = acquired.VerifiedPath;

    try
    {
      var result = await client.InstallAsync(
          acquiredPath!,
          "Microsoft.VisualStudio.Product.Community",
          null,
          @"C:\VS", [], [], null, CancellationToken.None);

      var request = Assert.Single(process.Requests);
      Assert.NotEqual(acquiredPath, request.FileName);
      Assert.Equal(trustedBytes, launchedBytes);
      Assert.Contains(Path.GetDirectoryName(request.FileName)!, policy.SecuredDirectories);
      Assert.Equal(hash, result.Evidence["installerSha256"]);
      Assert.False(File.Exists(request.FileName));
    }
    finally
    {
      File.Delete(acquiredPath!);
    }
  }

  [Fact]
  public async Task SecureStaging_NativeDirectoryFailureReturnsStructuredError()
  {
    var sourcePath = Path.GetTempFileName();
    try
    {
      var sourceBytes = "trusted artifact"u8.ToArray();
      await File.WriteAllBytesAsync(sourcePath, sourceBytes);
      var stager = new SecureArtifactStager(new NativeFailureSecureDirectoryPolicy());

      var result = await stager.StageVerifiedAsync(
          sourcePath,
          Convert.ToHexString(SHA256.HashData(sourceBytes)),
          SecureArtifactKind.Executable,
          CancellationToken.None);

      Assert.Null(result.Artifact);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task VsconfigParser_ValidFileReturnsWorkloadsAndComponents()
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, """
          { "version": "1.0", "components": [
            "Microsoft.VisualStudio.Workload.ManagedDesktop",
            "Microsoft.NetCore.Component.Runtime.10.0"
          ] }
          """);

      var result = await VisualStudioConfigurationParser.ParseAsync(path, CancellationToken.None);

      Assert.Null(result.Error);
      Assert.Equal(["Microsoft.VisualStudio.Workload.ManagedDesktop"], result.Configuration!.Workloads);
      Assert.Equal(["Microsoft.NetCore.Component.Runtime.10.0"], result.Configuration.Components);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{ \"version\": \"1.0\", \"components\": \"not-an-array\" }")]
  [InlineData("{ \"version\": \"1.0\", \"components\": [] }")]
  public async Task VsconfigParser_InvalidSchemaOrEmptyContentReturnsConfigurationError(string json)
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, json);

      var result = await VisualStudioConfigurationParser.ParseAsync(path, CancellationToken.None);

      Assert.Null(result.Configuration);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task AcquireBootstrapperAsync_DownloadFailureDoesNotExposeSourceSecrets()
  {
    const string secret = "bootstrapper-query-secret";
    var client = new VisualStudioInstallerClient(
        new RecordingProcessExecutor(),
        httpClient: new HttpClient(new ThrowingHandler(secret)));

    var result = await client.AcquireBootstrapperAsync(
        new Uri($"https://example.test/vs.exe?signature={secret}"),
        new string('A', 64),
        CancellationToken.None);

    Assert.False(result.IsTrusted);
    Assert.NotNull(result.Error);
    Assert.DoesNotContain(secret, result.Error.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain(
        secret,
        result.Error.UnderlyingExceptionMessage ?? string.Empty,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task AcquireBootstrapperAsync_DownloadDirectoryFailureIsSanitized()
  {
    var secretPath = Path.Combine(
        Path.GetTempPath(),
        $"secret-download-directory-{Guid.NewGuid():N}");
    await File.WriteAllTextAsync(secretPath, "not a directory");
    var client = new VisualStudioInstallerClient(
        new RecordingProcessExecutor(),
        bootstrapperDownloadDirectory: secretPath);

    try
    {
      var result = await client.AcquireBootstrapperAsync(
          new Uri("https://example.test/vs.exe"),
          new string('A', 64),
          CancellationToken.None);

      Assert.False(result.IsTrusted);
      Assert.Equal(WdemErrorCode.DownloadError, result.Error!.Code);
      Assert.DoesNotContain("secret-download", result.Error.Detail, StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(secretPath);
    }
  }

  [Fact]
  public async Task AcquireBootstrapperAsync_RejectsDeclaredContentLengthAboveLimit()
  {
    var client = new VisualStudioInstallerClient(
        new RecordingProcessExecutor(),
        httpClient: new HttpClient(new DeclaredLengthHandler(9)),
        maxBootstrapperBytes: 8);

    var result = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"),
        new string('A', 64),
        CancellationToken.None);

    Assert.False(result.IsTrusted);
    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
  }

  [Fact]
  public async Task AcquireBootstrapperAsync_RejectsStreamingBodyAboveLimit()
  {
    var client = new VisualStudioInstallerClient(
        new RecordingProcessExecutor(),
        httpClient: new HttpClient(new StreamingContentHandler(new byte[9])),
        maxBootstrapperBytes: 8);

    var result = await client.AcquireBootstrapperAsync(
        new Uri("https://example.test/vs.exe"),
        new string('A', 64),
        CancellationToken.None);

    Assert.False(result.IsTrusted);
    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
  }

  private sealed class RecordingProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];
    public ProcessExecutionResult Result { get; init; } =
        new(true, 0, [], []);
    public Exception? Failure { get; init; }
    public Action<ProcessExecutionRequest>? BeforeExecute { get; init; }

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      BeforeExecute?.Invoke(request);
      if (Failure is not null)
      {
        throw Failure;
      }

      return Task.FromResult(Result);
    }
  }

  private sealed class RecordingSecureDirectoryPolicy : ISecureArtifactDirectoryPolicy
  {
    public List<string> SecuredDirectories { get; } = [];

    public string CreateRestrictedStagingDirectory()
    {
      var path = Path.Combine(
          Path.GetTempPath(),
          $"wdem-secure-test-{Guid.NewGuid():N}");
      Directory.CreateDirectory(path);
      Assert.Empty(Directory.EnumerateFileSystemEntries(path));
      SecuredDirectories.Add(path);
      return path;
    }
  }

  private sealed class NativeFailureSecureDirectoryPolicy : ISecureArtifactDirectoryPolicy
  {
    public string CreateRestrictedStagingDirectory() => throw new Win32Exception(5);
  }

  private sealed class ThrowingHandler(string secret) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => throw new HttpRequestException(
            $"Download failed for a source containing {secret}.");
  }

  private sealed class ContentHandler(byte[] content) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage
        {
          StatusCode = System.Net.HttpStatusCode.OK,
          Content = new ByteArrayContent(content)
        });
  }

  private sealed class DeclaredLengthHandler(long contentLength) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      var content = new ByteArrayContent([]);
      content.Headers.ContentLength = contentLength;
      return Task.FromResult(new HttpResponseMessage
      {
        StatusCode = System.Net.HttpStatusCode.OK,
        Content = content
      });
    }
  }

  private sealed class StreamingContentHandler(byte[] content) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage
        {
          StatusCode = System.Net.HttpStatusCode.OK,
          Content = new StreamContent(new MemoryStream(content))
        });
  }
}

public sealed class TrustedFileVerifierTests
{
  [Fact]
  public async Task VerifySha256Async_MissingFileReturnsConfigurationError()
  {
    var result = await new TrustedFileVerifier().VerifySha256Async(
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
        new string('A', 64),
        CancellationToken.None);

    Assert.False(result.IsTrusted);
    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
  }

  [Fact]
  public async Task VerifySha256Async_MalformedExpectedHashReturnsConfigurationError()
  {
    var path = Path.GetTempFileName();
    try
    {
      var result = await new TrustedFileVerifier().VerifySha256Async(
          path, "not-a-hash", CancellationToken.None);

      Assert.False(result.IsTrusted);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task VerifySha256Async_AcceptsCaseInsensitiveHash()
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, "trusted");
      var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))
          .ToLowerInvariant();

      var result = await new TrustedFileVerifier().VerifySha256Async(
          path, expected, CancellationToken.None);

      Assert.True(result.IsTrusted);
      Assert.Equal(Path.GetFullPath(path), result.VerifiedPath);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task VerifySha256Async_MismatchReturnsConfigurationError()
  {
    var path = Path.GetTempFileName();
    try
    {
      await File.WriteAllTextAsync(path, "untrusted");
      var result = await new TrustedFileVerifier().VerifySha256Async(
          path, new string('A', 64), CancellationToken.None);

      Assert.False(result.IsTrusted);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    }
    finally
    {
      File.Delete(path);
    }
  }
}
