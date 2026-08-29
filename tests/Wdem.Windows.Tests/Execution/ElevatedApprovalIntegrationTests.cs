using System.IO.Pipes;
using System.Text;
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
    var resourceResult = Assert.Single(run.ResourceResults).Value;
    var stepResult = Assert.Single(resourceResult.StepResults);
    Assert.Equal(3010, stepResult.ProcessExitCode);
    Assert.True(stepResult.ProcessSucceeded);
    Assert.Equal(ExecutionOutcome.Succeeded, stepResult.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, resourceResult.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_RealPipeCleanupFailure_PersistsTerminalRunAndDiagnostic()
  {
    Directory.CreateDirectory(_directory);
    var profilePath = Path.Combine(_directory, "cleanup.json");
    await File.WriteAllTextAsync(
        profilePath,
        $$"""
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "cleanup-profile",
            "version": "1.0.0",
            "displayName": "Cleanup profile",
            "description": "Exercises real pipe cleanup.",
            "requiredResources": [ { "id": "admin-resource" } ]
          },
          "resources": {
            "admin-resource": {
              "type": "integration",
              "provider": "sealed"
            }
          }
        }
        """);
    var provider = new ElevatedRecordingProvider();
    var providers = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    await using var broker = await ClosedPipeCleanupBroker.CreateAsync();
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

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.NotNull(persisted);
    Assert.Equal(ExecutionState.Completed, persisted.State);
    var logs = await store.ReadLogPageAsync(
        run.RunId,
        afterSequence: 0,
        take: 100,
        CancellationToken.None);
    var cleanup = Assert.Single(
        logs,
        entry => entry.Error?.Summary == "Elevated host cleanup failed.");
    Assert.Equal(WdemErrorCode.PermissionError, cleanup.Error!.Code);
    Assert.Equal(
        typeof(ObjectDisposedException).FullName,
        cleanup.Error.UnderlyingExceptionType);
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

  private sealed class ClosedPipeCleanupBroker :
      IPrivilegeBroker,
      IPrivilegeBrokerRunLifecycle,
      IAsyncDisposable
  {
    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;
    private readonly StreamWriter _writer;

    private ClosedPipeCleanupBroker(
        NamedPipeServerStream server,
        NamedPipeClientStream client)
    {
      _server = server;
      _client = client;
      _writer = new StreamWriter(
          server,
          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
          leaveOpen: true);
    }

    public static async Task<ClosedPipeCleanupBroker> CreateAsync()
    {
      var pipeName = $"wdem-cleanup-{Guid.NewGuid():N}";
      var server = new NamedPipeServerStream(
          pipeName,
          PipeDirection.InOut,
          1,
          PipeTransmissionMode.Byte,
          PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
      var client = new NamedPipeClientStream(
          ".",
          pipeName,
          PipeDirection.InOut,
          PipeOptions.Asynchronous);
      try
      {
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;
        return new ClosedPipeCleanupBroker(server, client);
      }
      catch
      {
        server.Dispose();
        client.Dispose();
        throw;
      }
    }

    public Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => Task.FromResult(new ResourceApplyResult
        {
          ResourceId = request.ResourceId,
          Outcome = ApplyOutcome.Succeeded
        });

    public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
      await _writer.WriteAsync("buffered-cleanup");
      _server.Dispose();
      await _writer.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
      try
      {
        await _writer.DisposeAsync();
      }
      catch (Exception exception) when (exception is IOException or ObjectDisposedException)
      {
      }

      _server.Dispose();
      _client.Dispose();
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
        RestartRequirement = RestartPolicy.RestartRecommended,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = plan.Steps.Single().Id,
            Action = plan.Steps.Single().Action,
            Progress = 1,
            ProcessExitCode = 3010,
            Succeeded = true
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
