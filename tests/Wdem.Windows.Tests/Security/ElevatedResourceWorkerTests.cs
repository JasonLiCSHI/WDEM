using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class ElevatedResourceWorkerTests
{
  [Fact]
  public async Task ApplyAsync_FingerprintMismatch_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            "admin-resource",
            Mutate(approvedFingerprint)),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_ApprovedSnapshot_ResolvesProviderAndRedactsOutput()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var progress = new RecordingProgress();

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            "admin-resource",
            approvedFingerprint),
        progress,
        CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.DoesNotContain("hunter2", progress.Items.Single().Message, StringComparison.Ordinal);
    Assert.DoesNotContain(
        "hunter2",
        result.StepResults.Single().Message,
        StringComparison.Ordinal);
    Assert.DoesNotContain(
        "hunter2",
        result.Diagnostics.Single().Detail,
        StringComparison.Ordinal);
    Assert.Equal(23, result.StepResults.Single().ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_FailedApprovedProviderPreservesRestartEvidenceThroughRedaction()
  {
    var provider = new RecordingProvider
    {
      Outcome = ApplyOutcome.Failed,
      RestartRequirement = RestartPolicy.RestartRequired
    };
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, "admin-resource", approvedFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, result.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_SuccessfulRestartExitPreservesExplicitSuccessThroughRedaction()
  {
    var provider = new RecordingProvider
    {
      ProcessExitCode = 3010,
      StepSucceeded = true,
      RestartRequirement = RestartPolicy.RestartRecommended
    };
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, "admin-resource", approvedFingerprint),
        null,
        CancellationToken.None);

    var step = Assert.Single(result.StepResults);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.True(step.Succeeded);
    Assert.DoesNotContain("hunter2", step.Message, StringComparison.Ordinal);
    Assert.Equal(RestartPolicy.RestartRecommended, result.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_SealedSnapshot_PassesOriginalResourceValuesToProvider()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out _);
    var persisted = run.Plan!.Resources.Single();
    var original = persisted.Definition with
    {
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = "original-secret"
      }
    };
    var approvedPlan = persisted.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(original)
    };
    var fingerprint = ApprovedResourceFingerprint.Create(original, approvedPlan);
    var redactedRun = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          persisted with
          {
            Definition = original with
            {
              Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
              {
                ["password"] = "***"
              }
            },
            ResourcePlan = approvedPlan
          }
        ]
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(redactedRun),
        new StubApprovedResourceStore(new ApprovedResource(original, approvedPlan, fingerprint)),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            original.Id,
            fingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal("original-secret", provider.LastResource!.Parameters["password"]);
  }

  [Fact]
  public async Task ApplyAsync_CurrentUserSnapshot_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out var approvedFingerprint);
    var planned = approved.Plan!.Resources.Single();
    var currentUserRun = approved with
    {
      Plan = approved.Plan with
      {
        Resources =
        [
          planned with
          {
            RequiresElevation = false,
            ResourcePlan = planned.ResourcePlan with
            {
              Steps = planned.ResourcePlan.Steps
                  .Select(step => step with
                  {
                    PrivilegeRequirement = PrivilegeRequirement.CurrentUser
                  })
                  .ToArray()
            }
          }
        ]
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(currentUserRun),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            currentUserRun.RunId,
            "admin-resource",
            approvedFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_MixedApprovedPlan_ExecutesOnlyRequestedAdministratorSegment()
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out _);
    var planned = approved.Plan!.Resources.Single();
    var mixedPlan = planned.ResourcePlan with
    {
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          Id = "admin-resource:current-user",
          PrivilegeRequirement = PrivilegeRequirement.CurrentUser
        },
        planned.ResourcePlan.Steps.Single()
      ]
    };
    var mixedRun = approved with
    {
      Plan = approved.Plan with
      {
        Resources = [planned with { ResourcePlan = mixedPlan }]
      }
    };
    var administratorPlan = mixedPlan with { Steps = [mixedPlan.Steps[1]] };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(mixedRun),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            mixedRun.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, administratorPlan)),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    var applied = Assert.IsType<ResourcePlan>(provider.LastPlan);
    var step = Assert.Single(applied.Steps);
    Assert.Equal("admin-resource:apply", step.Id);
    Assert.Equal(PrivilegeRequirement.Administrator, step.PrivilegeRequirement);
  }

  [Fact]
  public async Task ApplyAsync_ResourceIsNotPersistedAsRunning_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out var approvedFingerprint);
    var notRunning = approved with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["admin-resource"] = approved.ResourceResults["admin-resource"] with
        {
          State = ExecutionState.Ready
        }
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(notRunning),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            approved.RunId,
            "admin-resource",
            approvedFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_DependencyHasNotSucceeded_RefusesOutOfOrderExecution()
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out var approvedFingerprint);
    var planned = approved.Plan!.Resources.Single();
    var outOfOrder = approved with
    {
      Plan = approved.Plan with
      {
        Resources = [planned with { Dependencies = ["dependency"] }]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["admin-resource"] = approved.ResourceResults["admin-resource"],
        ["dependency"] = new ResourceResult
        {
          ResourceId = "dependency",
          State = ExecutionState.Running
        }
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(outOfOrder),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            approved.RunId,
            "admin-resource",
            approvedFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Theory]
  [InlineData("deleted", "dependency-a")]
  [InlineData("added", "dependency-a,dependency-b,dependency-c")]
  [InlineData("reordered", "dependency-b,dependency-a")]
  public async Task ApplyAsync_PublicDependenciesWereTampered_RefusesWithoutCallingProvider(
      string _,
      string publicDependencies)
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out _);
    var persisted = approved.Plan!.Resources.Single();
    var original = persisted.Definition with
    {
      Dependencies = ["dependency-a", "dependency-b"]
    };
    var approvedPlan = persisted.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(original)
    };
    var fingerprint = ApprovedResourceFingerprint.Create(original, approvedPlan);
    var tampered = approved with
    {
      Plan = approved.Plan with
      {
        Resources =
        [
          persisted with
          {
            Definition = original,
            Dependencies = publicDependencies.Split(','),
            ResourcePlan = approvedPlan
          }
        ]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        [original.Id] = approved.ResourceResults[original.Id],
        ["dependency-a"] = SucceededDependency("dependency-a"),
        ["dependency-b"] = SucceededDependency("dependency-b"),
        ["dependency-c"] = SucceededDependency("dependency-c")
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(tampered),
        new StubApprovedResourceStore(new ApprovedResource(
            original,
            approvedPlan,
            fingerprint)),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            approved.RunId,
            original.Id,
            fingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_SameApprovedRequestIsReplayed_ExecutesProviderOnce()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var request = new ElevatedResourceRequest(
        run.RunId,
        "admin-resource",
        approvedFingerprint);

    var first = await worker.ApplyAsync(request, null, CancellationToken.None);
    var replay = await worker.ApplyAsync(request, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, first.Outcome);
    Assert.Equal(ApplyOutcome.Failed, replay.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, replay.Error!.Code);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_PersistedPlanStepWasTampered_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var approvedRun = ApprovedRun(provider, out var approvedFingerprint);
    var approvedPlan = approvedRun.Plan!.Resources.Single();
    var tampered = approvedRun with
    {
      Plan = approvedRun.Plan with
      {
        Resources =
        [
          approvedPlan with
          {
            ResourcePlan = approvedPlan.ResourcePlan with
            {
              Steps =
              [
                approvedPlan.ResourcePlan.Steps.Single() with
                {
                  Action = PlanAction.Upgrade
                }
              ]
            }
          }
        ]
      }
    };
    var sealedResource = new ApprovedResource(
        approvedPlan.Definition,
        approvedPlan.ResourcePlan,
        approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(tampered),
        new StubApprovedResourceStore(sealedResource),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            approvedRun.RunId,
            "admin-resource",
            approvedFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  public async Task ApplyAsync_ApprovedVsixLocatorFieldWasReplaced_RefusesBeforeProvider(
      int fieldIndex)
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out _);
    var planned = run.Plan!.Resources.Single();
    var locator = "vsix-v2:00112233445566778899AABBCCDDEEFF:" +
        "00112233445566778899aabbccddeeff:" + new string('A', 43);
    var approvedPlan = planned.ResourcePlan with
    {
      Steps = [planned.ResourcePlan.Steps.Single() with { Id = locator }]
    };
    var fingerprint = ApprovedResourceFingerprint.Create(planned.Definition, approvedPlan);
    var fields = locator.Split(':');
    fields[fieldIndex] = (fields[fieldIndex][0] == 'A' ? 'B' : 'A') + fields[fieldIndex][1..];
    var tamperedPlan = approvedPlan with
    {
      Steps = [approvedPlan.Steps.Single() with { Id = string.Join(':', fields) }]
    };
    var tamperedRun = run with
    {
      Plan = run.Plan with
      {
        Resources = [planned with { ResourcePlan = tamperedPlan }]
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(tamperedRun),
        new StubApprovedResourceStore(new ApprovedResource(
            planned.Definition,
            approvedPlan,
            fingerprint)),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, planned.Definition.Id, fingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_NewVsixLocatorRequiresNewProtectedApproval()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out _);
    var planned = run.Plan!.Resources.Single();
    var oldPlan = planned.ResourcePlan with
    {
      Steps = [planned.ResourcePlan.Steps.Single() with { Id = "vsix-v2:old-approved-locator" }]
    };
    var newPlan = oldPlan with
    {
      Steps = [oldPlan.Steps.Single() with { Id = "vsix-v2:new-approved-locator" }]
    };
    var oldFingerprint = ApprovedResourceFingerprint.Create(planned.Definition, oldPlan);
    var newFingerprint = ApprovedResourceFingerprint.Create(planned.Definition, newPlan);
    var newRun = run with
    {
      Plan = run.Plan with
      {
        Resources = [planned with { ResourcePlan = newPlan }]
      }
    };
    var staleApprovalWorker = new ElevatedResourceWorker(
        new StubRunStore(newRun),
        new StubApprovedResourceStore(new ApprovedResource(
            planned.Definition,
            oldPlan,
            oldFingerprint)),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var refused = await staleApprovalWorker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, planned.Definition.Id, newFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, refused.Outcome);
    Assert.Equal(0, provider.ApplyCalls);

    var renewedApprovalWorker = new ElevatedResourceWorker(
        new StubRunStore(newRun),
        new StubApprovedResourceStore(new ApprovedResource(
            planned.Definition,
            newPlan,
            newFingerprint)),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var applied = await renewedApprovalWorker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, planned.Definition.Id, newFingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  private static ExecutionRun ApprovedRun(
      RecordingProvider provider,
      out string fingerprint)
  {
    var definition = new ResourceDefinition
    {
      Id = "admin-resource",
      Type = provider.ResourceType,
      Provider = provider.ProviderName,
      PrivilegeRequirement = PrivilegeRequirement.Administrator
    };
    var desiredFingerprint = ResourceDefinitionFingerprint.Create(definition);
    var resourcePlan = new ResourcePlan
    {
      ResourceId = definition.Id,
      ResourceType = definition.Type,
      ProviderName = definition.Provider,
      DesiredStateFingerprint = desiredFingerprint,
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "admin-resource:apply",
          Description = "Apply elevated resource.",
          Action = PlanAction.Configure,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    fingerprint = ApprovedResourceFingerprint.Create(definition, resourcePlan);
    var planned = new PlannedResource
    {
      Definition = definition,
      Origin = ResourceOrigin.Required,
      Dependencies = [],
      ResourcePlan = resourcePlan,
      Status = PlannedResourceStatus.Ready,
      Risk = PlanRisk.Elevated,
      RequiresElevation = true,
      IsDestructive = false,
      RestartPolicy = RestartPolicy.NoRestart
    };
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Apply,
      ProfileSourcePath = "profile.yaml",
      ProfileId = "test",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(),
      StartedAtUtc = DateTimeOffset.UtcNow,
      State = ExecutionState.Running,
      Machine = new MachineInformation("Windows", "x64", "machine", "user"),
      Plan = new ExecutionPlan
      {
        PlanId = Guid.NewGuid(),
        Fingerprint = new string('B', 64),
        ProfileId = "test",
        ProfileVersion = "1.0.0",
        Layers = [new ResourceGraphLayer(0, [definition.Id])],
        Resources = [planned],
        IsExecutable = true
      },
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        [definition.Id] = new ResourceResult
        {
          ResourceId = definition.Id,
          State = ExecutionState.Running
        }
      }
    };
  }

  private static string Mutate(string fingerprint) =>
      $"{(fingerprint[0] == 'A' ? 'B' : 'A')}{fingerprint[1..]}";

  private static ResourceResult SucceededDependency(string resourceId) => new()
  {
    ResourceId = resourceId,
    State = ExecutionState.Completed,
    Outcome = ExecutionOutcome.Succeeded
  };

  private sealed class StubRunStore(ExecutionRun run) :
      IExecutionRunStore,
      IApprovedResourceStore
  {
    public IReadOnlyList<StructuredError> Diagnostics => [];

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult<ExecutionRun?>(runId == run.RunId ? run : null);

    public Task<ApprovedResource?> GetApprovedResourceAsync(
        Guid runId,
        string resourceId,
        CancellationToken cancellationToken)
    {
      var planned = runId == run.RunId
          ? run.Plan?.Resources.SingleOrDefault(resource => string.Equals(
              resource.Definition.Id,
              resourceId,
              StringComparison.OrdinalIgnoreCase))
          : null;
      return Task.FromResult(planned is null
          ? null
          : new ApprovedResource(
              planned.Definition,
              planned.ResourcePlan,
              ApprovedResourceFingerprint.Create(
                  planned.Definition,
                  planned.ResourcePlan)));
    }

    public Task CreateAsync(ExecutionRun value, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task CreateAsync(
        ExecutionRun value,
        IReadOnlyList<ApprovedResourceSeal> approvedResources,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ExecutionRun> SaveAsync(
        ExecutionRun value,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> TrySaveAsync(
        ExecutionRun value,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class RecordingProvider : IResourceProvider
  {
    public string ResourceType => "test";
    public string ProviderName => "test";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }
    public ResourceDefinition? LastResource { get; private set; }
    public ResourcePlan? LastPlan { get; private set; }
    public ApplyOutcome Outcome { get; init; } = ApplyOutcome.Succeeded;
    public RestartPolicy? RestartRequirement { get; init; }
    public int ProcessExitCode { get; init; } = 23;
    public bool? StepSucceeded { get; init; }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      LastResource = resource;
      LastPlan = plan;
      progress?.Report(new ProviderProgress("apply", 0.5, "password=hunter2"));
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = Outcome,
        RestartRequirement = RestartRequirement,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = plan.Steps.Single().Id,
            Action = PlanAction.Configure,
            Progress = 1,
            ProcessExitCode = ProcessExitCode,
            Succeeded = StepSucceeded,
            Message = "token=hunter2"
          }
        ],
        Diagnostics =
        [
          new StructuredError(
              WdemErrorCode.ProviderError,
              "Provider diagnostic.",
              "secret=hunter2")
        ]
      });
    }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class StubApprovedResourceStore(ApprovedResource approved) :
      IApprovedResourceStore
  {
    public Task<ApprovedResource?> GetApprovedResourceAsync(
        Guid runId,
        string resourceId,
        CancellationToken cancellationToken) => Task.FromResult<ApprovedResource?>(approved);
  }

  private sealed class RecordingProgress : IProgress<ProviderProgress>
  {
    public List<ProviderProgress> Items { get; } = [];
    public void Report(ProviderProgress value) => Items.Add(value);
  }
}
