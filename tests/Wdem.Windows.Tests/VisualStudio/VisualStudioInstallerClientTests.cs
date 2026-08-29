using Wdem.Core.Processes;
using Wdem.Core.Resources;
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
        @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe",
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
  public async Task InstallAsync_IncludesProductChannelConfigAndRequestedIdsAsTokens()
  {
    var process = new RecordingProcessExecutor();
    var client = new VisualStudioInstallerClient(process);

    await client.InstallAsync(
        @"C:\verified\vs_community.exe",
        "Microsoft.VisualStudio.Product.Community",
        new Uri("https://example.test/channel.json"),
        @"C:\VS",
        ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        ["Microsoft.NetCore.Component.Runtime.10.0"],
        @"C:\Profiles\developer.vsconfig",
        CancellationToken.None);

    var request = Assert.Single(process.Requests);
    Assert.Equal(@"C:\verified\vs_community.exe", request.FileName);
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
  public async Task InstallerExitCode3010_ReportsActualRestartRecommendation()
  {
    var process = new RecordingProcessExecutor
    {
      Result = new ProcessExecutionResult(true, 3010, [], [])
    };
    var client = new VisualStudioInstallerClient(process);

    var result = await client.ModifyAsync(
        @"C:\setup.exe",
        @"C:\VS",
        [],
        [],
        null,
        CancellationToken.None);

    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
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

  private sealed class RecordingProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];
    public ProcessExecutionResult Result { get; init; } =
        new(true, 0, [], []);

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      return Task.FromResult(Result);
    }
  }

  private sealed class ThrowingHandler(string secret) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => throw new HttpRequestException(
            $"Download failed for a source containing {secret}.");
  }
}
