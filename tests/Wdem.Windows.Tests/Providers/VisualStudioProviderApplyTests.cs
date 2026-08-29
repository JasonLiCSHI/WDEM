using System.Security.Cryptography;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class VisualStudioProviderApplyTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-vs-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_ExistingInstance_ModifiesMissingWorkloadAndComponent()
  {
    var discovery = new SequenceDiscovery(
    [
      [Instance("17.0_a")],
      [Instance(
          "17.0_a",
          workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
          components: ["Microsoft.NetCore.Component.Runtime.10.0"])]
    ]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource,
        State(Instance("17.0_a")),
        CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(
        [
          "modify", "--installPath", @"C:\VS",
          "--add", "Microsoft.VisualStudio.Workload.ManagedDesktop",
          "--add", "Microsoft.NetCore.Component.Runtime.10.0",
          "--passive", "--wait", "--norestart"
        ],
        installer.LastArguments);
  }

  [Fact]
  public async Task PlanAsync_VsconfigHashMismatch_IsNonExecutable()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "profile.vsconfig");
    await File.WriteAllTextAsync(vsconfig, "{}");
    var provider = Provider(new SequenceDiscovery([]), new RecordingInstallerClient());

    var plan = await provider.PlanAsync(
        Resource(vsconfig, new string('A', 64)),
        MissingState(),
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ConfigurationError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Fact]
  public async Task ApplyAsync_VerifiedVsconfigIsPassedAndRedetectedBeforeSuccess()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "profile.vsconfig");
    await File.WriteAllTextAsync(vsconfig, "{}");
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vsconfig)));
    var compliant = Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"]);
    var discovery = new SequenceDiscovery([[Instance("17.0_a")], [compliant]]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource(vsconfig, hash);
    var plan = await provider.PlanAsync(
        resource,
        State(Instance("17.0_a")),
        CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Contains("--config", installer.LastArguments);
    Assert.Contains(Path.GetFullPath(vsconfig), installer.LastArguments);
    Assert.True(discovery.CallCount > 0);
  }

  [Fact]
  public async Task PlanAsync_InstallAndModifyRequireAdministratorAndNoOpHasNoSteps()
  {
    var provider = Provider(new SequenceDiscovery([]), new RecordingInstallerClient());
    var resource = Resource();

    var install = await provider.PlanAsync(resource, MissingState(), CancellationToken.None);
    var modify = await provider.PlanAsync(
        resource,
        State(Instance("17.0_a")),
        CancellationToken.None);
    var satisfied = await provider.PlanAsync(
        resource,
        State(Instance(
            "17.0_a",
            workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
            components: ["Microsoft.NetCore.Component.Runtime.10.0"])),
        CancellationToken.None);

    Assert.Equal(PlanAction.Install, Assert.Single(install.Steps).Action);
    Assert.Equal(PrivilegeRequirement.Administrator, install.Steps[0].PrivilegeRequirement);
    Assert.Equal(PlanAction.Configure, Assert.Single(modify.Steps).Action);
    Assert.Equal(PrivilegeRequirement.Administrator, modify.Steps[0].PrivilegeRequirement);
    Assert.Empty(satisfied.Steps);
  }

  [Fact]
  public async Task VerifyAsync_MissingComponentDoesNotReportSuccess()
  {
    var discovery = new SequenceDiscovery([[Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"])]]);
    var provider = Provider(discovery, new RecordingInstallerClient());

    var result = await provider.VerifyAsync(Resource(), CancellationToken.None);

    Assert.Equal(ComplianceStatus.ConfigurationMismatch, result.Compliance);
  }

  [Fact]
  public async Task ApplyAsync_ReportsFourRequiredProgressPhases()
  {
    var discovery = new SequenceDiscovery(
    [[Instance("17.0_a")], [Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"])]]);
    var provider = Provider(discovery, new RecordingInstallerClient());
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource,
        State(Instance("17.0_a")),
        CancellationToken.None);
    var reports = new List<ProviderProgress>();

    await provider.ApplyAsync(
        resource,
        plan,
        new InlineProgress(reports.Add),
        CancellationToken.None);

    Assert.Equal(
        ["BootstrapperVerification", "Modify", "Configuration", "Verification"],
        reports.Select(report => report.Stage));
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }

  private static VisualStudioProvider Provider(
      IVisualStudioDiscovery discovery,
      IVisualStudioInstallerClient installer) => new(
          discovery,
          installer,
          new TrustedFileVerifier(),
          new ComplianceEvaluator());

  private static ResourceDefinition Resource(
      string? vsconfigPath = null,
      string? expectedSha256 = null)
  {
    var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
      ["productId"] = "Microsoft.VisualStudio.Product.Community",
      ["edition"] = "Community",
      ["channelId"] = "VisualStudio.18.Release",
      ["installPath"] = @"C:\VS",
      ["workloads"] = "Microsoft.VisualStudio.Workload.ManagedDesktop",
      ["components"] = "Microsoft.NetCore.Component.Runtime.10.0"
    };
    if (vsconfigPath is not null)
    {
      parameters["vsconfigPath"] = vsconfigPath;
      parameters["expectedSha256"] = expectedSha256;
    }

    return new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      VersionConstraint = ">= 18.0",
      Parameters = parameters
    };
  }

  private static DetectedState MissingState() => new()
  {
    ResourceId = "visual-studio",
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static DetectedState State(VisualStudioInstance instance) => new()
  {
    ResourceId = "visual-studio",
    Outcome = DetectionOutcome.Succeeded,
    Exists = true,
    Version = instance.ProductDisplayVersion,
    Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["instanceId"] = instance.InstanceId,
      ["installationPath"] = instance.InstallationPath,
      ["edition"] = instance.Edition,
      ["channel"] = instance.ChannelId,
      ["workloads"] = string.Join(';', instance.Workloads),
      ["components"] = string.Join(';', instance.Components)
    }
  };

  private static VisualStudioInstance Instance(
      string id,
      string[]? workloads = null,
      string[]? components = null) => new()
      {
        InstanceId = id,
        InstallationPath = @"C:\VS",
        ProductId = "Microsoft.VisualStudio.Product.Community",
        ProductPath = @"C:\VS\Common7\IDE\devenv.exe",
        ProductDisplayVersion = "18.3.2",
        InstallationVersion = "18.3.2.0",
        ChannelId = "VisualStudio.18.Release",
        Edition = "Community",
        IsComplete = true,
        IsLaunchable = true,
        Workloads = new HashSet<string>(workloads ?? [], StringComparer.OrdinalIgnoreCase),
        Components = new HashSet<string>(components ?? [], StringComparer.OrdinalIgnoreCase)
      };

  private sealed class SequenceDiscovery(
      IReadOnlyList<IReadOnlyList<VisualStudioInstance>> sequences)
      : IVisualStudioDiscovery
  {
    private int _index;
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      CallCount++;
      if (sequences.Count == 0)
      {
        return Task.FromResult<IReadOnlyList<VisualStudioInstance>>([]);
      }

      var result = sequences[Math.Min(_index, sequences.Count - 1)];
      _index++;
      return Task.FromResult(result);
    }
  }

  private sealed class RecordingInstallerClient : IVisualStudioInstallerClient
  {
    public IReadOnlyList<string> LastArguments { get; private set; } = [];

    public Task<TrustedFileVerificationResult> AcquireBootstrapperAsync(
        Uri source,
        string expectedSha256,
        CancellationToken cancellationToken) => Task.FromResult(
            new TrustedFileVerificationResult(
                true,
                @"C:\verified\vs-bootstrapper.exe",
                expectedSha256,
                null));

    public Task<VisualStudioInstallerResult> InstallAsync(
        string executablePath,
        string productId,
        Uri? channelUri,
        string installPath,
        IReadOnlyList<string> workloads,
        IReadOnlyList<string> components,
        string? vsconfigPath,
        CancellationToken cancellationToken)
    {
      LastArguments = VisualStudioInstallerClient.CreateInstallArguments(
          productId, channelUri, installPath, workloads, components, vsconfigPath);
      return Task.FromResult(Success(executablePath));
    }

    public Task<VisualStudioInstallerResult> ModifyAsync(
        string executablePath,
        string installPath,
        IReadOnlyList<string> workloads,
        IReadOnlyList<string> components,
        string? vsconfigPath,
        CancellationToken cancellationToken)
    {
      LastArguments = VisualStudioInstallerClient.CreateModifyArguments(
          installPath, workloads, components, vsconfigPath);
      return Task.FromResult(Success(executablePath));
    }

    private static VisualStudioInstallerResult Success(string executablePath) => new(
        new ProcessExecutionResult(true, 0, [], []),
        RestartPolicy.NoRestart,
        new Dictionary<string, string> { ["installerPath"] = executablePath });
  }

  private sealed class InlineProgress(Action<ProviderProgress> report)
      : IProgress<ProviderProgress>
  {
    public void Report(ProviderProgress value) => report(value);
  }
}
