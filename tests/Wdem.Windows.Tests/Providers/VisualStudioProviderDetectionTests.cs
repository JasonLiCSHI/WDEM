using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Providers;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class VisualStudioProviderDetectionTests
{
  [Fact]
  public async Task Factory_RegistersVisualStudioProvider()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-vs-provider-{Guid.NewGuid():N}");
    var profiles = Path.Combine(root, "profiles");
    Directory.CreateDirectory(profiles);
    try
    {
      var composition = await WdemWindowsFactory.CreateAsync(
          profiles,
          new WdemDataPaths(Path.Combine(root, "data")),
          CancellationToken.None);

      Assert.IsType<VisualStudioProvider>(
          composition.Providers.GetRequired("visual-studio", "visual-studio"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task DetectAsync_MultipleMatchingInstancesWithoutSelector_ReturnsConflict()
  {
    var discovery = new StubVisualStudioDiscovery
    {
      Instances =
      [
        Instance("a", "18.3.2", "Community", "VisualStudio.18.Release"),
        Instance("b", "18.3.2", "Community", "VisualStudio.18.Release")
      ]
    };
    var provider = new VisualStudioProvider(discovery, new ComplianceEvaluator());

    var state = await provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
    Assert.Contains("instanceId", state.StructuredError.Detail, StringComparison.Ordinal);
    Assert.Contains("a", state.StructuredError.Detail, StringComparison.Ordinal);
    Assert.Contains("b", state.StructuredError.Detail, StringComparison.Ordinal);
    Assert.Equal("a;b", state.Evidence["candidateInstanceIds"]);
  }

  [Fact]
  public async Task DetectAsync_ReportsInstanceVersionEditionChannelWorkloadsAndComponents()
  {
    const string workload = "Microsoft.VisualStudio.Workload.ManagedDesktop";
    const string component = "Microsoft.NetCore.Component.Runtime.10.0";
    var discovery = new StubVisualStudioDiscovery
    {
      Instances =
      [
        Instance(
            "17.0_abc",
            "18.3.2",
            "Community",
            "VisualStudio.18.Release",
            workloads: new HashSet<string>([workload], StringComparer.OrdinalIgnoreCase),
            components: new HashSet<string>([component], StringComparer.OrdinalIgnoreCase))
      ]
    };
    var provider = new VisualStudioProvider(discovery, new ComplianceEvaluator());

    var state = await provider.DetectAsync(
        VisualStudioResource(workloads: [workload], components: [component]),
        CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.True(state.Exists);
    Assert.Equal("18.3.2", state.Version);
    Assert.Equal("17.0_abc", state.Evidence["instanceId"]);
    Assert.Equal(@"C:\VS\17.0_abc", state.Evidence["installationPath"]);
    Assert.Equal("Microsoft.VisualStudio.Product.Community", state.Evidence["productId"]);
    Assert.Equal(@"C:\VS\17.0_abc\Common7\IDE\devenv.exe", state.Evidence["productPath"]);
    Assert.Equal("18.3.2", state.Evidence["productDisplayVersion"]);
    Assert.Equal("18.3.2.0", state.Evidence["installationVersion"]);
    Assert.Equal("Community", state.Evidence["edition"]);
    Assert.Equal("VisualStudio.18.Release", state.Evidence["channel"]);
    Assert.Equal("true", state.Evidence["isComplete"]);
    Assert.Equal("true", state.Evidence["isLaunchable"]);
    Assert.Equal(workload, state.Evidence["workloads"]);
    Assert.Equal(component, state.Evidence["components"]);
    Assert.Equal([workload], discovery.RequestedWorkloads);
    Assert.Equal([component], discovery.RequestedComponents);
  }

  [Fact]
  public async Task DetectAsync_IncompleteInstance_RemainsVisibleWithHealthEvidence()
  {
    var incomplete = Instance(
        "repairable",
        "18.3.2",
        "Community",
        "VisualStudio.18.Release") with
    {
      IsComplete = false,
      IsLaunchable = false
    };
    var provider = new VisualStudioProvider(
        new StubVisualStudioDiscovery { Instances = [incomplete] },
        new ComplianceEvaluator());

    var state = await provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("repairable", state.Evidence["instanceId"]);
    Assert.Equal("false", state.Evidence["isComplete"]);
    Assert.Equal("false", state.Evidence["isLaunchable"]);
  }

  [Fact]
  public async Task PlanCanDescribeInstall_ButApplyReturnsExplicitProviderError()
  {
    var provider = new VisualStudioProvider(
        new StubVisualStudioDiscovery(),
        new ComplianceEvaluator());
    var resource = VisualStudioResource();
    var missing = new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    };

    var plan = await provider.PlanAsync(resource, missing, CancellationToken.None);
    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    var step = Assert.Single(plan.Steps);
    Assert.True(plan.IsExecutable);
    Assert.Equal(PlanAction.Install, step.Action);
    Assert.Contains("Visual Studio", step.Description, StringComparison.Ordinal);
    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, result.Error!.Code);
    Assert.Contains("not implemented", result.Error.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task DetectAsync_FiltersProductEditionChannelAndVersionBeforeSelection()
  {
    var discovery = new StubVisualStudioDiscovery
    {
      Instances =
      [
        Instance("old", "17.9.0", "Community", "VisualStudio.18.Release"),
        Instance("professional", "18.3.2", "Professional", "VisualStudio.18.Release"),
        Instance("preview", "18.3.2", "Community", "VisualStudio.18.Preview"),
        Instance("selected", "18.3.2", "Community", "VisualStudio.18.Release")
      ]
    };
    var provider = new VisualStudioProvider(discovery, new ComplianceEvaluator());

    var state = await provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("selected", state.Evidence["instanceId"]);
  }

  [Fact]
  public async Task DetectAsync_InstanceIdSelectsOneMatchingInstance()
  {
    var discovery = new StubVisualStudioDiscovery
    {
      Instances =
      [
        Instance("a", "18.3.2", "Community", "VisualStudio.18.Release"),
        Instance("b", "18.3.2", "Community", "VisualStudio.18.Release")
      ]
    };
    var provider = new VisualStudioProvider(discovery, new ComplianceEvaluator());

    var state = await provider.DetectAsync(
        VisualStudioResource(instanceId: "b"),
        CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("b", state.Evidence["instanceId"]);
  }

  [Fact]
  public async Task DetectAsync_InstanceIdOutsideMatchingCandidates_ReturnsConflict()
  {
    var discovery = new StubVisualStudioDiscovery
    {
      Instances =
      [
        Instance("a", "18.3.2", "Community", "VisualStudio.18.Release"),
        Instance("b", "18.3.2", "Community", "VisualStudio.18.Release"),
        Instance("c", "18.3.2", "Professional", "VisualStudio.18.Release")
      ]
    };
    var provider = new VisualStudioProvider(discovery, new ComplianceEvaluator());

    var state = await provider.DetectAsync(
        VisualStudioResource(instanceId: "c"),
        CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
    Assert.Contains("instanceId", state.StructuredError.Detail, StringComparison.Ordinal);
    Assert.Equal("a;b", state.Evidence["candidateInstanceIds"]);
  }

  [Fact]
  public async Task DetectAsync_DiscoveryFailure_ReturnsStructuredDetectionError()
  {
    var provider = new VisualStudioProvider(
        new StubVisualStudioDiscovery
        {
          Exception = new InvalidDataException("vswhere returned malformed JSON")
        },
        new ComplianceEvaluator());

    var state = await provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
    Assert.Contains("vswhere", state.StructuredError.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.IsType<InvalidDataException>(state.StructuredError.UnderlyingException);
  }

  private static ResourceDefinition VisualStudioResource(
      string? instanceId = null,
      string? versionConstraint = ">= 18.0",
      IReadOnlyList<string>? workloads = null,
      IReadOnlyList<string>? components = null)
  {
    var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
      ["productId"] = "Microsoft.VisualStudio.Product.Community",
      ["edition"] = "Community",
      ["channelId"] = "VisualStudio.18.Release",
      ["workloads"] = string.Join(
          ';',
          workloads ?? ["Microsoft.VisualStudio.Workload.ManagedDesktop"]),
      ["components"] = string.Join(
          ';',
          components ?? ["Microsoft.VisualStudio.Component.Git"])
    };
    if (instanceId is not null)
    {
      parameters["instanceId"] = instanceId;
    }

    return new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      VersionConstraint = versionConstraint,
      Parameters = parameters
    };
  }

  private static VisualStudioInstance Instance(
      string id,
      string version,
      string edition,
      string channel,
      IReadOnlySet<string>? workloads = null,
      IReadOnlySet<string>? components = null) => new()
      {
        InstanceId = id,
        InstallationPath = $@"C:\VS\{id}",
        ProductId = $"Microsoft.VisualStudio.Product.{edition}",
        ProductPath = $@"C:\VS\{id}\Common7\IDE\devenv.exe",
        ProductDisplayVersion = version,
        InstallationVersion = $"{version}.0",
        ChannelId = channel,
        Edition = edition,
        IsComplete = true,
        IsLaunchable = true,
        Workloads = workloads ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        Components = components ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
      };

  private sealed class StubVisualStudioDiscovery : IVisualStudioDiscovery
  {
    public IReadOnlyList<VisualStudioInstance> Instances { get; init; } = [];
    public Exception? Exception { get; init; }
    public IReadOnlyList<string> RequestedWorkloads { get; private set; } = [];
    public IReadOnlyList<string> RequestedComponents { get; private set; } = [];

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (Exception is not null)
      {
        throw Exception;
      }

      RequestedWorkloads = requestedWorkloads;
      RequestedComponents = requestedComponents;
      return Task.FromResult(Instances);
    }
  }
}
