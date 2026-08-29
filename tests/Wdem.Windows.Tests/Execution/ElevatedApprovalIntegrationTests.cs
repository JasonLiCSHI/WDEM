using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Execution;
using Wdem.Windows.Persistence;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Execution;

public sealed class ElevatedApprovalIntegrationTests : IDisposable
{
  private const string Secret = "profile-loader-original-secret";
  private readonly string _directory = Path.Combine(
      Path.GetTempPath(),
      $"wdem-elevated-approval-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_RealProfileSealsOriginalResourceForElevatedWorker()
  {
    Directory.CreateDirectory(_directory);
    var profilePath = Path.Combine(_directory, "elevated.json");
    await File.WriteAllTextAsync(
        profilePath,
        $$"""
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "elevated-profile",
            "version": "1.0.0",
            "displayName": "Elevated profile",
            "description": "Exercises the real approval path.",
            "requiredResources": [ { "id": "admin-resource" } ]
          },
          "resources": {
            "admin-resource": {
              "type": "integration",
              "provider": "sealed",
              "parameters": {
                "password": "{{Secret}}"
              }
            }
          }
        }
        """);
    var provider = new ElevatedRecordingProvider();
    var providers = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var elevatedStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var worker = new ElevatedResourceWorker(elevatedStore, providers, new LogRedactor());
    var broker = new InProcessPrivilegeBroker(store, worker);
    var service = new EnvironmentRunService(
        new DirectoryProfileCatalog(_directory, providers),
        new ResourceGraphBuilder(),
        providers,
        compliance,
        new ExecutionPlanner(providers, compliance),
        new ResourceScheduler(),
        store,
        new PrivilegeAwareResourceApplyDispatcher(
            new DirectResourceApplyDispatcher(),
            broker),
        timeProvider: null,
        new RunEventHub(),
        redactor);

    var run = await service.ApplyAsync(
        new RunRequest(
            profilePath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(Secret, provider.AppliedResource!.Parameters["password"]);
    Assert.NotNull(provider.PlannedResource);
    Assert.NotNull(provider.ApprovedPlan);
    Assert.NotNull(broker.Request);
    Assert.Equal(
        ApprovedResourceFingerprint.Create(provider.PlannedResource, provider.ApprovedPlan),
        broker.Request.PlanFingerprint);
    Assert.DoesNotContain(Secret, broker.PublicSnapshot, StringComparison.Ordinal);
    Assert.Contains("\"password\": \"***\"", broker.PublicSnapshot, StringComparison.Ordinal);
    Assert.DoesNotContain(Secret, broker.ProtectedSnapshot, StringComparison.Ordinal);
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private sealed class InProcessPrivilegeBroker(
      JsonExecutionRunStore store,
      ElevatedResourceWorker worker) : IPrivilegeBroker
  {
    public ElevatedResourceRequest? Request { get; private set; }
    public string PublicSnapshot { get; private set; } = string.Empty;
    public string ProtectedSnapshot { get; private set; } = string.Empty;

    public async Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      Request = request;
      PublicSnapshot = await File.ReadAllTextAsync(
          store.SnapshotPath(request.RunId),
          cancellationToken);
      ProtectedSnapshot = await File.ReadAllTextAsync(
          store.ApprovedResourcesPath(request.RunId),
          cancellationToken);
      return await worker.ApplyAsync(request, progress, cancellationToken);
    }
  }

  private sealed class ElevatedRecordingProvider : IResourceProvider
  {
    public string ResourceType => "integration";
    public string ProviderName => "sealed";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }
    public ResourceDefinition? PlannedResource { get; private set; }
    public ResourceDefinition? AppliedResource { get; private set; }
    public ResourcePlan? ApprovedPlan { get; private set; }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken)
    {
      PlannedResource = resource;
      ApprovedPlan = new ResourcePlan
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
            Id = "admin-resource:apply",
            Description = "Apply the elevated resource.",
            Action = PlanAction.Configure,
            PrivilegeRequirement = PrivilegeRequirement.Administrator,
            RestartPolicy = RestartPolicy.NoRestart
          }
        ]
      };
      return ValueTask.FromResult(ApprovedPlan);
    }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      AppliedResource = resource;
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = plan.Steps.Single().Id,
            Action = plan.Steps.Single().Action,
            Progress = 1
          }
        ]
      });
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }
}
