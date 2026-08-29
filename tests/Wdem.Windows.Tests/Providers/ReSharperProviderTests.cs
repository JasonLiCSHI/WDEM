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
  public async Task ApplyAsync_InstallsFixedPackageThenVerifiesManifestWithoutLaunchingApplications()
  {
    var manifests = new FakeManifestReader();
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--accept-source-agreements", "--disable-interactivity"]);
    process.Enqueue(
        "winget",
        ["show", "--id", "JetBrains.ReSharper", "--exact",
         "--accept-source-agreements", "--disable-interactivity"]);
    process.Enqueue(
        "winget",
        ["install", "--id", "JetBrains.ReSharper", "--exact", "--silent",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
        () => manifests.Add("JetBrains.ReSharper", "2025.2.1", "17.0_a"));
    var provider = Provider(manifests, process);
    var resource = ReSharperResource(["visual-studio"]);
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Empty(process.Remaining);
    Assert.All(process.Requests, request => Assert.Equal("winget", request.FileName));
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
      IProcessExecutor process) => new(
          new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
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

  private static DetectedState Missing(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

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

  private sealed class FakeManifestReader : IVsixManifestReader
  {
    private readonly List<VsixManifest> _manifests = [];

    public void Add(string id, string version, string instanceId) => _manifests.Add(
        new VsixManifest(
            id,
            version,
            $@"C:\VS\{instanceId}\Extensions\{id}\extension.vsixmanifest",
            instanceId));

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
    private readonly Queue<(string FileName, IReadOnlyList<string> Arguments, Action? Action)> _steps = [];

    public List<ProcessExecutionRequest> Requests { get; } = [];
    public IReadOnlyCollection<object> Remaining => _steps.Cast<object>().ToArray();

    public void Enqueue(
        string fileName,
        IReadOnlyList<string> arguments,
        Action? action = null) => _steps.Enqueue((fileName, arguments, action));

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
      return Task.FromResult(new ProcessExecutionResult(true, 0, ["available"], []));
    }
  }

  private sealed class ThrowingProcessExecutor : IProcessExecutor
  {
    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken) => throw new InvalidOperationException();
  }
}
