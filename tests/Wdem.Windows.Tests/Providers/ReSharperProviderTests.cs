using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class ReSharperProviderTests
{
  [Fact]
  public async Task DetectAsync_OmittedInstanceIdSelectsOneCompleteConstrainedInstance()
  {
    var selected = Instance("17.real");
    var provider = Provider(
        new FakeManifestReader(),
        new ThrowingProcessExecutor(),
        new MutableVisualStudioDiscovery(
            selected,
            Instance("17.other") with { Edition = "Professional" }));

    var state = await provider.DetectAsync(
        WithOptionalInstanceSelector(ReSharperResource(["visual-studio"])),
        CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.Equal("17.real", state.Evidence["visualStudioInstanceId"]);
  }

  [Fact]
  public async Task DetectAsync_AmbiguousOptionalInstanceSelectorReportsCandidateIds()
  {
    var provider = Provider(
        new FakeManifestReader(),
        new ThrowingProcessExecutor(),
        new MutableVisualStudioDiscovery(Instance("17.a"), Instance("17.b")));

    var state = await provider.DetectAsync(
        WithOptionalInstanceSelector(ReSharperResource(["visual-studio"])),
        CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Contains("17.a", state.StructuredError!.Detail, StringComparison.Ordinal);
    Assert.Contains("17.b", state.StructuredError.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DetectAsync_MissingExplicitInstanceIdDoesNotSelectOtherCandidates()
  {
    var provider = Provider(
        new FakeManifestReader(),
        new ThrowingProcessExecutor(),
        new MutableVisualStudioDiscovery(Instance("17.a"), Instance("17.b")));
    var resource = ReSharperResource(["visual-studio"]);

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.Null(state.StructuredError);
  }

  [Fact]
  public async Task ApplyAsync_SelectedVisualStudioPathChangeFailsBeforeInstall()
  {
    var selected = Instance("17.real");
    var discovery = new MutableVisualStudioDiscovery(selected);
    var process = new CountingSuccessProcessExecutor();
    var provider = Provider(new FakeManifestReader(), process, discovery);
    var resource = WithOptionalInstanceSelector(ReSharperResource(["visual-studio"]));
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    discovery.Instances =
    [
      selected with
      {
        InstallationPath = @"D:\MovedVS",
        ProductPath = @"D:\MovedVS\Common7\IDE\devenv.exe"
      }
    ];

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Single(process.Requests);
  }

  [Fact]
  public async Task ReSharperPlan_RequiresVisualStudioDependency()
  {
    var provider = Provider(new FakeManifestReader(), new ThrowingProcessExecutor());

    var validation = await provider.ValidateAsync(
        ReSharperResource(dependsOn: []),
        CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(
        validation.StructuredErrors,
        error => error.Code == WdemErrorCode.DependencyError);
  }

  [Fact]
  public async Task PlanAsync_MissingSelectedVisualStudioDoesNotQueryWinGet()
  {
    var discovery = new MutableVisualStudioDiscovery();
    var process = new CountingSuccessProcessExecutor();
    var provider = Provider(new FakeManifestReader(), process, discovery);
    var resource = ReSharperResource(["visual-studio"]);

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Contains(plan.StructuredErrors, error => error.Code == WdemErrorCode.DependencyError);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_ReplacedSelectedVisualStudioDoesNotInvokeWinGet()
  {
    var original = Instance("17.0_a");
    var discovery = new MutableVisualStudioDiscovery(original);
    var process = new CountingSuccessProcessExecutor();
    var provider = Provider(new FakeManifestReader(), process, discovery);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    discovery.Instances =
    [
      original with
      {
        ProductId = "Microsoft.VisualStudio.Product.Enterprise",
        InstallationVersion = "17.1.0"
      }
    ];

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Single(process.Requests);
  }

  [Fact]
  public async Task PlanAsync_NoAvailableVersionSatisfiesConstraintReturnsVersionError()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        result: new ProcessExecutionResult(
            true,
            0,
            ["2026.1.0", "2025.1.9"],
            []));
    var provider = Provider(new FakeManifestReader(), process);
    var resource = ReSharperResource(["visual-studio"]);

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    var error = Assert.Single(plan.StructuredErrors);
    Assert.Equal(WdemErrorCode.VersionError, error.Code);
    Assert.Empty(process.Remaining);
  }

  [Fact]
  public async Task DetectAsync_RequiresManifestInSelectedVisualStudioInstance()
  {
    var manifests = new FakeManifestReader();
    manifests.Add("JetBrains.ReSharper", "2025.2.1", "17.0_b");
    var provider = Provider(manifests, new ThrowingProcessExecutor());

    var state = await provider.DetectAsync(
        ReSharperResource(["visual-studio"]),
        CancellationToken.None);

    Assert.False(state.Exists);
    Assert.Equal("17.0_a", state.Evidence["visualStudioInstanceId"]);
  }

  [Fact]
  public async Task VerifyAsync_RequiresMatchingVersionAndTargetInstanceIntegration()
  {
    var manifests = new FakeManifestReader();
    manifests.Add("JetBrains.ReSharper", "2025.2.1", "17.0_a");
    manifests.Add("JetBrains.ReSharper", "2026.1.0", "17.0_b");
    var provider = Provider(manifests, new ThrowingProcessExecutor());

    var verification = await provider.VerifyAsync(
        ReSharperResource(["visual-studio"]),
        CancellationToken.None);

    Assert.Equal(ComplianceStatus.Satisfied, verification.Compliance);
    Assert.Equal("2025.2.1", verification.DetectedState.Version);
    Assert.Equal(
        "17.0_a",
        verification.DetectedState.Evidence["visualStudioInstanceId"]);
  }

  [Fact]
  public async Task VerifyAsync_RejectsManifestWithIncompatibleInstallationTarget()
  {
    var manifests = new FakeManifestReader();
    manifests.Add(
        "JetBrains.ReSharper",
        "2025.2.1",
        "17.0_a",
        [new VsixInstallationTarget("Microsoft.VisualStudio.Enterprise", "[17.0,18.0)")]);
    var provider = Provider(manifests, new ThrowingProcessExecutor());

    var verification = await provider.VerifyAsync(
        ReSharperResource(["visual-studio"]),
        CancellationToken.None);

    Assert.Equal(ComplianceStatus.DetectionFailed, verification.Compliance);
  }

  [Fact]
  public async Task ApplyAsync_InstallsFixedPackageThenVerifiesManifestWithoutLaunchingApplications()
  {
    var manifests = new FakeManifestReader();
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--versions", "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2026.1.0", "2025.2.1", "2025.2.3"));
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--versions", "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.3"));
    process.Enqueue(
        "winget", ["install", "--id", "JetBrains.ReSharper", "--exact",
         "--version", "2025.2.3", "--silent",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
        () => manifests.Add("JetBrains.ReSharper", "2025.2.3", "17.0_a"));
    var provider = Provider(manifests, process);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Empty(process.Remaining);
    Assert.All(process.Requests, request => Assert.Equal("winget", request.FileName));
  }

  [Fact]
  public async Task ApplyAsync_SourceCannotSubstituteAnotherSatisfyingVersion()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.1"));
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.2"));
    var provider = Provider(new FakeManifestReader(), process);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DownloadError, result.Error!.Code);
    Assert.Empty(process.Remaining);
  }

  [Fact]
  public async Task ApplyAsync_ReplacedExactVersionEvidenceRefusesBeforeSourceQuery()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.1"));
    var provider = Provider(new FakeManifestReader(), process);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var step = Assert.Single(plan.Steps);
    var replaced = plan with
    {
      Steps = [step with { Id = step.Id.Replace("2025.2.1", "2025.2.2") }]
    };

    var result = await provider.ApplyAsync(resource, replaced, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Empty(process.Remaining);
    Assert.Single(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_WinGetFailureRemainsFailedWhenManifestBecomesCompliant()
  {
    var manifests = new FakeManifestReader();
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--versions", "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.1"));
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--versions", "--accept-source-agreements", "--disable-interactivity"],
        result: Success("2025.2.1"));
    process.Enqueue(
        "winget", ["install", "--id", "JetBrains.ReSharper", "--exact",
         "--version", "2025.2.1", "--silent",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
        () => manifests.Add("JetBrains.ReSharper", "2025.2.1", "17.0_a"),
        new ProcessExecutionResult(true, 1, [], []));
    var provider = Provider(manifests, process);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(1, result.Error!.ProcessExitCode);
  }

  [Fact]
  public async Task ValidateAsync_RejectsArbitraryPackageOverride()
  {
    var provider = Provider(new FakeManifestReader(), new ThrowingProcessExecutor());
    var resource = ReSharperResource(["visual-studio"]) with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["instanceId"] = "17.0_a",
        ["packageId"] = "Untrusted.Package"
      }
    };

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.Errors, error => error.Contains("packageId", StringComparison.Ordinal));
  }

  private static ReSharperProvider Provider(
      FakeManifestReader manifests,
      IProcessExecutor process,
      IVisualStudioDiscovery? discovery = null) => new(
          discovery ?? new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
          manifests,
          new WinGetCommandClient(process),
          new ComplianceEvaluator());

  private static ResourceDefinition ReSharperResource(IReadOnlyList<string> dependsOn) => new()
  {
    Id = "resharper",
    Type = "resharper",
    Provider = "winget",
    VersionConstraint = "2025.2.x",
    Dependencies = dependsOn,
    Parameters = new Dictionary<string, string?>
    {
      ["instanceId"] = "17.0_a"
    }
  };

  private static ResourceDefinition WithOptionalInstanceSelector(ResourceDefinition resource)
  {
    var parameters = resource.Parameters
        .Where(pair => !string.Equals(pair.Key, "instanceId", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    parameters["productId"] = "Microsoft.VisualStudio.Product.Community";
    parameters["edition"] = "Community";
    parameters["channelId"] = "VisualStudio.17.Release";
    return resource with { Parameters = parameters };
  }

  private static DetectedState Missing(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static ProcessExecutionResult Success(params string[] output) =>
      new(true, 0, output, []);

  private static VisualStudioInstance Instance(string instanceId) => new()
  {
    InstanceId = instanceId,
    InstallationPath = $@"C:\VS\{instanceId}",
    ProductId = "Microsoft.VisualStudio.Product.Community",
    ProductPath = $@"C:\VS\{instanceId}\Common7\IDE\devenv.exe",
    ProductDisplayVersion = "17.0",
    InstallationVersion = "17.0.0",
    ChannelId = "VisualStudio.17.Release",
    Edition = "Community",
    IsComplete = true,
    IsLaunchable = true
  };

  private sealed class FakeVisualStudioDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VisualStudioInstance>>(
            instances);
  }

  private sealed class MutableVisualStudioDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public IReadOnlyList<VisualStudioInstance> Instances { get; set; } = instances;

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) => Task.FromResult(Instances);
  }

  private sealed class FakeManifestReader : IVsixManifestReader
  {
    private readonly List<VsixManifest> _manifests = [];

    public void Add(
        string id,
        string version,
        string instanceId,
        IReadOnlyList<VsixInstallationTarget>? targets = null) => _manifests.Add(
        new VsixManifest(
            id,
            version,
            $@"C:\VS\{instanceId}\Extensions\{id}\extension.vsixmanifest",
            instanceId,
            targets));

    public Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
        VisualStudioInstance instance,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VsixManifest>>(
            _manifests.Where(manifest => string.Equals(
                manifest.VisualStudioInstanceId,
                instance.InstanceId,
                StringComparison.OrdinalIgnoreCase)).ToArray());

    public Task<VsixManifestReadResult> ReadSourceAsync(
        string path,
        string visualStudioInstanceId,
        CancellationToken cancellationToken) => throw new InvalidOperationException();
  }

  private sealed class ScriptedProcessExecutor : IProcessExecutor
  {
    private readonly Queue<(
        string FileName,
        IReadOnlyList<string> Arguments,
        Action? Action,
        ProcessExecutionResult? Result)> _steps = [];

    public List<ProcessExecutionRequest> Requests { get; } = [];
    public IReadOnlyCollection<object> Remaining => _steps.Cast<object>().ToArray();

    public void Enqueue(
        string fileName,
        IReadOnlyList<string> arguments,
        Action? action = null,
        ProcessExecutionResult? result = null) => _steps.Enqueue((fileName, arguments, action, result));

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      var expected = _steps.Dequeue();
      Assert.Equal(expected.FileName, request.FileName);
      Assert.Equal(expected.Arguments, request.Arguments);
      Requests.Add(request);
      expected.Action?.Invoke();
      return Task.FromResult(
          expected.Result ?? new ProcessExecutionResult(true, 0, ["available"], []));
    }
  }

  private sealed class ThrowingProcessExecutor : IProcessExecutor
  {
    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken) => throw new InvalidOperationException();
  }

  private sealed class CountingSuccessProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return Task.FromResult(Success("2025.2.1"));
    }
  }
}
