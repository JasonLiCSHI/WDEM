using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class DotNetSdkProviderTests
{
  [Fact]
  public async Task ApplyAsync_DotNetUsesExactPreferredVersionAndTokenizedArguments()
  {
    var process = new RecordingProcessExecutor();
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x", "10.0.105");
    var plan = InstallPlan(resource);

    await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    var install = Assert.Single(process.Requests, request =>
        string.Equals(request.FileName, "winget", StringComparison.OrdinalIgnoreCase) &&
        request.Arguments.FirstOrDefault() == "install");
    Assert.Equal(
        ["install", "--id", "Microsoft.DotNet.SDK.10", "--exact", "--version", "10.0.105",
         "--silent", "--accept-package-agreements", "--accept-source-agreements",
         "--disable-interactivity"],
        install.Arguments);
  }

  [Fact]
  public async Task DetectAndPlan_AnyInstalledSdkMaySatisfyConstraint()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName == "dotnet"
          ? new ProcessExecutionResult(true, 0,
              ["8.0.412 [C:\\dotnet\\sdk]", "10.0.105 [C:\\dotnet\\sdk]"], [])
          : new ProcessExecutionResult(true, 0, [], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, state, CancellationToken.None);

    Assert.Equal(2, state.InstalledVersions.Count);
    Assert.False(plan.RequiresApply);
    Assert.Equal(ComplianceStatus.Satisfied, plan.Compliance);
  }

  [Fact]
  public async Task DetectAsync_MissingExecutableIsSuccessfulMissingState()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(false, null, [], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
  }

  [Fact]
  public async Task DetectAsync_LaunchedMalformedOutputIsDetectionFailure()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(true, 0, ["preview build"], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
  }

  [Fact]
  public async Task ApplyAsync_SuccessfulExitWithoutVerifiedSdkFails()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName == "dotnet"
          ? new ProcessExecutionResult(false, null, [], [])
          : new ProcessExecutionResult(
              true,
              0,
              request.Arguments.FirstOrDefault() == "show"
                  ? ["Version: 10.0.105"]
                  : ["installed"],
              [])
    };
    var provider = new DotNetSdkProvider(
        process,
        new WinGetCommandClient(process, "test-winget.log"),
        new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x", "10.0.105");

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.InstallationError, result.Error!.Code);
    Assert.Equal(0, result.Error.ProcessExitCode);
    Assert.Equal("test-winget.log", result.Error.LogLocation);
  }

  private static ResourceDefinition DotNetResource(
      string? constraint = null,
      string? preferredVersion = null) => new()
      {
        Id = "dotnet-sdk",
        Type = "dotnet-sdk",
        Provider = "winget",
        VersionConstraint = constraint,
        PreferredVersion = preferredVersion
      };

  private static ResourcePlan InstallPlan(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    ResourceType = resource.Type,
    ProviderName = resource.Provider,
    DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
    Compliance = ComplianceStatus.Missing,
    IsExecutable = true,
    Steps =
    [
      new PlanStep
      {
        Id = $"{resource.Id}:install",
        Description = "Install .NET SDK.",
        Action = PlanAction.Install,
        PrivilegeRequirement = resource.PrivilegeRequirement,
        RestartPolicy = resource.RestartPolicy
      }
    ]
  };

  private sealed class RecordingProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];
    public Func<ProcessExecutionRequest, ProcessExecutionResult>? Handler { get; init; }

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      var result = Handler?.Invoke(request) ?? request.FileName switch
      {
        "dotnet" => new ProcessExecutionResult(
            true,
            0,
            ["10.0.105 [C:\\Program Files\\dotnet\\sdk]"],
            []),
        _ => new ProcessExecutionResult(true, 0, ["10.0.105"], [])
      };
      return Task.FromResult(result);
    }
  }
}
