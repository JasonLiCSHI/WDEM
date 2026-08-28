using System.Diagnostics;
using Wdem.Core.Compliance;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;
using Wdem.Windows.Processes;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class ProviderProcessClassificationTests
{
  [Theory]
  [InlineData("git")]
  [InlineData("dotnet-sdk")]
  public async Task DetectAsync_ExecutableNotFoundFromLegacyAdapterIsMissing(string providerKind)
  {
    _ = Enum.TryParse("ExecutableNotFound", out ProcessFailureKind failureKind);
    var runner = new StubProcessRunner(new ProcessRunResult(false, null, [], [])
    {
      FailureKind = failureKind,
      FailureMessage = "Executable was not found."
    });
    var adapter = new LegacySourceProcessExecutorAdapter(runner);
    var provider = Provider(providerKind, adapter);
    var resource = Resource(providerKind);

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.Null(state.StructuredError);
  }

  [Theory]
  [InlineData("git")]
  [InlineData("dotnet-sdk")]
  public async Task DetectAsync_OtherLegacyStartFailureRemainsDetectionFailure(string providerKind)
  {
    var runner = new StubProcessRunner(new ProcessRunResult(false, null, [], [])
    {
      FailureKind = ProcessFailureKind.StartFailed,
      FailureMessage = "Process could not be started."
    });
    var adapter = new LegacySourceProcessExecutorAdapter(runner);
    var provider = Provider(providerKind, adapter);

    var state = await provider.DetectAsync(Resource(providerKind), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.NotNull(state.StructuredError);
  }

  private static IResourceProvider Provider(
      string providerKind,
      LegacySourceProcessExecutorAdapter adapter) => providerKind switch
      {
        "git" => new GitProvider(adapter, new ComplianceEvaluator()),
        "dotnet-sdk" => new DotNetSdkProvider(adapter, new ComplianceEvaluator()),
        _ => throw new ArgumentOutOfRangeException(nameof(providerKind))
      };

  private static ResourceDefinition Resource(string providerKind) => new()
  {
    Id = providerKind,
    Type = providerKind,
    Provider = "winget"
  };

  private sealed class StubProcessRunner(ProcessRunResult result) : IProcessRunner
  {
    public Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken) => Task.FromResult(result);

    public bool RunCommand(
        string fileName,
        string arguments,
        bool dryRun,
        Action<string>? onOutput = null) => throw new NotSupportedException();

    public bool RunCommand(
        string fileName,
        IEnumerable<string> arguments,
        bool dryRun,
        Action<string>? onOutput = null) => throw new NotSupportedException();

    public Task<bool> RunCommandAsync(
        string fileName,
        IEnumerable<string> arguments,
        bool dryRun,
        Action<string>? onOutput,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public string RunCommandWithOutput(string fileName, string args) =>
        throw new NotSupportedException();

    public string RunCommandWithOutput(string fileName, IEnumerable<string> args) =>
        throw new NotSupportedException();

    public string RunCommandWithOutput(string fileName, string args, string? standardInput) =>
        throw new NotSupportedException();

    public string RunCommandWithOutput(
        string fileName,
        IEnumerable<string> args,
        string? standardInput) => throw new NotSupportedException();

    public string RunAndCapture(string fileName, string arguments) =>
        throw new NotSupportedException();

    public string RunAndCapture(string fileName, IEnumerable<string> arguments) =>
        throw new NotSupportedException();

    public bool RunProcessWithStartInfo(ProcessStartInfo startInfo) =>
        throw new NotSupportedException();
  }
}
