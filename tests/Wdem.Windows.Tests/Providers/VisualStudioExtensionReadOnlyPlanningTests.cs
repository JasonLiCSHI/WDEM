using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Persistence;
using Wdem.Windows.Providers;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed partial class VisualStudioExtensionProviderTests
{
  [Theory]
  [InlineData(false, PlannedResourceStatus.Ready)]
  [InlineData(true, PlannedResourceStatus.AlreadySatisfied)]
  public async Task ExecutionPlanner_CreateAsync_AcceptsVsixPlanContract(
      bool alreadyInstalled,
      PlannedResourceStatus expectedStatus)
  {
    var manifests = new BoundaryRecordingManifestReader
    {
      InstalledManifests = alreadyInstalled ? [CompatibleManifest()] : []
    };
    var provider = Provider(
        manifests,
        new BoundaryRecordingStager(),
        new ThrowingProcessExecutor());
    var resource = Resource(@"C:\approved\extension.vsix");
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var dependency = PlanningDependencyResource();
    var planner = new ExecutionPlanner(
        new ResourceProviderRegistry([new SatisfiedDependencyProvider(), provider]),
        new ComplianceEvaluator());

    var plan = await planner.CreateAsync(
        PlanningGraph(dependency, resource),
        new Dictionary<string, DetectedState>(StringComparer.OrdinalIgnoreCase)
        {
          [dependency.Id] = new DetectedState
          {
            ResourceId = dependency.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          },
          [resource.Id] = detected
        },
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.True(plan.IsExecutable);
    Assert.Empty(plan.Errors);
    var planned = Assert.Single(
        plan.Resources,
        candidate => candidate.Definition.Id == resource.Id);
    Assert.Equal(expectedStatus, planned.Status);
    Assert.Matches(
        "^[0-9A-F]{64}$",
        Assert.IsType<string>(planned.ResourcePlan.ExecutionPreconditionFingerprint));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task JsonExecutionRunStore_RoundTripsVsixPlanContract(bool alreadyInstalled)
  {
    var manifests = new BoundaryRecordingManifestReader
    {
      InstalledManifests = alreadyInstalled ? [CompatibleManifest()] : []
    };
    var provider = Provider(
        manifests,
        new BoundaryRecordingStager(),
        new ThrowingProcessExecutor());
    var resource = Resource(@"C:\approved\extension.vsix");
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var resourcePlan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    var run = RunWithPlan(resource, resourcePlan);
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"wdem-vsix-plan-store-{Guid.NewGuid():N}");
    var store = new JsonExecutionRunStore(new WdemDataPaths(directory), new LogRedactor());
    try
    {
      await store.CreateAsync(run, CancellationToken.None);

      var restored = Assert.IsType<ExecutionRun>(
          await store.GetAsync(run.RunId, CancellationToken.None));
      var restoredPlan = Assert.Single(restored.Plan!.Resources).ResourcePlan;
      Assert.Equal(
          resourcePlan.ExecutionPreconditionFingerprint,
          restoredPlan.ExecutionPreconditionFingerprint);
      Assert.Matches(
          "^[0-9A-F]{64}$",
          Assert.IsType<string>(restoredPlan.ExecutionPreconditionFingerprint));
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Fact]
  public async Task PlanAsync_MissingLocalSource_RemainsExecutableWithoutReadingOrStagingSource()
  {
    var sourceReader = new BoundaryRecordingManifestReader();
    var stager = new BoundaryRecordingStager();
    var provider = new VisualStudioExtensionProvider(
        new FixedDiscovery(Instance("17.0_a")),
        sourceReader,
        new ThrowingProcessExecutor(),
        new ComplianceEvaluator(),
        stager);
    var resource = Resource(@"C:\does-not-exist\approved.vsix");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.True(plan.IsExecutable);
    Assert.Single(plan.Steps);
    Assert.Equal(0, sourceReader.SourceReadCount);
    Assert.Equal(0, stager.StageCount);
  }

  [Fact]
  public async Task DetectAsync_OnlyReadsInstalledStateAndNeverAcquiresConfiguredSource()
  {
    var manifests = new BoundaryRecordingManifestReader();
    var stager = new BoundaryRecordingStager();
    var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException());
    using var httpClient = new HttpClient(handler);
    var provider = Provider(manifests, stager, new ThrowingProcessExecutor(), httpClient: httpClient);
    var resource = Resource("https://artifacts.example.test/extension.vsix");

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.Equal(0, manifests.SourceReadCount);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, handler.RequestCount);
  }

  [Fact]
  public async Task DetectAsync_InstalledExtension_DoesNotReportAcquisitionHashOrAcquireSource()
  {
    var manifests = new BoundaryRecordingManifestReader
    {
      InstalledManifests = [CompatibleManifest()]
    };
    var stager = new BoundaryRecordingStager();
    var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException());
    using var httpClient = new HttpClient(handler);
    var provider = Provider(manifests, stager, new ThrowingProcessExecutor(), httpClient: httpClient);
    var resource = Resource("https://artifacts.example.test/extension.vsix");

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.True(state.Exists);
    Assert.Null(state.ConfigurationHash);
    Assert.Equal(0, manifests.SourceReadCount);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, handler.RequestCount);
  }

  [Fact]
  public async Task ExpectedSha256Change_LeavesDetectedFactsButChangesPlanFingerprints()
  {
    var manifests = new BoundaryRecordingManifestReader
    {
      InstalledManifests = [CompatibleManifest()]
    };
    var provider = Provider(
        manifests,
        new BoundaryRecordingStager(),
        new ThrowingProcessExecutor());
    var firstResource = Resource(
        "https://artifacts.example.test/extension.vsix",
        new string('A', 64));
    var secondResource = Resource(
        "https://artifacts.example.test/extension.vsix",
        new string('B', 64));

    var firstState = await provider.DetectAsync(firstResource, CancellationToken.None);
    var secondState = await provider.DetectAsync(secondResource, CancellationToken.None);
    var firstPlan = await provider.PlanAsync(firstResource, firstState, CancellationToken.None);
    var secondPlan = await provider.PlanAsync(secondResource, secondState, CancellationToken.None);

    Assert.Equal(firstState.Outcome, secondState.Outcome);
    Assert.Equal(firstState.Exists, secondState.Exists);
    Assert.Equal(firstState.Version, secondState.Version);
    Assert.Equal(firstState.InstalledVersions, secondState.InstalledVersions);
    Assert.Null(firstState.ConfigurationHash);
    Assert.Null(secondState.ConfigurationHash);
    Assert.Equal(firstState.Evidence, secondState.Evidence);
    Assert.NotEqual(firstPlan.DesiredStateFingerprint, secondPlan.DesiredStateFingerprint);
    Assert.NotEqual(
        firstPlan.ExecutionPreconditionFingerprint,
        secondPlan.ExecutionPreconditionFingerprint);
  }

  [Fact]
  public async Task ApplyAsync_SourceManifestReadFails_CleansStagingBeforeReturningFailure()
  {
    var sourceReader = new BoundaryRecordingManifestReader
    {
      SourceReadException = new IOException("invalid source package")
    };
    await using var stager = new SuccessfulRecordingStager();
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioExtensionProvider(
        new FixedDiscovery(Instance("17.0_a")),
        sourceReader,
        process,
        new ComplianceEvaluator(),
        stager);
    var resource = Resource(@"C:\approved\extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(1, stager.StageCount);
    Assert.Equal(1, sourceReader.SourceReadCount);
    Assert.Equal(0, process.ExecuteCount);
    Assert.False(Directory.Exists(stager.DirectoryPath));
  }

  [Fact]
  public async Task PlanAsync_HttpsSource_IsStableSerializableAndHasNoSourceSideEffects()
  {
    var manifests = new BoundaryRecordingManifestReader();
    var stager = new BoundaryRecordingStager();
    var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException());
    using var httpClient = new HttpClient(handler);
    var provider = Provider(manifests, stager, new ThrowingProcessExecutor(), httpClient: httpClient);
    var resource = Resource("https://artifacts.example.test/private/extension.vsix");

    var first = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var second = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var json = JsonSerializer.Serialize(first);
    var roundTripped = JsonSerializer.Deserialize<ResourcePlan>(json);

    Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    Assert.Equal(first.Steps[0].Id, second.Steps[0].Id);
    Assert.NotNull(roundTripped);
    Assert.Equal(json, JsonSerializer.Serialize(roundTripped));
    Assert.Equal(first.Steps[0].Id, roundTripped.Steps[0].Id);
    Assert.Equal(0, handler.RequestCount);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, manifests.SourceReadCount);
    Assert.DoesNotContain("artifacts.example.test", first.Steps[0].Id, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('A', 64), first.Steps[0].Id, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(
        resource.Parameters[VisualStudioExtensionProvider.SourcePathParameter]!,
        json,
        StringComparison.Ordinal);
    Assert.DoesNotContain(
        resource.Parameters[VisualStudioExtensionProvider.ExpectedSha256Parameter]!,
        json,
        StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ApplyAsync_TamperedStep_IsRejectedBeforeSourceAcquisition()
  {
    var stager = new BoundaryRecordingStager();
    var provider = Provider(new BoundaryRecordingManifestReader(), stager, new ThrowingProcessExecutor());
    var resource = Resource(@"C:\approved\extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var step = plan.Steps[0];
    var tampered = plan with
    {
      Steps = [step with { Id = step.Id[..^1] + (step.Id[^1] == '0' ? '1' : '0') }]
    };

    var result = await provider.ApplyAsync(resource, tampered, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, stager.StageCount);
  }

  [Fact]
  public async Task ApplyAsync_TamperedDescription_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var tampered = plan with
    {
      Steps = [plan.Steps[0] with { Description = "Run an unapproved installer command." }]
    };

    var result = await boundary.Provider.ApplyAsync(resource, tampered, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_TamperedReason_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var tampered = plan with
    {
      Steps = [plan.Steps[0] with { Reason = "Unapproved alternate rationale." }]
    };

    var result = await boundary.Provider.ApplyAsync(resource, tampered, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_TamperedComplianceAndAction_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var tampered = plan with
    {
      Compliance = ComplianceStatus.VersionMismatch,
      Steps = [plan.Steps[0] with { Action = PlanAction.Upgrade }]
    };

    var result = await boundary.Provider.ApplyAsync(resource, tampered, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_ExecutablePlanDowngradedToSatisfiedNoOp_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var downgraded = plan with
    {
      Compliance = ComplianceStatus.Satisfied,
      Steps = []
    };

    var result = await boundary.Provider.ApplyAsync(resource, downgraded, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_LegitimateSatisfiedPlan_IsNotRequiredWithoutSourceSideEffects()
  {
    var manifests = new BoundaryRecordingManifestReader
    {
      InstalledManifests = [CompatibleManifest()]
    };
    var handler = new RecordingHttpHandler(request => new HttpResponseMessage(
        HttpStatusCode.ServiceUnavailable)
    {
      RequestMessage = request
    });
    using var httpClient = new HttpClient(handler);
    var stager = new RejectingRecordingStager();
    var process = new RecordingProcessExecutor();
    var provider = Provider(manifests, stager, process, httpClient);
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var currentState = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, currentState, CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ComplianceStatus.Satisfied, plan.Compliance);
    Assert.NotNull(plan.ExecutionPreconditionFingerprint);
    Assert.Empty(plan.Steps);
    Assert.Equal(ApplyOutcome.NotRequired, result.Outcome);
    Assert.Equal(0, handler.RequestCount);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, process.ExecuteCount);
    Assert.Equal(0, manifests.SourceReadCount);
  }

  [Fact]
  public async Task ApplyAsync_InvalidResource_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix") with
    {
      PrivilegeRequirement = PrivilegeRequirement.CurrentUser
    };
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await boundary.Provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_MalformedPlan_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var malformed = plan with { Steps = [plan.Steps[0] with { Id = "not-a-valid-step-id" }] };

    var result = await boundary.Provider.ApplyAsync(resource, malformed, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_DestructivePlan_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var destructive = plan with { Steps = [plan.Steps[0] with { IsDestructive = true }] };

    var result = await boundary.Provider.ApplyAsync(resource, destructive, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_ExtraStep_IsRejectedBeforeSourceAcquisition()
  {
    var boundary = PreAcquisitionBoundary();
    var resource = Resource("https://artifacts.example.test/extension.vsix");
    var plan = await boundary.Provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var extraStep = plan.Steps[0] with { Id = plan.Steps[0].Id[..^1] + "0" };
    var expanded = plan with { Steps = [plan.Steps[0], extraStep] };

    var result = await boundary.Provider.ApplyAsync(resource, expanded, null, CancellationToken.None);

    AssertPreAcquisitionRejection(result, boundary);
  }

  [Fact]
  public async Task ApplyAsync_ResourceDefinitionForgery_IsRejectedBeforeSourceAcquisition()
  {
    var stager = new BoundaryRecordingStager();
    var provider = Provider(new BoundaryRecordingManifestReader(), stager, new ThrowingProcessExecutor());
    var resource = Resource(@"C:\approved\extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var changedParameters = resource.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
    changedParameters[VisualStudioExtensionProvider.SourcePathParameter] = @"C:\other\extension.vsix";
    var changedResource = resource with { Parameters = changedParameters };
    var forged = plan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(changedResource)
    };

    var result = await provider.ApplyAsync(changedResource, forged, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, stager.StageCount);
  }

  [Fact]
  public async Task ApplyAsync_VisualStudioIdentityChanged_IsRejectedBeforeSourceAcquisition()
  {
    var discovery = new MutableDiscovery(Instance("17.0_a"));
    var stager = new BoundaryRecordingStager();
    var provider = Provider(
        new BoundaryRecordingManifestReader(),
        stager,
        new ThrowingProcessExecutor(),
        discovery: discovery);
    var resource = Resource(@"C:\approved\extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    discovery.Instances = [Instance("17.0_a") with { InstallationPath = @"D:\MovedVS" }];

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, stager.StageCount);
  }

  [Fact]
  public async Task ApplyAsync_SourceChangedAfterPlanning_IsReadAgainAndRejectedByApprovedHash()
  {
    var sourcePath = TempSource("approved bytes");
    var expectedHash = Sha256("approved bytes");
    await using var stager = new HashCheckingStager();
    var process = new RecordingProcessExecutor();
    var provider = Provider(new BoundaryRecordingManifestReader(), stager, process);
    var resource = Resource(sourcePath, expectedHash);
    try
    {
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      File.WriteAllText(sourcePath, "changed bytes");

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(1, stager.StageCount);
      Assert.Equal(0, process.ExecuteCount);
      Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task ApplyAsync_MissingLocalSource_FailsBeforeManifestOrProcess()
  {
    var manifests = new BoundaryRecordingManifestReader();
    await using var stager = new HashCheckingStager();
    var process = new RecordingProcessExecutor();
    var provider = Provider(manifests, stager, process);
    var resource = Resource(@"C:\missing\extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, manifests.SourceReadCount);
    Assert.Equal(0, process.ExecuteCount);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ApplyAsync_UnsafeOrOversizedHttpsResponse_FailsBeforeStaging(bool oversized)
  {
    var handler = new RecordingHttpHandler(request =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent([]),
        RequestMessage = oversized
            ? request
            : new HttpRequestMessage(HttpMethod.Get, "http://unsafe.example.test/extension.vsix")
      };
      if (oversized)
      {
        response.Content.Headers.ContentLength = 512L * 1024 * 1024 + 1;
      }

      return response;
    });
    using var httpClient = new HttpClient(handler);
    var stager = new BoundaryRecordingStager();
    var process = new RecordingProcessExecutor();
    var provider = Provider(
        new BoundaryRecordingManifestReader(),
        stager,
        process,
        httpClient: httpClient);
    var resource = Resource("https://safe.example.test/extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(1, handler.RequestCount);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, process.ExecuteCount);
  }

  [Fact]
  public async Task ApplyAsync_HttpsDownloadFailure_FailsBeforeStaging()
  {
    var handler = new RecordingHttpHandler(request => new HttpResponseMessage(
        HttpStatusCode.ServiceUnavailable)
    {
      RequestMessage = request
    });
    using var httpClient = new HttpClient(handler);
    var stager = new BoundaryRecordingStager();
    var process = new RecordingProcessExecutor();
    var provider = Provider(
        new BoundaryRecordingManifestReader(),
        stager,
        process,
        httpClient: httpClient);
    var resource = Resource("https://safe.example.test/extension.vsix");
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DownloadError, result.Error?.Code);
    Assert.Equal(0, stager.StageCount);
    Assert.Equal(0, process.ExecuteCount);
  }

  [Fact]
  public Task ApplyAsync_ManifestWithWrongId_FailsAndCleansBeforeProcess() =>
      AssertManifestRejectedAndCleanedAsync(new VsixManifest(
          "Wrong.Extension",
          "3.2.0",
          "staged",
          "17.0_a",
          [new VsixInstallationTarget("Microsoft.VisualStudio.Community", "[17.0,18.0)")]));

  [Fact]
  public Task ApplyAsync_ManifestWithWrongVersion_FailsAndCleansBeforeProcess() =>
      AssertManifestRejectedAndCleanedAsync(new VsixManifest(
          "Contoso.DeveloperTools",
          "4.0.0",
          "staged",
          "17.0_a",
          [new VsixInstallationTarget("Microsoft.VisualStudio.Community", "[17.0,18.0)")]));

  [Fact]
  public Task ApplyAsync_ManifestWithWrongTarget_FailsAndCleansBeforeProcess() =>
      AssertManifestRejectedAndCleanedAsync(new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "staged",
          "17.0_a",
          [new VsixInstallationTarget("Microsoft.VisualStudio.Enterprise", "[17.0,18.0)")]));

  [Fact]
  public async Task ApplyAsync_InstallerFailureRemainsFailedWhenPostDetectionIsCompliant()
  {
    var sourcePath = TempSource("approved bytes");
    await using var stager = new HashCheckingStager();
    var manifests = new BoundaryRecordingManifestReader
    {
      SourceManifest = CompatibleManifest()
    };
    var process = new RecordingProcessExecutor(
        () => manifests.InstalledManifests = [CompatibleManifest()],
        new ProcessExecutionResult(true, 1, [], []));
    var provider = Provider(manifests, stager, process);
    var resource = Resource(sourcePath, Sha256("approved bytes"));
    try
    {
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(1, result.Error?.ProcessExitCode);
      Assert.Equal(1, Assert.Single(result.StepResults).ProcessExitCode);
      Assert.Equal(1, process.ExecuteCount);
      Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  private static async Task AssertManifestRejectedAndCleanedAsync(VsixManifest sourceManifest)
  {
    var sourcePath = TempSource("approved bytes");
    await using var stager = new HashCheckingStager();
    var manifests = new BoundaryRecordingManifestReader { SourceManifest = sourceManifest };
    var process = new RecordingProcessExecutor();
    var provider = Provider(manifests, stager, process);
    var resource = Resource(sourcePath, Sha256("approved bytes"));
    try
    {
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(0, process.ExecuteCount);
      Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task ApplyAsync_CancelledManifestRead_CleansBeforePropagatingCancellation()
  {
    var sourcePath = TempSource("approved bytes");
    using var cancellation = new CancellationTokenSource();
    await using var stager = new HashCheckingStager(ignoreCancellation: true);
    var manifests = new BoundaryRecordingManifestReader
    {
      BeforeSourceRead = () => cancellation.Cancel(),
      SourceReadExceptionFactory = () => new OperationCanceledException(cancellation.Token)
    };
    var process = new RecordingProcessExecutor();
    var provider = Provider(manifests, stager, process);
    var resource = Resource(sourcePath, Sha256("approved bytes"));
    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    try
    {
      await Assert.ThrowsAsync<OperationCanceledException>(() =>
          provider.ApplyAsync(resource, plan, null, cancellation.Token).AsTask());

      Assert.Equal(0, process.ExecuteCount);
      Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task ApplyAsync_Success_UsesOnlyVerifiedStagingPathAndCleansIt()
  {
    var sourcePath = TempSource("approved bytes");
    await using var stager = new HashCheckingStager();
    var manifests = new BoundaryRecordingManifestReader
    {
      SourceManifest = CompatibleManifest()
    };
    var process = new RecordingProcessExecutor(() => manifests.InstalledManifests = [CompatibleManifest()]);
    var provider = Provider(manifests, stager, process);
    var resource = Resource(sourcePath, Sha256("approved bytes"));
    try
    {
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
      var request = Assert.Single(process.Requests);
      Assert.Equal(
          Path.Combine(Instance("17.0_a").InstallationPath, "Common7", "IDE", "VSIXInstaller.exe"),
          request.FileName);
      Assert.Equal(["/quiet", "/admin", stager.StagedPaths.Single()], request.Arguments);
      Assert.NotEqual(sourcePath, request.Arguments[2]);
      Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
    }
    finally
    {
      File.Delete(sourcePath);
    }
  }

  [Fact]
  public async Task ApplyAsync_HttpsSource_IsDownloadedAndStagedOnlyDuringApply()
  {
    const string content = "approved https bytes";
    var handler = new RecordingHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
      RequestMessage = request
    });
    using var httpClient = new HttpClient(handler);
    await using var stager = new HashCheckingStager();
    var manifests = new BoundaryRecordingManifestReader { SourceManifest = CompatibleManifest() };
    var process = new RecordingProcessExecutor(() => manifests.InstalledManifests = [CompatibleManifest()]);
    var provider = Provider(manifests, stager, process, httpClient);
    var resource = Resource(
        "https://safe.example.test/extension.vsix",
        Sha256(content));

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    Assert.Equal(0, handler.RequestCount);
    Assert.Equal(0, stager.StageCount);

    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(1, handler.RequestCount);
    Assert.Equal(1, stager.StageCount);
    Assert.All(stager.DirectoryPaths, path => Assert.False(Directory.Exists(path)));
  }

  private static VisualStudioExtensionProvider Provider(
      BoundaryRecordingManifestReader manifests,
      ISecureArtifactStager stager,
      IProcessExecutor process,
      HttpClient? httpClient = null,
      IVisualStudioDiscovery? discovery = null) => new(
          discovery ?? new FixedDiscovery(Instance("17.0_a")),
          manifests,
          process,
          new ComplianceEvaluator(),
          stager,
          httpClient);

  private static PreAcquisitionBoundaryFixture PreAcquisitionBoundary()
  {
    var handler = new RecordingHttpHandler(request => new HttpResponseMessage(
        HttpStatusCode.ServiceUnavailable)
    {
      RequestMessage = request
    });
    var httpClient = new HttpClient(handler);
    var stager = new RejectingRecordingStager();
    var process = new RecordingProcessExecutor();
    return new PreAcquisitionBoundaryFixture(
        Provider(new BoundaryRecordingManifestReader(), stager, process, httpClient),
        handler,
        stager,
        process,
        httpClient);
  }

  private static ResourceDefinition PlanningDependencyResource() => new()
  {
    Id = "visual-studio",
    Type = "planner-dependency",
    Provider = "planner-dependency"
  };

  private static ResourceGraph PlanningGraph(
      ResourceDefinition dependency,
      ResourceDefinition resource) => new(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            [dependency.Id] = new(
                dependency,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            [resource.Id] = new(
                resource,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [
            new ResourceGraphLayer(0, [dependency.Id]),
            new ResourceGraphLayer(1, [resource.Id])
          ]);

  private static ExecutionRun RunWithPlan(
      ResourceDefinition resource,
      ResourcePlan resourcePlan)
  {
    var requiresApply = resourcePlan.RequiresApply;
    var planned = new PlannedResource
    {
      Definition = resource,
      Origin = ResourceOrigin.Required,
      Dependencies = resource.Dependencies,
      ResourcePlan = resourcePlan,
      Status = requiresApply
          ? PlannedResourceStatus.Ready
          : PlannedResourceStatus.AlreadySatisfied,
      Risk = requiresApply ? PlanRisk.Elevated : PlanRisk.None,
      RequiresElevation = requiresApply,
      IsDestructive = false,
      RestartPolicy = requiresApply ? resource.RestartPolicy : RestartPolicy.NoRestart,
      Reason = resourcePlan.Steps.FirstOrDefault()?.Reason
    };
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Inspect,
      ProfileSourcePath = @"C:\profiles\developer.yaml",
      ProfileId = "developer",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      StartedAtUtc = DateTimeOffset.UtcNow,
      State = ExecutionState.Ready,
      Machine = new MachineInformation("Windows", "X64", "DEVBOX", "developer"),
      Plan = new ExecutionPlan
      {
        PlanId = Guid.NewGuid(),
        Fingerprint = new string('B', 64),
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        Layers = [new ResourceGraphLayer(0, [resource.Id])],
        Resources = [planned],
        IsExecutable = true
      }
    };
  }

  private static void AssertPreAcquisitionRejection(
      ResourceApplyResult result,
      PreAcquisitionBoundaryFixture boundary)
  {
    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, boundary.Handler.RequestCount);
    Assert.Equal(0, boundary.Stager.StageCount);
    Assert.Equal(0, boundary.Process.ExecuteCount);
    boundary.HttpClient.Dispose();
  }

  private static ResourceDefinition Resource(string sourcePath, string? expectedSha256 = null) =>
      ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          sourcePath,
          expectedSha256);

  private static VsixManifest CompatibleManifest() => new(
      "Contoso.DeveloperTools",
      "3.2.0",
      "staged!/extension.vsixmanifest",
      "17.0_a",
      [new VsixInstallationTarget("Microsoft.VisualStudio.Community", "[17.0,18.0)")]);

  private static string TempSource(string content)
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-vsix-source-{Guid.NewGuid():N}.vsix");
    File.WriteAllText(path, content);
    return path;
  }

  private static string Sha256(string content) =>
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

  private sealed class FixedDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VisualStudioInstance>>(instances);
  }

  private sealed class MutableDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public IReadOnlyList<VisualStudioInstance> Instances { get; set; } = instances;

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) => Task.FromResult(Instances);
  }

  private sealed class BoundaryRecordingManifestReader : IVsixManifestReader
  {
    public int SourceReadCount { get; private set; }
    public Exception? SourceReadException { get; init; }
    public Func<Exception>? SourceReadExceptionFactory { get; init; }
    public Action? BeforeSourceRead { get; init; }
    public VsixManifest? SourceManifest { get; init; }
    public IReadOnlyList<VsixManifest> InstalledManifests { get; set; } = [];

    public Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
        VisualStudioInstance instance,
        CancellationToken cancellationToken) =>
        Task.FromResult(InstalledManifests);

    public Task<VsixManifestReadResult> ReadSourceAsync(
        string path,
        string visualStudioInstanceId,
        CancellationToken cancellationToken)
    {
      SourceReadCount++;
      BeforeSourceRead?.Invoke();
      if (SourceReadException is not null)
      {
        throw SourceReadException;
      }

      if (SourceReadExceptionFactory is not null)
      {
        throw SourceReadExceptionFactory();
      }

      return Task.FromResult(new VsixManifestReadResult(
          SourceManifest,
          SourceManifest is null
              ? new StructuredError(WdemErrorCode.ConfigurationError, "Invalid.", "Invalid.")
              : null));
    }
  }

  private sealed class BoundaryRecordingStager : ISecureArtifactStager
  {
    public int StageCount { get; private set; }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      StageCount++;
      throw new InvalidOperationException("Planning must not stage a source artifact.");
    }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      StageCount++;
      throw new InvalidOperationException("Planning must not stage a source artifact.");
    }
  }

  private sealed class RecordingProcessExecutor(
      Action? onExecute = null,
      ProcessExecutionResult? result = null) : IProcessExecutor
  {
    public int ExecuteCount { get; private set; }
    public List<ProcessExecutionRequest> Requests { get; } = [];

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      ExecuteCount++;
      Requests.Add(request);
      onExecute?.Invoke();
      return Task.FromResult(result ?? new ProcessExecutionResult(true, 0, [], []));
    }
  }

  private sealed class RejectingRecordingStager : ISecureArtifactStager
  {
    public int StageCount { get; private set; }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => Reject();

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => Reject();

    private Task<SecureArtifactStageResult> Reject()
    {
      StageCount++;
      return Task.FromResult(new SecureArtifactStageResult(
          null,
          new StructuredError(WdemErrorCode.ConfigurationError, "Rejected.", "Rejected.")));
    }
  }

  private sealed record PreAcquisitionBoundaryFixture(
      VisualStudioExtensionProvider Provider,
      RecordingHttpHandler Handler,
      RejectingRecordingStager Stager,
      RecordingProcessExecutor Process,
      HttpClient HttpClient);

  private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
      : HttpMessageHandler
  {
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      RequestCount++;
      return Task.FromResult(responseFactory(request));
    }
  }

  private sealed class HashCheckingStager(bool ignoreCancellation = false)
      : ISecureArtifactStager, IAsyncDisposable
  {
    private readonly List<SecureStagedArtifact> _artifacts = [];
    public int StageCount { get; private set; }
    public List<string> DirectoryPaths { get; } = [];
    public List<string> StagedPaths { get; } = [];

    public async Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      StageCount++;
      if (!File.Exists(sourcePath))
      {
        return Failure("The source does not exist.");
      }

      await using var source = File.OpenRead(sourcePath);
      return await StageCore(source, expectedSha256, cancellationToken);
    }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      StageCount++;
      return StageCore(source, expectedSha256, cancellationToken);
    }

    private async Task<SecureArtifactStageResult> StageCore(
        Stream source,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
      if (!ignoreCancellation)
      {
        cancellationToken.ThrowIfCancellationRequested();
      }

      var directory = Path.Combine(Path.GetTempPath(), $"wdem-vsix-stage-{Guid.NewGuid():N}");
      DirectoryPaths.Add(directory);
      Directory.CreateDirectory(directory);
      var path = Path.Combine(directory, "extension.vsix");
      StagedPaths.Add(path);
      await using (var destination = File.Create(path))
      {
        await source.CopyToAsync(destination, ignoreCancellation ? CancellationToken.None : cancellationToken);
      }

      string actual;
      await using (var hashSource = File.OpenRead(path))
      {
        actual = Convert.ToHexString(
            await SHA256.HashDataAsync(hashSource, CancellationToken.None));
      }
      if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
      {
        Directory.Delete(directory, recursive: true);
        return Failure("The SHA-256 did not match.");
      }

      var readLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      var artifact = new SecureStagedArtifact(
          directory,
          path,
          actual,
          readLock,
          ArtifactLease.Create(directory));
      _artifacts.Add(artifact);
      return new SecureArtifactStageResult(artifact, null);
    }

    private static SecureArtifactStageResult Failure(string detail) => new(
        null,
        new StructuredError(WdemErrorCode.ConfigurationError, "Staging failed.", detail));

    public async ValueTask DisposeAsync()
    {
      foreach (var artifact in _artifacts)
      {
        await artifact.DisposeAsync();
      }

      foreach (var directory in DirectoryPaths.Where(Directory.Exists))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  private sealed class SuccessfulRecordingStager : ISecureArtifactStager, IAsyncDisposable
  {
    private SecureStagedArtifact? _artifact;

    public SuccessfulRecordingStager()
    {
      DirectoryPath = Path.Combine(Path.GetTempPath(), $"wdem-vsix-apply-{Guid.NewGuid():N}");
      Directory.CreateDirectory(DirectoryPath);
      StagedPath = Path.Combine(DirectoryPath, "extension.vsix");
      File.WriteAllText(StagedPath, "staged");
    }

    public string DirectoryPath { get; }
    public string StagedPath { get; }
    public int StageCount { get; private set; }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => Stage(expectedSha256);

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => Stage(expectedSha256);

    private Task<SecureArtifactStageResult> Stage(string expectedSha256)
    {
      StageCount++;
      var readLock = new FileStream(StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
      _artifact = new SecureStagedArtifact(
          DirectoryPath,
          StagedPath,
          expectedSha256,
          readLock,
          ArtifactLease.Create(DirectoryPath));
      return Task.FromResult(new SecureArtifactStageResult(_artifact, null));
    }

    public async ValueTask DisposeAsync()
    {
      if (_artifact is not null)
      {
        await _artifact.DisposeAsync();
      }

      if (Directory.Exists(DirectoryPath))
      {
        Directory.Delete(DirectoryPath, recursive: true);
      }
    }
  }
}
