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
    Assert.True(result.RestartRequirement.HasValue);
    Assert.Equal(RestartPolicy.NoRestart, result.RestartRequirement.Value);
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
  public async Task ApplyAsync_ExistingInstancePathChangeAfterPlanningFailsBeforeModify()
  {
    var original = Instance("17.0_a");
    var moved = original with
    {
      InstallationPath = @"D:\MovedVS",
      ProductPath = @"D:\MovedVS\Common7\IDE\devenv.exe"
    };
    var discovery = new SequenceDiscovery([[moved]]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(resource, State(original), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Contains("changed after planning", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(installer.LastArguments);
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
    await File.WriteAllTextAsync(vsconfig, Vsconfig(
        "Microsoft.VisualStudio.Workload.NetWeb",
        "Microsoft.VisualStudio.Component.Git"));
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vsconfig)));
    var compliant = Instance(
        "17.0_a",
        workloads:
        [
          "Microsoft.VisualStudio.Workload.ManagedDesktop",
          "Microsoft.VisualStudio.Workload.NetWeb"
        ],
        components:
        [
          "Microsoft.NetCore.Component.Runtime.10.0",
          "Microsoft.VisualStudio.Component.Git"
        ]);
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
    Assert.NotEqual(Path.GetFullPath(vsconfig), installer.LastVsConfigPath);
    Assert.Contains(installer.LastVsConfigPath, installer.LastArguments);
    Assert.Contains("Microsoft.VisualStudio.Workload.NetWeb", discovery.RequestedWorkloads);
    Assert.Contains("Microsoft.VisualStudio.Component.Git", discovery.RequestedComponents);
  }

  [Fact]
  public async Task PlanAsync_EmptyVsconfigIsNonExecutable()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "empty.vsconfig");
    await File.WriteAllTextAsync(vsconfig, "{}");
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vsconfig)));
    var provider = Provider(new SequenceDiscovery([]), new RecordingInstallerClient());

    var plan = await provider.PlanAsync(
        Resource(vsconfig, hash), MissingState(), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ConfigurationError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Fact]
  public async Task ApplyAsync_MissingVsconfigComponentFailsPostExecutionVerification()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "profile.vsconfig");
    await File.WriteAllTextAsync(vsconfig, Vsconfig("Microsoft.VisualStudio.Component.Git"));
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vsconfig)));
    var installedWithoutConfigComponent = Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"]);
    var discovery = new SequenceDiscovery(
        [[Instance("17.0_a")], [installedWithoutConfigComponent]]);
    var provider = Provider(discovery, new RecordingInstallerClient());
    var resource = Resource(vsconfig, hash);
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    Assert.Contains("Microsoft.VisualStudio.Component.Git", discovery.RequestedComponents);
  }

  [Fact]
  public async Task ApplyAsync_VsconfigReplacementCannotChangeRestrictedStagedConfiguration()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "profile.vsconfig");
    var trustedContents = Vsconfig("Microsoft.VisualStudio.Component.Git");
    await File.WriteAllTextAsync(vsconfig, trustedContents);
    var trustedBytes = await File.ReadAllBytesAsync(vsconfig);
    var hash = Convert.ToHexString(SHA256.HashData(trustedBytes));
    var policy = new RecordingSecureDirectoryPolicy();
    byte[]? configuredBytes = null;
    var installer = new RecordingInstallerClient
    {
      BeforeModify = stagedPath =>
      {
        File.WriteAllText(
            vsconfig,
            Vsconfig("Microsoft.VisualStudio.Component.Replacement"));
        configuredBytes = File.ReadAllBytes(stagedPath!);
      }
    };
    var discovery = new SequenceDiscovery(
    [
      [Instance("17.0_a")],
      [Instance(
          "17.0_a",
          workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
          components:
          [
            "Microsoft.NetCore.Component.Runtime.10.0",
            "Microsoft.VisualStudio.Component.Git"
          ])]
    ]);
    var provider = Provider(
        discovery,
        installer,
        new SecureArtifactStager(policy));
    var resource = Resource(vsconfig, hash);
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.NotEqual(Path.GetFullPath(vsconfig), installer.LastVsConfigPath);
    Assert.Equal(trustedBytes, configuredBytes);
    Assert.Contains(
        Path.GetDirectoryName(installer.LastVsConfigPath!)!,
        policy.SecuredDirectories);
    Assert.False(File.Exists(installer.LastVsConfigPath));
  }

  [Fact]
  public async Task PlanAndApplyAsync_ExecutableOperationsRequireAdministratorAndNoOpReturnsNotRequired()
  {
    var discovery = new SequenceDiscovery([]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
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
    var noOpResult = await provider.ApplyAsync(
        resource,
        satisfied,
        null,
        CancellationToken.None);

    Assert.Equal(PlanAction.Install, Assert.Single(install.Steps).Action);
    Assert.Equal(PrivilegeRequirement.Administrator, install.Steps[0].PrivilegeRequirement);
    Assert.Equal(PlanAction.Configure, Assert.Single(modify.Steps).Action);
    Assert.Equal(PrivilegeRequirement.Administrator, modify.Steps[0].PrivilegeRequirement);
    Assert.False(satisfied.RequiresApply);
    Assert.Empty(satisfied.Steps);
    Assert.Equal(ApplyOutcome.NotRequired, noOpResult.Outcome);
    Assert.Empty(installer.Operations);
    Assert.Equal(0, discovery.AttemptCount);
  }

  [Fact]
  public async Task ApplyAsync_VersionMismatchUpdatesAndConfiguresExactExistingInstanceWithoutInstall()
  {
    var old = Instance("17.0_a", version: "17.9.0");
    var upgraded = Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"]);
    var discovery = new SequenceDiscovery([[old], [upgraded]]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(resource, State(old), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(PlanAction.Upgrade, Assert.Single(plan.Steps).Action);
    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(["update", "modify"], installer.Operations);
    Assert.DoesNotContain("install", installer.Operations);
    Assert.Equal(@"C:\VS", installer.LastInstallPath);
  }

  [Theory]
  [InlineData("17.9.0", false)]
  [InlineData("18.3.2", false)]
  [InlineData("18.3.2", true)]
  public async Task ApplyAsync_StaleInstallPlanFailsBeforeExecutingUnapprovedOperation(
      string version,
      bool alreadyConfigured)
  {
    var appeared = Instance(
        "17.0_a",
        workloads: alreadyConfigured
            ? ["Microsoft.VisualStudio.Workload.ManagedDesktop"]
            : [],
        components: alreadyConfigured
            ? ["Microsoft.NetCore.Component.Runtime.10.0"]
            : [],
        version: version);
    var discovery = new SequenceDiscovery([[appeared]]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var staleInstallPlan = await provider.PlanAsync(
        resource,
        MissingState(),
        CancellationToken.None);

    var result = await provider.ApplyAsync(
        resource,
        staleInstallPlan,
        null,
        CancellationToken.None);

    Assert.Equal(PlanAction.Install, Assert.Single(staleInstallPlan.Steps).Action);
    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
    Assert.Equal("Visual Studio state changed after planning.", result.Error.Summary);
    Assert.Contains("detect and plan again", result.Error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(installer.Operations);
    Assert.Equal(1, discovery.AttemptCount);
  }

  [Fact]
  public async Task ApplyAsync_StaleInstallPlanWithAmbiguousInstancesFailsBeforeInstall()
  {
    var discovery = new SequenceDiscovery(
    [
      [Instance("17.0_a"), Instance("17.0_b")]
    ]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var staleInstallPlan = await provider.PlanAsync(
        resource,
        MissingState(),
        CancellationToken.None);

    var result = await provider.ApplyAsync(
        resource,
        staleInstallPlan,
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
    Assert.Equal("Multiple Visual Studio instances match.", result.Error.Summary);
    Assert.Contains("17.0_a, 17.0_b", result.Error.Detail, StringComparison.Ordinal);
    Assert.Empty(installer.Operations);
    Assert.Equal(1, discovery.AttemptCount);
  }

  [Fact]
  public async Task ApplyAsync_CancelledAfterUpdatePreservesRestartEvidenceWithoutModify()
  {
    using var cancellation = new CancellationTokenSource();
    var old = Instance("17.0_a", version: "17.9.0");
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, 3010, [], []),
          RestartPolicy.RestartRecommended,
          new Dictionary<string, string>
          {
            ["installerOperation"] = "update",
            ["restartRequirement"] = "RestartRecommended"
          }),
      AfterUpdate = cancellation.Cancel
    };
    var provider = Provider(new SequenceDiscovery([[old]]), installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(resource, State(old), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, cancellation.Token);

    var step = Assert.Single(result.StepResults);
    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.CancellationError, result.Error!.Code);
    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.Contains(
        "restartRequirement=RestartRecommended",
        step.Message,
        StringComparison.Ordinal);
    Assert.Equal(["update"], installer.Operations);
  }

  [Fact]
  public async Task ApplyAsync_VersionAndConfigurationMismatchUpdatesThenModifiesVerifiedSnapshot()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "upgrade.vsconfig");
    await File.WriteAllTextAsync(
        vsconfig,
        Vsconfig("Microsoft.VisualStudio.Component.Git"));
    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vsconfig)));
    var old = Instance("17.0_a", version: "17.9.0");
    var converged = Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components:
        [
          "Microsoft.NetCore.Component.Runtime.10.0",
          "Microsoft.VisualStudio.Component.Git"
        ]);
    var installer = new RecordingInstallerClient();
    var provider = Provider(new SequenceDiscovery([[old], [converged]]), installer);
    var resource = Resource(vsconfig, hash);
    var plan = await provider.PlanAsync(resource, State(old), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(["update", "modify"], installer.Operations);
    Assert.Equal(
        ["update", "--installPath", @"C:\VS", "--passive", "--wait", "--norestart"],
        installer.ArgumentHistory[0]);
    Assert.Equal("modify", installer.ArgumentHistory[1][0]);
    Assert.Contains("Microsoft.VisualStudio.Workload.ManagedDesktop", installer.ArgumentHistory[1]);
    Assert.Contains("Microsoft.NetCore.Component.Runtime.10.0", installer.ArgumentHistory[1]);
    Assert.Contains("--config", installer.ArgumentHistory[1]);
    Assert.NotEqual(Path.GetFullPath(vsconfig), installer.LastVsConfigPath);
    Assert.Contains(installer.LastVsConfigPath, installer.ArgumentHistory[1]);
  }

  [Fact]
  public async Task ApplyAsync_DownloadedBootstrapperSupportsUpdateAndConfigurationInvocations()
  {
    Directory.CreateDirectory(_root);
    var vsconfig = Path.Combine(_root, "downloaded-upgrade.vsconfig");
    await File.WriteAllTextAsync(
        vsconfig,
        Vsconfig("Microsoft.VisualStudio.Component.Git"));
    var configBytes = await File.ReadAllBytesAsync(vsconfig);
    var configHash = Convert.ToHexString(SHA256.HashData(configBytes));
    var bootstrapperBytes = "trusted Visual Studio bootstrapper"u8.ToArray();
    var bootstrapperHash = Convert.ToHexString(SHA256.HashData(bootstrapperBytes));
    var old = Instance("17.0_a", version: "17.9.0");
    var converged = Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components:
        [
          "Microsoft.NetCore.Component.Runtime.10.0",
          "Microsoft.VisualStudio.Component.Git"
        ]);
    var process = new RecordingRealProcessExecutor();
    process.ExitCodes.Enqueue(3010);
    process.ExitCodes.Enqueue(0);
    var downloads = new CountingContentHandler(bootstrapperBytes);
    using var httpClient = new HttpClient(downloads);
    var secureStager = new SecureArtifactStager(new RecordingSecureDirectoryPolicy());
    var installer = new VisualStudioInstallerClient(
        process,
        httpClient: httpClient,
        secureArtifactStager: secureStager,
        bootstrapperDownloadDirectory: Path.Combine(_root, "downloads"));
    var provider = Provider(
        new SequenceDiscovery([[old], [converged]]),
        installer,
        secureStager);
    var resource = Resource(
        vsconfig,
        configHash,
        useBootstrapper: true,
        bootstrapperSha256: bootstrapperHash);
    var plan = await provider.PlanAsync(resource, State(old), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
    Assert.Contains(
        "restartRequirement=RestartRecommended",
        Assert.Single(result.StepResults).Message);
    Assert.Equal(2, downloads.RequestCount);
    Assert.Collection(
        process.Requests,
        request => Assert.Equal("update", request.Arguments[0]),
        request => Assert.Equal("modify", request.Arguments[0]));
    Assert.NotEqual(process.Requests[0].FileName, process.Requests[1].FileName);
    Assert.Equal(2, process.ExecutableSnapshots.Count);
    Assert.All(process.ExecutableSnapshots, bytes => Assert.Equal(bootstrapperBytes, bytes));
    Assert.Equal(configBytes, Assert.Single(process.ConfigurationSnapshots));
    Assert.All(process.Requests, request => Assert.False(File.Exists(request.FileName)));
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
  public async Task VerifyAsync_MissingDetectedVersionWithoutConstraintDoesNotReportSuccess()
  {
    var discovery = new SequenceDiscovery([[Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"],
        version: string.Empty)]]);
    var provider = Provider(discovery, new RecordingInstallerClient());

    var result = await provider.VerifyAsync(
        Resource(versionConstraint: null),
        CancellationToken.None);

    Assert.NotEqual(ComplianceStatus.Satisfied, result.Compliance);
    Assert.Equal(DetectionOutcome.Failed, result.DetectedState.Outcome);
    Assert.NotNull(result.DetectedState.Error);
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

  [Fact]
  public async Task ApplyAsync_PropagatesActualInstallerRestartEvidence()
  {
    var discovery = new SequenceDiscovery(
    [[Instance("17.0_a")], [Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"])]]);
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, 3010, [], []),
          RestartPolicy.RestartRecommended,
          new Dictionary<string, string> { ["installerPath"] = @"C:\setup.exe" })
    };
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_PostVerificationFailureRetainsInstallerRestartEvidence()
  {
    var discovery = new SequenceDiscovery(
    [[Instance("17.0_a")], [Instance("17.0_a")]]);
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, 1641, [], []),
          RestartPolicy.RestartRequired,
          new Dictionary<string, string> { ["installerPath"] = @"C:\setup.exe" })
    };
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, result.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_EnrichesSuppliedFinalComplianceErrorWithExecutionContext()
  {
    var suppliedError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Supplied verification failure.",
        "Keep this safe verification detail.")
    {
      SuggestedAction = "Keep this safe suggested action.",
      IsRetryable = true
    };
    var evaluator = new FinalErrorComplianceEvaluator(suppliedError);
    var discovery = new SequenceDiscovery([[Instance(
        "17.0_a",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"])]]);
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, 3010, [], []),
          RestartPolicy.RestartRecommended,
          new Dictionary<string, string> { ["installerOperation"] = "modify" })
    };
    var provider = Provider(discovery, installer, complianceEvaluator: evaluator);
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);
    var plannedStep = Assert.Single(plan.Steps);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    var error = result.Error!;
    var step = Assert.Single(result.StepResults);
    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(resource.Id, error.ResourceId);
    Assert.Equal(plannedStep.Id, error.StepId);
    Assert.Equal(3010, error.ProcessExitCode);
    Assert.Equal(suppliedError.Code, error.Code);
    Assert.Equal(suppliedError.Summary, error.Summary);
    Assert.Equal(suppliedError.Detail, error.Detail);
    Assert.Equal(suppliedError.SuggestedAction, error.SuggestedAction);
    Assert.Equal(suppliedError.IsRetryable, error.IsRetryable);
    Assert.Equal(plannedStep.Action, step.Action);
    Assert.Equal(error, step.Error);
  }

  [Theory]
  [InlineData(3010, RestartPolicy.RestartRecommended)]
  [InlineData(1641, RestartPolicy.RestartRequired)]
  public async Task ApplyAsync_CancelledDuringFinalVerificationPreservesInstallerEvidence(
      int exitCode,
    RestartPolicy restartRequirement)
  {
    using var cancellation = new CancellationTokenSource();
    SequenceDiscovery? discovery = null;
    discovery = new SequenceDiscovery([[], [Instance("17.0_a")]])
    {
      BeforeDiscover = () =>
      {
        if (discovery!.AttemptCount == 2)
        {
          cancellation.Cancel();
        }
      }
    };
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, exitCode, [], []),
          restartRequirement,
          new Dictionary<string, string>
          {
            ["installerOperation"] = "install",
            ["restartRequirement"] = restartRequirement.ToString()
          })
    };
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(resource, MissingState(), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, cancellation.Token);

    var step = Assert.Single(result.StepResults);
    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.CancellationError, result.Error!.Code);
    Assert.Equal(restartRequirement, result.RestartRequirement);
    Assert.Equal(exitCode, step.ProcessExitCode);
    Assert.Contains(
        $"restartRequirement={restartRequirement}",
        step.Message,
        StringComparison.Ordinal);
    Assert.Equal(["install"], installer.Operations);
    Assert.Equal(2, discovery.AttemptCount);
  }

  [Fact]
  public async Task ApplyAsync_InstallerFailureRetainsVerifiedArtifactEvidence()
  {
    var discovery = new SequenceDiscovery([[Instance("17.0_a")]]);
    var installer = new RecordingInstallerClient
    {
      Result = new VisualStudioInstallerResult(
          new ProcessExecutionResult(true, 500, [], []),
          RestartPolicy.NoRestart,
          new Dictionary<string, string>
          {
            ["installerPath"] = @"C:\verified\vs.exe",
            ["installerSha256"] = new string('A', 64)
          })
    };
    var provider = Provider(discovery, installer);
    var resource = Resource();
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Contains("installerPath=C:\\verified\\vs.exe", Assert.Single(result.StepResults).Message);
  }

  [Fact]
  public async Task ApplyAsync_BootstrapperStagingFailureCorrelatesResourceAndStep()
  {
    var bootstrapperBytes = "trusted Visual Studio bootstrapper"u8.ToArray();
    var bootstrapperHash = Convert.ToHexString(SHA256.HashData(bootstrapperBytes));
    var stagingError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Secure artifact staging failed.",
        "The artifact exceeds the permitted staging size.");
    var process = new RecordingRealProcessExecutor();
    using var httpClient = new HttpClient(new CountingContentHandler(bootstrapperBytes));
    var installer = new VisualStudioInstallerClient(
        process,
        httpClient: httpClient,
        secureArtifactStager: new FailingArtifactStager(stagingError),
        bootstrapperDownloadDirectory: Path.Combine(_root, "failed-download"));
    var provider = Provider(new SequenceDiscovery([]), installer);
    var resource = Resource(
        useBootstrapper: true,
        bootstrapperSha256: bootstrapperHash);
    var plan = await provider.PlanAsync(resource, MissingState(), CancellationToken.None);
    var plannedStep = Assert.Single(plan.Steps);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(resource.Id, result.Error!.ResourceId);
    Assert.Equal(plannedStep.Id, result.Error.StepId);
    Assert.Equal(stagingError.Detail, result.Error.Detail);
    var step = Assert.Single(result.StepResults);
    Assert.Equal(resource.Id, step.Error!.ResourceId);
    Assert.Equal(plannedStep.Id, step.Error.StepId);
    Assert.Equal(stagingError.Detail, step.Error.Detail);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_MissingModifyInstanceFailsBeforeBootstrapperAcquisition()
  {
    var installer = new RecordingInstallerClient();
    var provider = Provider(new SequenceDiscovery([[]]), installer);
    var resource = Resource(useBootstrapper: true);
    var plan = await provider.PlanAsync(
        resource, State(Instance("17.0_a")), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
    Assert.Null(installer.LastAcquisition);
    Assert.Empty(installer.LastArguments);
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
      IVisualStudioInstallerClient installer,
      ISecureArtifactStager? secureArtifactStager = null,
      IComplianceEvaluator? complianceEvaluator = null) => new(
          discovery,
          installer,
          new TrustedFileVerifier(),
          complianceEvaluator ?? new ComplianceEvaluator(),
          secureArtifactStager ?? new SecureArtifactStager(
              new RecordingSecureDirectoryPolicy()));

  private static ResourceDefinition Resource(
      string? vsconfigPath = null,
      string? expectedSha256 = null,
      bool useBootstrapper = false,
      string? versionConstraint = ">= 18.0",
      string? bootstrapperSha256 = null)
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

    if (useBootstrapper)
    {
      parameters["bootstrapperUri"] = "https://example.test/vs.exe";
      parameters["bootstrapperSha256"] = bootstrapperSha256 ?? new string('A', 64);
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
      ["productId"] = instance.ProductId,
      ["productPath"] = instance.ProductPath,
      ["installationVersion"] = instance.InstallationVersion,
      ["edition"] = instance.Edition,
      ["channel"] = instance.ChannelId,
      ["workloads"] = string.Join(';', instance.Workloads),
      ["components"] = string.Join(';', instance.Components)
    }
  };

  private static string Vsconfig(params string[] components) => $$"""
      { "version": "1.0", "components": [{{string.Join(',', components.Select(id => $"\"{id}\""))}}] }
      """;

  private static VisualStudioInstance Instance(
      string id,
      string[]? workloads = null,
      string[]? components = null,
      string version = "18.3.2") => new()
      {
        InstanceId = id,
        InstallationPath = @"C:\VS",
        ProductId = "Microsoft.VisualStudio.Product.Community",
        ProductPath = @"C:\VS\Common7\IDE\devenv.exe",
        ProductDisplayVersion = version,
        InstallationVersion = $"{version}.0",
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
    public int AttemptCount { get; private set; }
    public int CallCount { get; private set; }
    public IReadOnlyList<string> RequestedWorkloads { get; private set; } = [];
    public IReadOnlyList<string> RequestedComponents { get; private set; } = [];
    public Action? BeforeDiscover { get; init; }

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken)
    {
      AttemptCount++;
      BeforeDiscover?.Invoke();
      cancellationToken.ThrowIfCancellationRequested();
      CallCount++;
      RequestedWorkloads = requestedWorkloads;
      RequestedComponents = requestedComponents;
      if (sequences.Count == 0)
      {
        return Task.FromResult<IReadOnlyList<VisualStudioInstance>>([]);
      }

      var result = sequences[Math.Min(_index, sequences.Count - 1)];
      _index++;
      return Task.FromResult(result);
    }
  }

  private sealed class FinalErrorComplianceEvaluator(StructuredError finalError)
      : IComplianceEvaluator
  {
    private readonly ComplianceEvaluator _inner = new();
    private int _calls;

    public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
    {
      if (Interlocked.Increment(ref _calls) == 1)
      {
        return _inner.Evaluate(desired, current);
      }

      return new ComplianceResult(
          ComplianceStatus.ConfigurationMismatch,
          finalError.Summary,
          finalError);
    }
  }

  private sealed class RecordingInstallerClient : IVisualStudioInstallerClient
  {
    public IReadOnlyList<string> LastArguments { get; private set; } = [];
    public List<IReadOnlyList<string>> ArgumentHistory { get; } = [];
    public List<string> Operations { get; } = [];
    public string? LastVsConfigPath { get; private set; }
    public Action<string?>? BeforeModify { get; init; }
    public Action? AfterUpdate { get; init; }
    public VisualStudioInstallerResult Result { get; init; } = Success(@"C:\setup.exe");
    public VisualStudioBootstrapperAcquisition? LastAcquisition { get; private set; }
    public string? LastOperation { get; private set; }
    public string? LastInstallPath { get; private set; }

    public Task<VisualStudioBootstrapperAcquisition> AcquireBootstrapperAsync(
        Uri source,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
      LastAcquisition = new VisualStudioBootstrapperAcquisition(
          new TrustedFileVerificationResult(
              true,
              @"C:\verified\vs-bootstrapper.exe",
              expectedSha256,
              null));
      return Task.FromResult(LastAcquisition);
    }

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
      LastOperation = "install";
      Operations.Add(LastOperation);
      LastInstallPath = installPath;
      LastArguments = VisualStudioInstallerClient.CreateInstallArguments(
          productId, channelUri, installPath, workloads, components, vsconfigPath);
      ArgumentHistory.Add(LastArguments);
      return Task.FromResult(Result);
    }

    public Task<VisualStudioInstallerResult> ModifyAsync(
        string executablePath,
        string installPath,
        IReadOnlyList<string> workloads,
        IReadOnlyList<string> components,
        string? vsconfigPath,
        CancellationToken cancellationToken)
    {
      LastOperation = "modify";
      Operations.Add(LastOperation);
      LastInstallPath = installPath;
      LastVsConfigPath = vsconfigPath;
      BeforeModify?.Invoke(vsconfigPath);
      LastArguments = VisualStudioInstallerClient.CreateModifyArguments(
          installPath, workloads, components, vsconfigPath);
      ArgumentHistory.Add(LastArguments);
      return Task.FromResult(Result);
    }

    public Task<VisualStudioInstallerResult> UpdateAsync(
        string executablePath,
        string installPath,
        CancellationToken cancellationToken)
    {
      LastOperation = "update";
      Operations.Add(LastOperation);
      LastInstallPath = installPath;
      LastArguments = VisualStudioInstallerClient.CreateUpdateArguments(installPath);
      ArgumentHistory.Add(LastArguments);
      AfterUpdate?.Invoke();
      return Task.FromResult(Result);
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

  private sealed class FailingArtifactStager(StructuredError error) : ISecureArtifactStager
  {
    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SecureArtifactStageResult(null, error));

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SecureArtifactStageResult(null, error));
  }

  private sealed class RecordingRealProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];
    public List<byte[]> ExecutableSnapshots { get; } = [];
    public List<byte[]> ConfigurationSnapshots { get; } = [];
    public Queue<int> ExitCodes { get; } = [];

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      ExecutableSnapshots.Add(File.ReadAllBytes(request.FileName));
      var configIndex = request.Arguments.ToList().IndexOf("--config");
      if (configIndex >= 0)
      {
        ConfigurationSnapshots.Add(File.ReadAllBytes(request.Arguments[configIndex + 1]));
      }

      var exitCode = ExitCodes.TryDequeue(out var configuredExitCode)
          ? configuredExitCode
          : 0;
      return Task.FromResult(new ProcessExecutionResult(true, exitCode, [], []));
    }
  }

  private sealed class CountingContentHandler(byte[] content) : HttpMessageHandler
  {
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      RequestCount++;
      return Task.FromResult(new HttpResponseMessage
      {
        StatusCode = System.Net.HttpStatusCode.OK,
        Content = new ByteArrayContent(content)
      });
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
}
