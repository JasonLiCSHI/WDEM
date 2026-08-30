using System.Diagnostics;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests.Execution;

public sealed class EnvironmentRunServiceTests
{
  [Fact]
  public void RunRequest_DoesNotExposeApprovalProvenance()
  {
    Assert.Null(typeof(RunRequest).GetProperty("ApprovalSource"));
    Assert.Null(typeof(RunRequest).GetProperty("ApprovedPlanFingerprint"));
    Assert.Null(typeof(RunRequest).GetProperty("RetriedFromRunId"));
  }

  [Fact]
  public void GeneralRunService_DoesNotExposeHostSpecificApplyRoutes()
  {
    var methods = typeof(IEnvironmentRunService).GetMethods();

    Assert.DoesNotContain(methods, method => method.Name == "ApplyFromCommandLineAsync");
    Assert.DoesNotContain(methods, method => method.Name == "ApplyReviewedPlanAsync");
  }

  [Fact]
  public void HostApplyCapabilities_AreSiblingInterfacesWithDistinctContracts()
  {
    Assert.False(typeof(ICommandLineEnvironmentRunService).IsAssignableFrom(
        typeof(IReviewedPlanEnvironmentRunService)));
    Assert.False(typeof(IReviewedPlanEnvironmentRunService).IsAssignableFrom(
        typeof(ICommandLineEnvironmentRunService)));
    Assert.Contains(
        typeof(ICommandLineEnvironmentRunService).GetMethods(),
        method => method.Name == "ApplyAsync" && method.GetParameters().Length == 2);
    Assert.Contains(
        typeof(IReviewedPlanEnvironmentRunService).GetMethods(),
        method => method.Name == "ApplyAsync" && method.GetParameters().Length == 3);
  }

  [Fact]
  public async Task ApplyAsync_ChangedReviewedPlanReturnsNonExecutableRunWithoutApplying()
  {
    var provider = new ScriptedProvider(
        Missing("git"),
        Satisfied("git", "2.52.1"));
    var (service, _) = CreateService(provider);
    var inspection = await service.InspectAsync(Request(), CancellationToken.None);
    var run = await ((IReviewedPlanEnvironmentRunService)service).ApplyAsync(
        Request(),
        inspection.Plan!.Fingerprint,
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.False(run.Plan!.IsExecutable);
    var error = Assert.Single(run.Plan.Errors);
    Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
    Assert.Contains("changed", error.Summary, StringComparison.OrdinalIgnoreCase);
    Assert.Null(run.PlanApproval);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_MatchingReviewedPlanAppliesExactlyOnce()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);
    var inspection = await service.InspectAsync(Request(), CancellationToken.None);
    var run = await ((IReviewedPlanEnvironmentRunService)service).ApplyAsync(
        Request(),
        inspection.Plan!.Fingerprint,
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_AcquisitionOnlyHash_DoesNotInvalidateSatisfiedVerification()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      Capabilities = new ProviderCapabilities
      {
        AcquisitionOnlyParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
          "expectedSha256"
        }
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Satisfied,
        DetectedState = Satisfied("git", "2.52.1")
      }
    };
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with
        {
          Parameters = new Dictionary<string, string?>
          {
            ["expectedSha256"] = new string('A', 64)
          }
        }
      }
    };
    var (service, _) = CreateService(provider, profile: profile);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var result = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, result.FinalCompliance);
    Assert.Null(result.DetectedAfter!.ConfigurationHash);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_RecordsReviewedInitialPlanApproval()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);
    var inspection = await service.InspectAsync(Request(), CancellationToken.None);

    var run = await ((IReviewedPlanEnvironmentRunService)service).ApplyAsync(
        Request(),
        inspection.Plan!.Fingerprint,
        CancellationToken.None);

    var approval = Assert.IsType<PlanApproval>(run.PlanApproval);
    Assert.Equal(inspection.Plan.Fingerprint, approval.InitialPlanFingerprint);
    Assert.Equal(PlanApprovalSource.DesktopReviewedPlan, approval.Source);
    Assert.NotEqual(default, approval.ConfirmedAtUtc);
    Assert.Empty(approval.DeferredAuthorizations);
  }

  [Fact]
  public async Task ApplyAsync_RecordsCommandLinePlanApproval()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);

    var run = await ((ICommandLineEnvironmentRunService)service).ApplyAsync(
        Request(),
        CancellationToken.None);

    var approval = Assert.IsType<PlanApproval>(run.PlanApproval);
    Assert.Equal(run.Plan!.Fingerprint, approval.InitialPlanFingerprint);
    Assert.Equal(PlanApprovalSource.CommandLine, approval.Source);
  }

  [Fact]
  public async Task ApplyAsync_RecordsExplicitApplyRequestApproval()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(
        PlanApprovalSource.ExplicitApplyRequest,
        Assert.IsType<PlanApproval>(run.PlanApproval).Source);
  }

  [Fact]
  public async Task ApplyAsync_ReplansDeferredResourceAfterDependencySucceeds()
  {
    var runtimeInstalled = false;
    var store = new InMemoryRunStore();
    var persistedBeforeDependentApply = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent" && !runtimeInstalled
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          runtimeInstalled = true;
        }
        else
        {
          persistedBeforeDependentApply = store.SavedSnapshots.Last().Plan!.Resources
              .Single(item => item.Definition.Id == "dependent").Status ==
              PlannedResourceStatus.Ready;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        store: store);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(PlannedResourceStatus.Ready, run.Plan!.Resources[1].Status);
    Assert.Equal(2, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["dependent"].Outcome);
    Assert.Equal(3, provider.DetectCalls);
    Assert.True(persistedBeforeDependentApply);
    var initialApproval = Assert.IsType<PlanApproval>(store.SavedSnapshots[0].PlanApproval);
    Assert.Same(initialApproval, run.PlanApproval);
    var proof = Assert.Single(initialApproval.DeferredAuthorizations);
    Assert.Equal("dependent", proof.ResourceId);
    Assert.Equal(["git"], proof.Dependencies);
    Assert.Equal([PlanAction.Install], proof.AllowedActions);
    Assert.Equal(PrivilegeRequirement.CurrentUser, proof.MaximumPrivilege);
    Assert.Equal(RestartPolicy.NoRestart, proof.MaximumRestartPolicy);
    Assert.Equal(PlanRisk.Standard, proof.MaximumRisk);
    Assert.False(proof.AllowDestructive);
  }

  [Theory]
  [InlineData(PlanAction.Configure, PrivilegeRequirement.CurrentUser)]
  [InlineData(PlanAction.Install, PrivilegeRequirement.Administrator)]
  public async Task ApplyAsync_RejectsDeferredPlanOutsideApprovedActionOrPrivilege(
      PlanAction freshAction,
      PrivilegeRequirement freshPrivilege)
  {
    var runtimeInstalled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent" && !runtimeInstalled
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state) with
          {
            Steps = state.Exists
                ? []
                :
                [
                  new PlanStep
                  {
                    Id = "fresh-action",
                    Description = "Fresh deferred action",
                    Action = freshAction,
                    PrivilegeRequirement = freshPrivilege,
                    RestartPolicy = RestartPolicy.NoRestart
                  }
                ]
          },
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          runtimeInstalled = true;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, store) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(PlannedResourceStatus.Deferred, run.Plan!.Resources[1].Status);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        store.SavedSnapshots.Last().Plan!.Resources[1].Status);
    var failure = run.ResourceResults["dependent"];
    Assert.Equal(WdemErrorCode.ConfigurationError, failure.Error!.Code);
    Assert.Equal(ComplianceStatus.Missing, failure.FinalCompliance);
  }

  [Theory]
  [InlineData("privilege")]
  [InlineData("restart")]
  [InlineData("destructive")]
  public async Task ApplyAsync_RejectsUnsafeNoneStepInDeferredRefinement(string unsafeField)
  {
    var runtimeInstalled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id != "dependent"
          ? ExecutablePlan(resource, state)
          : !runtimeInstalled
              ? DependencyUnavailablePlan(resource)
              : ExecutablePlan(resource, state) with
              {
                Steps = state.Exists
                ? []
                :
                [
                  new PlanStep
                  {
                    Id = "install-dependent",
                    Description = "Install dependent",
                    Action = PlanAction.Install,
                    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
                    RestartPolicy = RestartPolicy.NoRestart
                  },
                  new PlanStep
                  {
                    Id = "unsafe-declaration",
                    Description = "Unsafe declaration",
                    Action = PlanAction.None,
                    PrivilegeRequirement = unsafeField == "privilege"
                        ? PrivilegeRequirement.Administrator
                        : PrivilegeRequirement.CurrentUser,
                    RestartPolicy = unsafeField == "restart"
                        ? RestartPolicy.RestartRequired
                        : RestartPolicy.NoRestart,
                    IsDestructive = unsafeField == "destructive"
                  }
                ]
              },
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          runtimeInstalled = true;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, store) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, failure.Outcome);
    Assert.Equal(WdemErrorCode.ConfigurationError, failure.Error!.Code);
    Assert.Equal(PlannedResourceStatus.Deferred, run.Plan!.Resources[1].Status);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        store.SavedSnapshots.Last().Plan!.Resources[1].Status);
  }

  [Fact]
  public async Task ApplyAsync_SealFailureRestoresDeferredAuthorizationBeforeCompleting()
  {
    var runtimeInstalled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent" && !runtimeInstalled
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          runtimeInstalled = true;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var store = new InMemoryRunStore
    {
      SealOperation = (_, _, _) => throw new IOException("seal unavailable")
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(
            includeDependentResource: true,
            dependentPrivilege: PrivilegeRequirement.Administrator),
        store: store);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(PlannedResourceStatus.Deferred, run.Plan!.Resources[1].Status);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        store.SavedSnapshots.Last().Plan!.Resources[1].Status);
    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["git"].Outcome);
    var failure = run.ResourceResults["dependent"].Error!;
    Assert.Equal("Resource execution failed.", failure.Summary);
    Assert.Equal(typeof(IOException).FullName, failure.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_DeferredDependencyStillUnavailablePreservesFreshCompliance()
  {
    var runtimeInstalled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, _) => resource.Id == "dependent"
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, Missing(resource.Id)),
      ApplyForResourceOperation = (resource, _) =>
      {
        runtimeInstalled = resource.Id == "git";
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, failure.Outcome);
    Assert.Equal(ComplianceStatus.Missing, failure.FinalCompliance);
    Assert.Equal(WdemErrorCode.DependencyError, failure.Error!.Code);
    Assert.Equal("Dependency is not installed yet.", failure.Error.Summary);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_ChangedDeferredDefinitionPreservesPriorCompliance()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent"
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state)
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var planner = new TransformingPlanner(
        new ExecutionPlanner(registry, compliance),
        plan => plan with
        {
          Resources = plan.Resources.Select(resource =>
              resource.Definition.Id == "dependent"
                  ? resource with
                  {
                    ResourcePlan = resource.ResourcePlan with
                    {
                      DesiredStateFingerprint = new string('A', 64)
                    }
                  }
                  : resource).ToArray()
        });
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        planner: planner);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, failure.Outcome);
    Assert.Equal(ComplianceStatus.Missing, failure.FinalCompliance);
    Assert.Equal(
        "The deferred resource definition changed after plan approval.",
        failure.Error!.Summary);
  }

  [Fact]
  public async Task ApplyAsync_DeferredDetectionFailureIsPlanningFailureWithDetectionFailedCompliance()
  {
    var runtimeInstalled = false;
    var dependentDetections = 0;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource =>
      {
        if (resource.Id == "dependent" && ++dependentDetections > 1)
        {
          throw new IOException("fresh detect unavailable");
        }

        return resource.Id == "git" && runtimeInstalled
            ? Satisfied(resource.Id, "2.52.1")
            : Missing(resource.Id);
      },
      PlanOperation = (resource, state) => resource.Id == "dependent"
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        runtimeInstalled = resource.Id == "git";
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ComplianceStatus.DetectionFailed, failure.FinalCompliance);
    Assert.Equal("Deferred resource planning failed.", failure.Error!.Summary);
    Assert.Equal(typeof(IOException).FullName, failure.Error.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_DeferredPlannerFailurePreservesFreshCompliance()
  {
    var runtimeInstalled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && runtimeInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent"
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        runtimeInstalled = resource.Id == "git";
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var planner = new ThrowingReplanPlanner(
        new ExecutionPlanner(registry, compliance),
        new IOException("fresh plan unavailable"));
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        planner: planner);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ComplianceStatus.Missing, failure.FinalCompliance);
    Assert.Equal("Deferred resource planning failed.", failure.Error!.Summary);
    Assert.Equal(typeof(IOException).FullName, failure.Error.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_DeferredComplianceFailurePreservesFreshDetectionEvidence()
  {
    var runtimeInstalled = false;
    var dependentDetections = 0;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource =>
      {
        if (resource.Id == "dependent" && ++dependentDetections > 1)
        {
          return Missing(resource.Id) with
          {
            Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
              ["phase"] = "fresh-deferred-detection"
            }
          };
        }

        return resource.Id == "git" && runtimeInstalled
            ? Satisfied(resource.Id, "2.52.1")
            : Missing(resource.Id);
      },
      PlanOperation = (resource, state) => resource.Id == "dependent"
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        runtimeInstalled = resource.Id == "git";
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        complianceEvaluator: new ThrowingFreshComplianceEvaluator());

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, failure.Outcome);
    Assert.Equal(ComplianceStatus.DetectionFailed, failure.FinalCompliance);
    Assert.Equal("fresh-deferred-detection", failure.DetectedBefore!.Evidence["phase"]);
    Assert.Equal("Deferred resource planning failed.", failure.Error!.Summary);
    Assert.Equal(typeof(IOException).FullName, failure.Error.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_DeferredPlanPersistenceFailureIsNotReportedAsPlanningFailure()
  {
    var runtimeInstalled = false;
    var provider = DeferredProvider(() => runtimeInstalled, () => runtimeInstalled = true);
    var store = new InMemoryRunStore
    {
      SaveOperation = (snapshot, _) => snapshot.Plan!.Resources.Any(resource =>
          resource.Definition.Id == "dependent" &&
          resource.Status == PlannedResourceStatus.Ready)
              ? Task.FromException(new IOException("plan snapshot unavailable"))
              : Task.CompletedTask
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        store: store);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"].Error!;
    Assert.Equal("Resource execution failed.", failure.Summary);
    Assert.Equal(typeof(IOException).FullName, failure.UnderlyingExceptionType);
    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["git"].Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_CancellationBetweenDeferredPlanSaveAndSealRollsBackWithoutApplying()
  {
    var runtimeInstalled = false;
    using var cancellation = new CancellationTokenSource();
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        dependentPrivilege: PrivilegeRequirement.Administrator);
    var store = new InMemoryRunStore
    {
      SaveOperation = (snapshot, _) =>
      {
        if (snapshot.Plan!.Resources.Any(resource =>
            resource.Definition.Id == "dependent" &&
            resource.Status == PlannedResourceStatus.Ready))
        {
          cancellation.Cancel();
        }

        return Task.CompletedTask;
      },
      SealOperation = (_, _, token) =>
      {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
      }
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(
            includeDependentResource: true,
            dependentPrivilege: PrivilegeRequirement.Administrator),
        store: store);

    var run = await service.ApplyAsync(Request(), cancellation.Token);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["dependent"].Outcome);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        store.SavedSnapshots.Last().Plan!.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status);
  }

  [Fact]
  public async Task ApplyAsync_DeferredSealRollbackCannotOutliveCancellationDrainDeadline()
  {
    var drainBudget = TimeSpan.FromMilliseconds(200);
    var runtimeInstalled = false;
    var sealAttempted = false;
    using var cancellation = new CancellationTokenSource();
    var rollbackElapsed = new TaskCompletionSource<TimeSpan>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        dependentPrivilege: PrivilegeRequirement.Administrator);
    var store = new InMemoryRunStore
    {
      SealOperation = async (_, _, _) =>
      {
        sealAttempted = true;
        await Task.Delay(TimeSpan.FromMilliseconds(140));
        throw new IOException("seal unavailable");
      },
      SaveOperation = async (snapshot, token) =>
      {
        var dependent = snapshot.Plan!.Resources.Single(resource =>
            resource.Definition.Id == "dependent");
        if (!sealAttempted && dependent.Status == PlannedResourceStatus.Ready)
        {
          cancellation.Cancel();
        }

        if (sealAttempted && dependent.Status == PlannedResourceStatus.Deferred)
        {
          var started = Stopwatch.GetTimestamp();
          try
          {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
          }
          catch (OperationCanceledException)
          {
            rollbackElapsed.TrySetResult(Stopwatch.GetElapsedTime(started));
            throw;
          }
        }
      }
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(
            includeDependentResource: true,
            dependentPrivilege: PrivilegeRequirement.Administrator),
        store: store,
        scheduler: new ResourceScheduler(drainBudget),
        persistenceTimeout: TimeSpan.FromMilliseconds(500));

    var returned = await service.ApplyAsync(Request(), cancellation.Token);
    var finalized = await ((IEnvironmentRunFinalizationService)service)
        .WaitForRunFinalizationAsync(returned.RunId, CancellationToken.None);
    var elapsed = await rollbackElapsed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var failure = finalized.ResourceResults["dependent"].Error!;

    Assert.Equal(typeof(AggregateException).FullName, failure.UnderlyingExceptionType);
    Assert.Contains("seal unavailable", failure.UnderlyingExceptionMessage);
    Assert.Contains("canceled", failure.UnderlyingExceptionMessage, StringComparison.OrdinalIgnoreCase);
    Assert.True(
        elapsed < TimeSpan.FromMilliseconds(300),
        $"Rollback remained active for {elapsed.TotalMilliseconds:F0} ms.");
  }

  [Fact]
  public async Task ApplyAsync_ReplansDeferredResourcesWithSharedDependencyConcurrently()
  {
    var dependencyInstalled = false;
    var freshDetections = 0;
    var bothFreshDetectionsStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFreshDetections = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      Capabilities = new ProviderCapabilities { MaxConcurrentOperations = 3 },
      DetectOperation = async (resource, token) =>
      {
        if (resource.Id.StartsWith("dependent", StringComparison.Ordinal) &&
            dependencyInstalled)
        {
          if (Interlocked.Increment(ref freshDetections) == 2)
          {
            bothFreshDetectionsStarted.TrySetResult();
          }

          await releaseFreshDetections.Task.WaitAsync(token);
        }

        return resource.Id == "git" && dependencyInstalled
            ? Satisfied(resource.Id, "2.52.1")
            : Missing(resource.Id);
      },
      PlanOperation = (resource, state) =>
          resource.Id.StartsWith("dependent", StringComparison.Ordinal) &&
          !dependencyInstalled
              ? DependencyUnavailablePlan(resource)
              : ExecutablePlan(resource, state),
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          dependencyInstalled = true;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var profile = Profile(includeDependentResource: true);
    var resources = profile.Resources.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.OrdinalIgnoreCase);
    resources.Add("dependent-two", resources["dependent"] with { Id = "dependent-two" });
    profile = profile with
    {
      RequiredResources = profile.RequiredResources
          .Append(new ProfileResourceReference { Id = "dependent-two" })
          .ToArray(),
      Resources = resources
    };
    var (service, _) = CreateService(provider, profile: profile);

    var apply = service.ApplyAsync(Request(), CancellationToken.None);
    try
    {
      await bothFreshDetectionsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    finally
    {
      releaseFreshDetections.TrySetResult();
    }

    var run = await apply;
    Assert.Equal(2, freshDetections);
    Assert.Equal(3, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["dependent"].Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["dependent-two"].Outcome);
  }

  [Fact]
  public async Task ApplyAsync_SealAndRollbackFailuresPreserveBothExceptions()
  {
    var runtimeInstalled = false;
    var sealAttempted = false;
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        dependentPrivilege: PrivilegeRequirement.Administrator);
    var store = new InMemoryRunStore
    {
      SealOperation = (_, _, _) =>
      {
        sealAttempted = true;
        throw new IOException("seal unavailable");
      },
      SaveOperation = (snapshot, _) => sealAttempted && snapshot.Plan!.Resources.Any(resource =>
          resource.Definition.Id == "dependent" &&
          resource.Status == PlannedResourceStatus.Deferred)
              ? Task.FromException(new UnauthorizedAccessException("rollback unavailable"))
              : Task.CompletedTask
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(
            includeDependentResource: true,
            dependentPrivilege: PrivilegeRequirement.Administrator),
        store: store);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var failure = run.ResourceResults["dependent"].Error!;
    Assert.Equal("Resource execution failed.", failure.Summary);
    Assert.Equal(typeof(AggregateException).FullName, failure.UnderlyingExceptionType);
    Assert.Contains("seal unavailable", failure.UnderlyingExceptionMessage);
    Assert.Contains("rollback unavailable", failure.UnderlyingExceptionMessage);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_DeferredProgressPersistenceFailureFailsFastWithAppliedEvidence()
  {
    var runtimeInstalled = false;
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        progressEventsForResource: resource => resource.Id == "dependent"
            ? [new ProviderProgress("install-dependent", 0.5, "dependent progress")]
            : [],
        applyOperation: (resource, _) =>
        {
          if (resource.Id == "git")
          {
            runtimeInstalled = true;
          }

          return ValueTask.FromResult(AppliedWithStepEvidence(resource));
        });
    var store = new InMemoryRunStore
    {
      AppendLogOperation = (_, entry, _) =>
          entry.ResourceId == "dependent" && entry.Message == "dependent progress"
              ? Task.FromException(new IOException("progress persistence unavailable"))
              : Task.CompletedTask
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        store: store);

    var error = await Assert.ThrowsAsync<IOException>(() =>
        service.ApplyAsync(Request(), CancellationToken.None));

    Assert.Equal("progress persistence unavailable", error.Message);
    var persisted = Assert.IsType<ExecutionRun>(store.SavedSnapshots.Last());
    Assert.Equal(ExecutionOutcome.Succeeded, persisted.ResourceResults["git"].Outcome);
    var evidence = persisted.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, evidence.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, Assert.Single(evidence.StepResults).Outcome);
    Assert.Equal(2, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_ProgressPersistenceFailureCancelsLongRunningApplyAndPreservesEvidence()
  {
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var applyObservedCancellation = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyWithProgressOperation = async (progress, token) =>
      {
        progress!.Report(new ProviderProgress("install", 0.5, "durability failure"));
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, token);
        if (await Task.WhenAny(cancellation, releaseApply.Task) == cancellation)
        {
          applyObservedCancellation.TrySetResult();
          await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellation);
        }

        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 1,
              ProcessExitCode = 0,
              Succeeded = true
            }
          ]
        };
      }
    };
    var store = new InMemoryRunStore
    {
      AppendLogOperation = (_, entry, _) => entry.Message == "durability failure"
          ? Task.FromException(new IOException("progress persistence unavailable"))
          : Task.CompletedTask
    };
    var (service, _) = CreateService(
        provider,
        store: store,
        scheduler: new ResourceScheduler(TimeSpan.FromMilliseconds(250)));
    var apply = service.ApplyAsync(Request(), CancellationToken.None);
    Exception? observed;
    try
    {
      observed = await Record.ExceptionAsync(
          () => apply.WaitAsync(TimeSpan.FromSeconds(1)));
    }
    finally
    {
      releaseApply.TrySetResult();
      await Record.ExceptionAsync(() => apply);
    }

    var error = Assert.IsType<IOException>(observed);
    Assert.Equal("progress persistence unavailable", error.Message);
    Assert.True(applyObservedCancellation.Task.IsCompleted);
    var persisted = Assert.IsType<ExecutionRun>(store.SavedSnapshots.Last());
    var evidence = persisted.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, evidence.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, Assert.Single(evidence.StepResults).Outcome);
  }

  [Fact]
  public async Task ApplyAsync_InternalCancellationBoundsCleanupAndPreservesOriginalFailure()
  {
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyWithProgressOperation = async (progress, token) =>
      {
        progress!.Report(new ProviderProgress("install", 0.5, "durability failure"));
        await Task.WhenAny(
            Task.Delay(Timeout.InfiniteTimeSpan, token),
            releaseApply.Task);
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded
        };
      }
    };
    var store = new InMemoryRunStore
    {
      AppendLogOperation = (_, entry, _) => entry.Message == "durability failure"
          ? Task.FromException(new IOException("progress persistence unavailable"))
          : Task.CompletedTask
    };
    var dispatcher = new BlockingCleanupDispatcher();
    var (service, _) = CreateService(
        provider,
        store: store,
        dispatcher: dispatcher,
        scheduler: new ResourceScheduler(TimeSpan.FromMilliseconds(100)));
    var apply = service.ApplyAsync(Request(), CancellationToken.None);
    Exception? observed;
    try
    {
      observed = await Record.ExceptionAsync(
          () => apply.WaitAsync(TimeSpan.FromMilliseconds(500)));
    }
    finally
    {
      releaseApply.TrySetResult();
      dispatcher.ReleaseCleanup.TrySetResult();
      await Record.ExceptionAsync(() => apply);
    }

    var error = Assert.IsType<IOException>(observed);
    Assert.Equal("progress persistence unavailable", error.Message);
    Assert.True(dispatcher.CleanupToken.CanBeCanceled);
  }

  [Fact]
  public async Task ApplyAsync_RequiredProgressDeliveryFailureCancelsLongRunningApply()
  {
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var applyObservedCancellation = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyWithProgressOperation = async (progress, token) =>
      {
        progress!.Report(new ProviderProgress("install", 0.5, "required failure"));
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, token);
        if (await Task.WhenAny(cancellation, releaseApply.Task) == cancellation)
        {
          applyObservedCancellation.TrySetResult();
          await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellation);
        }

        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded
        };
      }
    };
    using var events = new RunEventHub();
    using var subscription = events.SubscribeRequired((runEvent, _) =>
        runEvent.Message == "required failure"
            ? Task.FromException(new IOException("required observer unavailable"))
            : Task.CompletedTask);
    var (service, _) = CreateService(
        provider,
        eventSink: events,
        scheduler: new ResourceScheduler(TimeSpan.FromMilliseconds(250)));
    var apply = service.ApplyAsync(Request(), CancellationToken.None);
    Exception? observed;
    try
    {
      observed = await Record.ExceptionAsync(
          () => apply.WaitAsync(TimeSpan.FromSeconds(1)));
    }
    finally
    {
      releaseApply.TrySetResult();
      await Record.ExceptionAsync(() => apply);
    }

    var error = Assert.IsType<RequiredRunEventDeliveryException>(observed);
    Assert.Equal("required observer unavailable", error.Cause.Message);
    Assert.True(applyObservedCancellation.Task.IsCompleted);
  }

  [Fact]
  public async Task ApplyAsync_DeferredRequiredEventFailureFailsFastWithAppliedEvidence()
  {
    var runtimeInstalled = false;
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        applyOperation: (resource, _) =>
        {
          if (resource.Id == "git")
          {
            runtimeInstalled = true;
          }

          return ValueTask.FromResult(AppliedWithStepEvidence(resource));
        });
    var store = new InMemoryRunStore();
    using var events = new RunEventHub();
    using var subscription = events.SubscribeRequired((runEvent, _) =>
        runEvent.ResourceId == "dependent" && runEvent.Kind == RunEventKind.StepProgress
            ? Task.FromException(new IOException("required observer unavailable"))
            : Task.CompletedTask);
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        store: store,
        eventSink: events);

    var error = await Assert.ThrowsAsync<RequiredRunEventDeliveryException>(() =>
        service.ApplyAsync(Request(), CancellationToken.None));

    Assert.Equal("required observer unavailable", error.Cause.Message);
    var persisted = Assert.IsType<ExecutionRun>(store.SavedSnapshots.Last());
    Assert.Equal(ExecutionOutcome.Succeeded, persisted.ResourceResults["git"].Outcome);
    var evidence = persisted.ResourceResults["dependent"];
    Assert.Equal(ExecutionOutcome.Failed, evidence.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, Assert.Single(evidence.StepResults).Outcome);
    Assert.Equal(2, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_AppliedEvidenceSaveFailureTakesPriorityOverEventFailure()
  {
    var runtimeInstalled = false;
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        progressEventsForResource: resource => resource.Id == "dependent"
            ? [new ProviderProgress("install-dependent", 0.5, "dependent progress")]
            : [],
        applyOperation: (resource, _) =>
        {
          if (resource.Id == "git")
          {
            runtimeInstalled = true;
          }

          return ValueTask.FromResult(AppliedWithStepEvidence(resource));
        });
    var store = new InMemoryRunStore
    {
      AppendLogOperation = (_, entry, _) =>
          entry.ResourceId == "dependent" && entry.Message == "dependent progress"
              ? Task.FromException(new IOException("progress persistence unavailable"))
              : Task.CompletedTask,
      SaveOperation = (snapshot, _) =>
          snapshot.ResourceResults.TryGetValue("dependent", out var result) &&
          result.Outcome == ExecutionOutcome.Failed &&
          result.StepResults.Count > 0
              ? Task.FromException(
                  new UnauthorizedAccessException("evidence snapshot unavailable"))
              : Task.CompletedTask
    };
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        store: store);

    var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        service.ApplyAsync(Request(), CancellationToken.None));

    Assert.Equal("evidence snapshot unavailable", error.Message);
    Assert.Equal(2, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_LateCancellationDuringDeferredEvidencePublicationIsCancellation()
  {
    var runtimeInstalled = false;
    using var cancellation = new CancellationTokenSource();
    var provider = DeferredProvider(
        () => runtimeInstalled,
        () => runtimeInstalled = true,
        applyOperation: (resource, _) =>
        {
          if (resource.Id == "git")
          {
            runtimeInstalled = true;
          }

          return ValueTask.FromResult(AppliedWithStepEvidence(resource));
        });
    using var sink = new LateCancellingEventSink(cancellation);
    var store = new InMemoryRunStore();
    var (service, _) = CreateService(
        provider,
        profile: Profile(includeDependentResource: true),
        eventSink: sink,
        store: store);

    var run = await service.ApplyAsync(Request(), cancellation.Token);

    Assert.True(cancellation.IsCancellationRequested);
    Assert.Equal(2, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["dependent"].Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.DoesNotContain(store.SavedSnapshots, snapshot =>
        snapshot.ResourceResults.TryGetValue("dependent", out var result) &&
        result.Error?.Summary == "Applied resource evidence could not be fully published.");
  }

  [Fact]
  public async Task ApplyAsync_AbsentActualRestartEvidenceRetainsPlannedRestartPolicy()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      PlannedRestartPolicy = RestartPolicy.RestartRequired
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(
        RestartPolicy.RestartRequired,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRequired, run.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_PublishesDurableResourceRestartRequirement()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      PlannedRestartPolicy = RestartPolicy.RestartRequired
    };
    using var events = new RunEventHub();
    var observed = new List<RunEvent>();
    using var subscription = events.SubscribeRequired((runEvent, _) =>
    {
      observed.Add(runEvent);
      return Task.CompletedTask;
    });
    var (service, _) = CreateService(provider, eventSink: events);

    await service.ApplyAsync(Request(), CancellationToken.None);

    var completed = observed.Last(runEvent =>
        runEvent.Kind == RunEventKind.ResourceStateChanged &&
        runEvent.ResourceId == "git" &&
        runEvent.State == ExecutionState.Completed);
    Assert.Equal(RestartPolicy.RestartRequired, completed.RestartRequirement);
  }

  [Fact]
  public async Task ApplyAsync_ScopedOptionalObserverFailureDoesNotFailExecution()
  {
    var provider = new ScriptedProvider(Missing("git"));
    using var events = new RunEventHub();
    using var subscription = events.SubscribeScoped((_, _) =>
        Task.FromException(new InvalidOperationException("UI dispatcher stopped")));
    var (service, _) = CreateService(provider, eventSink: events);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_UsesActualProviderRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRecommended
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(
        RestartPolicy.RestartRecommended,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRecommended, run.RestartRequirements);
  }

  [Theory]
  [InlineData(3010, RestartPolicy.RestartRecommended)]
  [InlineData(1641, RestartPolicy.RestartRequired)]
  public async Task ApplyAsync_SuccessfulRestartExitRemainsSuccessful(
      int exitCode,
      RestartPolicy restartRequirement)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = restartRequirement,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = exitCode,
            Succeeded = true,
            Message = $"restartRequirement={restartRequirement}"
          }
        ]
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, resource.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, step.Outcome);
    Assert.Equal(exitCode, step.ProcessExitCode);
    Assert.True(step.ProcessSucceeded);
    Assert.Equal(restartRequirement, resource.RestartRequirement);
    Assert.Contains(restartRequirement, persisted!.RestartRequirements);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ApplyAsync_LateCancellationWithMissingOrUntrustedFinalVerificationCancelsFallback(
      bool includeUntrustedFinalVerification)
  {
    using var cancellation = new CancellationTokenSource();
    var verificationObservedCancellation = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        var result = new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded,
          FinalizeAfterCancellation = true,
          FinalVerification = includeUntrustedFinalVerification
              ? new VerificationResult
              {
                ResourceId = "different-resource",
                Compliance = ComplianceStatus.Satisfied,
                DetectedState = Satisfied("git", "2.52.1")
              }
              : null,
          RestartRequirement = RestartPolicy.RestartRecommended,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 1,
              ProcessExitCode = 3010,
              Succeeded = true,
              Message = "restartRequirement=RestartRecommended"
            }
          ]
        };
        return ValueTask.FromResult(result);
      },
      VerificationOperation = token =>
      {
        verificationObservedCancellation = token.IsCancellationRequested;
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Verification should observe cancellation.");
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.True(verificationObservedCancellation);
    Assert.Null(resource.DetectedAfter);
    Assert.Equal(RestartPolicy.RestartRecommended, resource.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.True(step.ProcessSucceeded);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRecommended, persisted.RestartRequirements);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_UsesAuthoritativeProviderFinalVerificationWithoutSecondVerify()
  {
    using var cancellation = new CancellationTokenSource();
    var detectedAfter = Satisfied("git", "2.52.1");
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded,
          FinalizeAfterCancellation = true,
          FinalVerification = new VerificationResult
          {
            ResourceId = "git",
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = detectedAfter
          }
        });
      },
      VerificationOperation = _ => throw new InvalidOperationException(
          "A second verification would escape the provider finalization deadline.")
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, resource.Outcome);
    Assert.Equal(detectedAfter, resource.DetectedAfter);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(0, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_UntrustedProviderFinalVerificationFallsBackToVerify()
  {
    var detectedAfter = Satisfied("git", "2.52.1");
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        FinalizeAfterCancellation = true,
        FinalVerification = new VerificationResult
        {
          ResourceId = "different-resource",
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = detectedAfter
        }
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Satisfied,
        DetectedState = detectedAfter
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(detectedAfter, run.ResourceResults["git"].DetectedAfter);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_FinalVerificationWithContradictoryErrorFallsBackToVerify()
  {
    var trustedDetectedAfter = Satisfied("git", "2.52.1");
    var contradictoryDetectedAfter = trustedDetectedAfter with
    {
      Error = "Detection reported an error despite satisfied compliance."
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        FinalizeAfterCancellation = true,
        FinalVerification = new VerificationResult
        {
          ResourceId = "git",
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = contradictoryDetectedAfter
        }
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Satisfied,
        DetectedState = trustedDetectedAfter
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(trustedDetectedAfter, run.ResourceResults["git"].DetectedAfter);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_UnflaggedLateCancellationCancelsFinalVerification()
  {
    using var cancellation = new CancellationTokenSource();
    var verificationTokenCanBeCanceled = false;
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationOperation = token =>
      {
        verificationTokenCanBeCanceled = token.CanBeCanceled;
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new VerificationResult
        {
          ResourceId = "git",
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = Satisfied("git", "2.52.1")
        });
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["git"].Outcome);
    Assert.True(verificationTokenCanBeCanceled);
    Assert.Equal(run.ResourceResults["git"], persisted!.ResourceResults["git"]);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_FailedProviderRetainsAndPersistsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        RestartRequirement = RestartPolicy.RestartRequired,
        Error = ProviderError("git", "Post-install verification failed.")
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var result = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.Equal(ComplianceStatus.Missing, result.FinalCompliance);
    Assert.Null(result.DetectedAfter);
    Assert.Equal(RestartPolicy.RestartRequired, result.RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persisted!.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_FailedFinalVerificationPropagatesComplianceErrorAndEvidence()
  {
    var finalError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Applied configuration does not match.",
        "The destination hash differs from the approved configuration.")
    {
      ResourceId = "git",
      StepId = "configure"
    };
    var detectedAfter = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      ConfigurationHash = "ACTUAL",
      Evidence = new Dictionary<string, string>
      {
        ["destinationSha256"] = "ACTUAL",
        ["expectedSha256"] = "EXPECTED"
      },
      Error = finalError.Detail,
      StructuredError = finalError
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = finalError,
        FinalVerification = new VerificationResult
        {
          ResourceId = "git",
          Compliance = ComplianceStatus.ConfigurationMismatch,
          DetectedState = detectedAfter,
          Message = finalError.Summary
        }
      }
    };
    var (service, store) = CreateService(provider);

    var report = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(report.RunId, CancellationToken.None);

    var result = report.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.Equal(ComplianceStatus.ConfigurationMismatch, result.FinalCompliance);
    Assert.Equal(finalError, result.Error);
    Assert.Equal(detectedAfter, result.DetectedAfter);
    Assert.Equal("ACTUAL", result.DetectedAfter!.Evidence["destinationSha256"]);
    Assert.Equal(result, persisted!.ResourceResults["git"]);
    Assert.Equal(0, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_CoreVerificationFailureRetainsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRecommended
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Missing,
        DetectedState = Missing("git")
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(
        RestartPolicy.RestartRecommended,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRecommended, run.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_EnrichesSuppliedFinalVerificationErrorWithProcessEvidence()
  {
    var suppliedError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Supplied final verification failure.",
        "Keep this safe verification detail.")
    {
      SuggestedAction = "Keep this safe suggested action.",
      IsRetryable = true
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = 3010,
            Succeeded = true
          }
        ]
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.ConfigurationMismatch,
        DetectedState = Missing("git")
      }
    };
    var (service, _) = CreateService(
        provider,
        complianceEvaluator: new FinalErrorComplianceEvaluator(suppliedError));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var error = run.ResourceResults["git"].Error!;
    Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
    Assert.Equal(suppliedError.Summary, error.Summary);
    Assert.Equal(suppliedError.Detail, error.Detail);
    Assert.Equal(suppliedError.SuggestedAction, error.SuggestedAction);
    Assert.True(error.IsRetryable);
    Assert.Equal("git", error.ResourceId);
    Assert.Equal("install", error.StepId);
    Assert.Equal(3010, error.ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_PreservesSuppliedFinalVerificationCorrelationOverProcessEvidence()
  {
    var suppliedError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Supplied final verification failure.",
        "Keep this safe verification detail.")
    {
      StepId = "verify-configuration",
      ProcessExitCode = 42
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = 3010,
            Succeeded = true
          }
        ]
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.ConfigurationMismatch,
        DetectedState = Missing("git")
      }
    };
    var (service, _) = CreateService(
        provider,
        complianceEvaluator: new FinalErrorComplianceEvaluator(suppliedError));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var error = run.ResourceResults["git"].Error!;
    Assert.Equal("verify-configuration", error.StepId);
    Assert.Equal(42, error.ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_VerificationExceptionRetainsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRequired,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = 1641,
            Succeeded = true
          }
        ]
      },
      VerificationOperation = _ => throw new IOException("verification unavailable")
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var resource = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal("git", resource.Error!.ResourceId);
    Assert.Equal("install", resource.Error.StepId);
    Assert.Equal(1641, resource.Error.ProcessExitCode);
    Assert.Equal(typeof(IOException).FullName, resource.Error.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_PublishesPersistedRunEventsInSequenceWithoutDroppingDetails()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ProgressEvents =
      [
        new ProviderProgress("install", 0.4, "installing", "install"),
        new ProviderProgress("install", 0.8, "provider-log hunter2", "install")
      ],
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            Message = "step-complete"
          }
        ],
        Diagnostics =
        [
          new StructuredError(
              WdemErrorCode.ProviderError,
              "provider diagnostic hunter2",
              "provider diagnostic detail hunter2")
          {
            UnderlyingException = new InvalidOperationException("hunter2")
          }
        ]
      }
    };
    var sink = new RunEventHub();
    var events = new List<RunEvent>();
    var profile = Profile();
    profile = profile with
    {
      Resources = profile.Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Value with
          {
            Parameters = new Dictionary<string, string?> { ["access_token"] = "hunter2" }
          },
          StringComparer.OrdinalIgnoreCase)
    };
    var (service, store) = CreateService(provider, profile, eventSink: sink);
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      Assert.NotNull(await store.GetAsync(runEvent.RunId, cancellationToken));
      events.Add(runEvent);
    });

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.NotEmpty(events);
    Assert.All(events, runEvent => Assert.Equal(run.RunId, runEvent.RunId));
    Assert.Equal(
        Enumerable.Range(1, events.Count).Select(value => (long)value),
        events.Select(runEvent => runEvent.Sequence));
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.RunStateChanged);
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.ResourceStateChanged);
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.StepProgress);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.ResourceStateChanged &&
        runEvent.State == ExecutionState.Completed &&
        runEvent.Outcome == ExecutionOutcome.Succeeded);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.StepProgress &&
        runEvent.Message == "installing" &&
        runEvent.State == ExecutionState.Running &&
        runEvent.Outcome is null);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.StepProgress &&
        runEvent.Message == "step-complete" &&
        runEvent.State == ExecutionState.Completed &&
        runEvent.Outcome == ExecutionOutcome.Succeeded);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.Log && runEvent.Message == "provider-log ***");
    Assert.DoesNotContain(events, runEvent => runEvent.Message.Contains("hunter2", StringComparison.Ordinal));
    Assert.DoesNotContain(events, runEvent =>
        runEvent.Error?.Detail.Contains("hunter2", StringComparison.Ordinal) == true ||
        runEvent.Error?.UnderlyingExceptionMessage?.Contains("hunter2", StringComparison.Ordinal) == true);
    Assert.Equal(RunEventKind.Completed, events[^1].Kind);
  }

  [Fact]
  public async Task InspectAsync_DetectsAndPlansButNeverCallsApply()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(RunMode.Inspect, run.Mode);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
    Assert.NotNull(run.Plan);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task ApplyAsync_CleanupDiagnosticFailure_PersistsTerminalRun(
      bool failDurableAppend)
  {
    var provider = new ScriptedProvider(Missing("git"));
    var store = new InMemoryRunStore();
    if (failDurableAppend)
    {
      store.AppendLogOperation = (_, entry, _) =>
          entry.Error?.Summary == "Elevated host cleanup failed."
              ? Task.FromException(new IOException("cleanup log unavailable"))
              : Task.CompletedTask;
    }

    var sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired((runEvent, _) =>
        !failDurableAppend && runEvent.Message == "Elevated host cleanup failed."
            ? Task.FromException(new IOException("cleanup observer unavailable"))
            : Task.CompletedTask);
    var (service, _) = CreateService(
        provider,
        store: store,
        eventSink: sink,
        dispatcher: new CleanupFailingDispatcher());

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Equal(run.Outcome, persisted.Outcome);
  }

  [Fact]
  public async Task InspectAsync_AnyAcceptedPlanErrorFailsRunAndSkipsResource()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var error = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Configuration cannot be applied.",
        "The accepted plan contains an invalid setting.");
    var planner = new TransformingPlanner(
        new ExecutionPlanner(registry, compliance),
        plan => plan with { Errors = [error] });
    var (service, _) = CreateService(provider, planner: planner);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(ExecutionOutcome.Skipped, run.ResourceResults["git"].Outcome);
    Assert.Equal(error, Assert.Single(run.Plan!.Errors));
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task InspectAsync_InvalidProfilePreservesValidationDiagnostics()
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "Profile validation failed.",
        "The profile version is required.");
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error]
    });
    var (service, store) = CreateService(provider, catalog: catalog);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(error, Assert.Single(Assert.IsType<ExecutionPlan>(run.Plan).Errors));
    var stored = await store.GetAsync(
        run.RunId,
        CancellationToken.None);
    Assert.Equal(error, Assert.Single(Assert.IsType<ExecutionPlan>(stored!.Plan).Errors));
  }

  [Fact]
  public async Task InspectAsync_InvalidProfilePathPersistsCatalogFailureWithoutThrowing()
  {
    const string invalidPath = "invalid\0profile.yaml";
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new DirectoryProfileCatalog(
        Path.GetTempPath(),
        new ResourceProviderRegistry([provider]));
    var (service, store) = CreateService(provider, catalog: catalog);

    var run = await service.InspectAsync(
        new RunRequest(
            invalidPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(invalidPath, run.ProfileSourcePath);
    Assert.Equal(WdemErrorCode.ProfileError, Assert.Single(run.Plan!.Errors).Code);
    Assert.Equal(run, await store.GetAsync(run.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task InspectAsync_InvalidDagPreservesDependencyDiagnostics()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Dependencies = ["missing"] }
      }
    };
    var (service, _) = CreateService(provider, profile);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    var error = Assert.Single(Assert.IsType<ExecutionPlan>(run.Plan).Errors);
    Assert.Equal(WdemErrorCode.DependencyError, error.Code);
    Assert.Contains("undefined resource 'missing'", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InspectAsync_PreparationFailurePlanIdentityIsContentAddressed()
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "Profile validation failed.",
        "The profile version is required.");
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error]
    });
    var (service, _) = CreateService(provider, catalog: catalog);

    var first = (await service.InspectAsync(Request(), CancellationToken.None)).Plan!;
    var second = (await service.InspectAsync(Request(), CancellationToken.None)).Plan!;
    var changedCatalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error with { Detail = "The profile id is required." }]
    });
    var (changedService, _) = CreateService(provider, catalog: changedCatalog);
    var changed = (await changedService.InspectAsync(Request(), CancellationToken.None)).Plan!;

    Assert.Equal(64, first.Fingerprint.Length);
    Assert.Equal(first.Fingerprint, second.Fingerprint);
    Assert.Equal(first.PlanId, second.PlanId);
    Assert.Equal(
        new Guid(Convert.FromHexString(first.Fingerprint).AsSpan(0, 16)),
        first.PlanId);
    Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    Assert.NotEqual(first.PlanId, changed.PlanId);
  }

  [Fact]
  public async Task RetryAsync_CreatesNewRunAndRedetectsBeforePlanning()
  {
    var provider = new ScriptedProvider(
        Missing("git"),
        Satisfied("git", "2.52.1"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = ProviderError("git", "Installation failed.")
      }
    };
    var (service, _) = CreateService(provider);
    var failed = await service.ApplyAsync(Request(), CancellationToken.None);

    var retried = await service.RetryAsync(
        failed.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.NotEqual(failed.RunId, retried.RunId);
    Assert.Equal(failed.RunId, retried.RetriedFromRunId);
    Assert.True(provider.DetectCalls >= 2);
    Assert.Equal(ExecutionOutcome.NotRequired, retried.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, retried.ResourceResults["git"].FinalCompliance);
    var approval = Assert.IsType<PlanApproval>(retried.PlanApproval);
    Assert.Equal(retried.Plan!.Fingerprint, approval.InitialPlanFingerprint);
    Assert.Equal(PlanApprovalSource.Retry, approval.Source);
  }

  [Fact]
  public async Task RetryAsync_PlansOnlyRequestedResourcesAndTheirDependencies()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git"
          ? Missing("git")
          : new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Failed,
            Error = "unrelated detection failed"
          }
    };
    var (service, store) = CreateService(provider, Profile(includeBrokenResource: true));
    var prior = FailedRun("git", "broken");
    await store.CreateAsync(prior, CancellationToken.None);

    var retried = await service.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, retried.ResourceResults["git"].Outcome);
    Assert.Equal(["git"], provider.DetectedResourceIds);
    Assert.Equal(["git"], retried.Plan!.Resources.Select(resource => resource.Definition.Id));
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task RetryAsync_RejectsInspectPriorWithoutApplying()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var prior = FailedRun("git") with { Mode = RunMode.Inspect };
    await store.CreateAsync(prior, CancellationToken.None);

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.RetryAsync(
            prior.RunId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
            CancellationToken.None));

    Assert.Contains("apply run", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task RetryAsync_RejectsPriorWithoutApprovalWithoutApplying()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var prior = FailedRun("git") with { PlanApproval = null };
    await store.CreateAsync(prior, CancellationToken.None);

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.RetryAsync(
            prior.RunId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
            CancellationToken.None));

    Assert.Contains("approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task RetryAsync_ChangedPlanRequiresReviewWithoutApplying()
  {
    var initialProvider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = ProviderError("git", "initial apply failed")
      }
    };
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var store = new InMemoryRunStore(sharedRuns);
    var (initialService, _) = CreateService(initialProvider, store: store);
    var prior = await initialService.ApplyAsync(Request(), CancellationToken.None);
    var changedProvider = new ScriptedProvider(Missing("git"))
    {
      PlanOperation = (resource, _) => new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = ComplianceStatus.ConfigurationMismatch,
        IsExecutable = true,
        Steps =
        [
          new PlanStep
          {
            Id = "destructive-upgrade",
            Description = "Destructively upgrade the resource",
            Action = PlanAction.Upgrade,
            PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
            RestartPolicy = RestartPolicy.NoRestart,
            IsDestructive = true
          }
        ]
      }
    };
    var (retryService, _) = CreateService(changedProvider, store: store);

    var retried = await retryService.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(0, changedProvider.ApplyCalls);
    Assert.Null(retried.PlanApproval);
    Assert.Equal(ExecutionOutcome.Failed, retried.Outcome);
    var error = Assert.Single(
        retried.Plan!.Errors,
        candidate => candidate.Code == WdemErrorCode.ConfigurationError);
    Assert.Contains("review", error.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RetryAsync_DeletedRequestedResourceRequiresReviewWithoutApplying()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var store = new InMemoryRunStore(sharedRuns);
    var prior = FailedRun("git");
    await store.CreateAsync(prior, CancellationToken.None);
    var currentProfile = Profile(includeBrokenResource: true);
    var profileWithoutGit = currentProfile with
    {
      RequiredResources = [new ProfileResourceReference { Id = "broken" }],
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["broken"] = currentProfile.Resources["broken"]
      }
    };
    var (service, _) = CreateService(provider, profileWithoutGit, store: store);

    var retried = await service.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(0, provider.ApplyCalls);
    Assert.Null(retried.PlanApproval);
    Assert.Equal(ExecutionOutcome.Failed, retried.Outcome);
    Assert.Equal(prior.RunId, retried.RetriedFromRunId);
    var error = Assert.Single(
        retried.Plan!.Errors,
        candidate => candidate.Code == WdemErrorCode.ConfigurationError);
    Assert.Contains("review", error.Detail, StringComparison.OrdinalIgnoreCase);
    var persisted = await store.GetAsync(retried.RunId, CancellationToken.None);
    Assert.Equal(retried, persisted);
  }

  [Fact]
  public async Task ApplyAsync_RequiresSatisfiedVerificationBeforeRecordingSuccess()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Missing,
        DetectedState = Missing("git"),
        Message = "git was still missing"
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(WdemErrorCode.VerificationError, run.ResourceResults["git"].Error?.Code);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_NotRequiredCannotSucceedWhenResourceStillNeedsRemediation()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.NotRequired
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_SatisfiedPlanWithExecutionPreconditionDispatchesProviderPreflight()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"))
    {
      IncludeExecutionPrecondition = true,
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.NotRequired
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.NotRequired, run.ResourceResults["git"].Outcome);
  }

  [Fact]
  public async Task ApplyAsync_SatisfiedPlanWithoutExecutionPreconditionSkipsProviderPreflight()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.NotRequired, run.ResourceResults["git"].Outcome);
  }

  [Fact]
  public async Task ApplyAsync_UnexecutablePlanPreservesSatisfiedResourceEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git"
          ? Satisfied("git", "2.52.1")
          : new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Failed,
            Error = "detection failed"
          }
    };
    var (service, _) = CreateService(provider, Profile(includeBrokenResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.NotRequired, run.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, run.ResourceResults["git"].FinalCompliance);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["broken"].Outcome);
    Assert.Equal(ComplianceStatus.DetectionFailed, run.ResourceResults["broken"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_PersistsVerifiedResultBeforeCompletingRun()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["git"].Outcome);
    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["git"].State == ExecutionState.Completed &&
        snapshot.ResourceResults["git"].Outcome == ExecutionOutcome.Succeeded);
    Assert.Equal(
        Enumerable.Range(0, store.SavedSnapshots.Count).Select(revision => (long)revision),
        store.SavedSnapshots.Select(snapshot => snapshot.Revision));
    Assert.Equal(store.SavedSnapshots[^1].Revision, run.Revision);
  }

  [Fact]
  public async Task ApplyAsync_PersistsSchedulerReadyAndBlockedTransitions()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => Missing(resource.Id),
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = ProviderError("git", "apply failed")
      }
    };
    var (service, store) = CreateService(provider, Profile(includeDependentResource: true));

    await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["git"].State == ExecutionState.Ready);
    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["dependent"].State == ExecutionState.Blocked);
  }

  [Fact]
  public async Task RunTransitions_FailedSaveLeavesCurrentAtLastPersistedSnapshot()
  {
    var initial = InterruptedRun() with
    {
      State = ExecutionState.Running,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Pending,
          DetectedBefore = Missing("git")
        }
      }
    };
    var store = new InMemoryRunStore
    {
      SaveOperation = (_, _) => Task.FromException(
          new InvalidOperationException("snapshot failed"))
    };
    await store.CreateAsync(initial, CancellationToken.None);
    var transitionsType = typeof(EnvironmentRunService).GetNestedType(
        "RunTransitions",
        System.Reflection.BindingFlags.NonPublic)!;
    var transitions = Activator.CreateInstance(
        transitionsType,
        System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
        binder: null,
        args: [store, initial],
        culture: null)!;
    var setResource = transitionsType.GetMethod(
        "SetResourceAsync",
        System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public)!;
    var save = Assert.IsAssignableFrom<Task>(setResource.Invoke(
        transitions,
        [new ResourceResult { ResourceId = "git", State = ExecutionState.Ready },
         CancellationToken.None]));

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => save);

    Assert.Equal("snapshot failed", error.Message);
    var current = Assert.IsType<ExecutionRun>(transitionsType.GetProperty("Current")!.GetValue(
        transitions));
    Assert.Same(initial, current);
    Assert.Same(initial, await store.GetAsync(initial.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task ApplyAsync_PersistsCancelledTerminalStateWithIndependentIoToken()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled
        });
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["git"].Outcome);
    Assert.Equal(run, await store.GetAsync(run.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task ApplyAsync_CancellationWaitsForProviderEvidenceAndPersistsIt()
  {
    using var cancellation = new CancellationTokenSource();
    var applyCancelled = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        applyCancelled.SetResult();
        await releaseApply.Task;
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRecommended,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.5,
              ProcessExitCode = 3010,
              Message = "The update completed before cancellation."
            }
          ]
        };
      }
    };
    var (service, store) = CreateService(provider);

    var apply = service.ApplyAsync(Request(), cancellation.Token);
    await applyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    releaseApply.SetResult();
    var run = await apply;
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, resource.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRecommended, persisted.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_CancellationPreservesProviderEvidenceBeyondSchedulerDrainDefault()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRecommended,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.75,
              ProcessExitCode = 3010,
              Message = "The update completed before cancellation."
            }
          ]
        };
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, resource.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRecommended, persisted.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_CancellationWaitsForBoundedProviderFinalizationBeyondGenericDrain()
  {
    var genericDrain = TimeSpan.FromMilliseconds(150);
    var providerFinalization = TimeSpan.FromMilliseconds(400);
    using var cancellation = new CancellationTokenSource();
    var verificationError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Final verification failed.",
        "The committed settings were rewritten after the external process completed.")
    {
      ResourceId = "git"
    };
    var detectedAfter = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      ConfigurationHash = new string('B', 64),
      StructuredError = verificationError
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      Capabilities = new ProviderCapabilities
      {
        MaxConcurrentOperations = 1,
        CancellationFinalizationTimeout = providerFinalization
      },
      ProgressEvents =
      [
        new ProviderProgress("apply", 0.5, "External process started.")
        {
          BeginsCancellationFinalization = true
        }
      ],
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Failed,
          FinalizeAfterCancellation = true,
          Error = verificationError,
          FinalVerification = new VerificationResult
          {
            ResourceId = "git",
            Compliance = ComplianceStatus.ConfigurationMismatch,
            DetectedState = detectedAfter,
            Message = "The committed settings no longer match."
          }
        };
      }
    };
    var (service, store) = CreateService(
        provider,
        scheduler: new ResourceScheduler(genericDrain));

    var apply = service.ApplyAsync(Request(), cancellation.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(225));

    Assert.False(apply.IsCompleted);
    var run = await apply.WaitAsync(TimeSpan.FromSeconds(2));
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    var resource = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, resource.Outcome);
    Assert.Equal(ComplianceStatus.ConfigurationMismatch, resource.FinalCompliance);
    Assert.Equal(detectedAfter, resource.DetectedAfter);
    Assert.Equal(verificationError, resource.Error);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
  }

  [Fact]
  public async Task ApplyAsync_CancellationBetweenProcessStartAndProgressPreservesFinalizationBudget()
  {
    var genericDrain = TimeSpan.FromMilliseconds(150);
    var providerFinalization = TimeSpan.FromMilliseconds(400);
    using var cancellation = new CancellationTokenSource();
    var processStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseStartedProgress = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      Capabilities = new ProviderCapabilities
      {
        MaxConcurrentOperations = 1,
        CancellationFinalizationTimeout = providerFinalization
      },
      ApplyWithProgressOperation = async (progress, _) =>
      {
        processStarted.TrySetResult();
        cancellation.Cancel();
        await releaseStartedProgress.Task;
        progress?.Report(new ProviderProgress("apply", 0.5, "Process started.")
        {
          BeginsCancellationFinalization = true
        });
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Failed,
          FinalizeAfterCancellation = true,
          Error = new StructuredError(
              WdemErrorCode.VerificationError,
              "Final verification failed.",
              "The launched process completed without a valid final state.")
        };
      }
    };
    var (service, _) = CreateService(
        provider,
        scheduler: new ResourceScheduler(genericDrain));

    var apply = service.ApplyAsync(Request(), cancellation.Token);
    await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await Task.Delay(TimeSpan.FromMilliseconds(225));
    var completedBeforeStartedProgress = apply.IsCompleted;
    releaseStartedProgress.TrySetResult();

    var run = await apply.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.False(completedBeforeStartedProgress);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
  }

  [Fact]
  public async Task ApplyAsync_CancellationBeforeLaunchCompletesPromptlyWithPotentialFinalization()
  {
    var genericDrain = TimeSpan.FromMilliseconds(150);
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      Capabilities = new ProviderCapabilities
      {
        MaxConcurrentOperations = 1,
        CancellationFinalizationTimeout = TimeSpan.FromSeconds(5)
      },
      ApplyOperation = async cancellationToken =>
      {
        cancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled
        };
      }
    };
    var (service, _) = CreateService(
        provider,
        scheduler: new ResourceScheduler(genericDrain));

    var started = Stopwatch.StartNew();
    var apply = service.ApplyAsync(Request(), cancellation.Token);
    var run = await apply.WaitAsync(TimeSpan.FromSeconds(1));

    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["git"].Outcome);
    Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
  }

  [Fact]
  public async Task ApplyAsync_CancellationUsesOneDeadlineAndPreservesLateProviderEvidence()
  {
    var drainBudget = TimeSpan.FromMilliseconds(600);
    using var cancellation = new CancellationTokenSource();
    var store = new InMemoryRunStore
    {
      AppendLogOperation = async (_, entry, token) =>
      {
        if (entry.Message == "blocked progress")
        {
          await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
      }
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ProgressEvents =
      [
        new ProviderProgress("install", 0.25, "blocked progress")
      ],
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(450));
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRequired,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.8,
              ProcessExitCode = 1641,
              Error = new StructuredError(
                  WdemErrorCode.ProviderError,
                  "Installer cancellation recorded.",
                  "The installer initiated a restart before cancellation.")
            }
          ]
        };
      }
    };
    var (service, persistedStore) = CreateService(
        provider,
        store: store,
        scheduler: new ResourceScheduler(drainBudget),
        persistenceTimeout: TimeSpan.FromSeconds(5));
    var stopwatch = Stopwatch.StartNew();

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await persistedStore.GetAsync(run.RunId, CancellationToken.None);

    stopwatch.Stop();
    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal(1641, step.ProcessExitCode);
    Assert.Equal(
        "The installer initiated a restart before cancellation.",
        step.Error!.Detail);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRequired, persisted.RestartRequirements);
    Assert.True(
        stopwatch.Elapsed < TimeSpan.FromMilliseconds(900),
        $"Cancellation took {stopwatch.Elapsed} for a {drainBudget} drain budget.");
  }

  [Fact]
  public async Task ApplyAsync_CancellationBoundsRunCleanupAndPreservesProviderEvidence()
  {
    var drainBudget = TimeSpan.FromMilliseconds(600);
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRequired,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.9,
              ProcessExitCode = 1641,
              Error = new StructuredError(
                  WdemErrorCode.ProviderError,
                  "Installer cancellation recorded.",
                  "The installer initiated a restart before cleanup began.")
            }
          ]
        });
      }
    };
    var dispatcher = new BlockingCleanupDispatcher();
    var (service, store) = CreateService(
        provider,
        dispatcher: dispatcher,
        scheduler: new ResourceScheduler(drainBudget));
    var stopwatch = Stopwatch.StartNew();
    var apply = service.ApplyAsync(Request(), cancellation.Token);

    await dispatcher.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    ExecutionRun run;
    try
    {
      run = await apply.WaitAsync(TimeSpan.FromMilliseconds(1200));
    }
    finally
    {
      dispatcher.ReleaseCleanup.TrySetResult();
    }

    stopwatch.Stop();
    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal(1641, step.ProcessExitCode);
    Assert.Equal(
        "The installer initiated a restart before cleanup began.",
        step.Error!.Detail);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.NotNull(dispatcher.ApplyDeadline);
    Assert.True(dispatcher.ApplyDeadline.IsStarted);
    Assert.True(dispatcher.CleanupToken.CanBeCanceled);
    Assert.True(
        stopwatch.Elapsed < TimeSpan.FromMilliseconds(1000),
        $"Cancellation took {stopwatch.Elapsed} for a {drainBudget} drain budget.");
  }

  [Fact]
  public async Task ApplyAsync_LateFailedTransitionIsNotOverwrittenByTerminalCancellation()
  {
    var lateFailure = new ResourceResult
    {
      ResourceId = "git",
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Late provider failure.",
          "The provider failed after the cancellation drain deadline.")
      {
        ResourceId = "git"
      },
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var scheduler = new LateTransitionScheduler(lateFailure);
    var dispatcher = new BlockingCleanupDispatcher();
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(
        provider,
        scheduler: scheduler,
        dispatcher: dispatcher);
    var apply = service.ApplyAsync(Request(), CancellationToken.None);

    await dispatcher.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    scheduler.ReleaseLateTransition.TrySetResult();
    await scheduler.TransitionCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    dispatcher.ReleaseCleanup.TrySetResult();
    var run = await apply.WaitAsync(TimeSpan.FromSeconds(5));
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    var resource = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, resource.Outcome);
    Assert.Equal(lateFailure.Error, resource.Error);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Succeeded)]
  [InlineData(ExecutionOutcome.NotRequired)]
  public async Task ApplyAsync_LateDependencySatisfyingTransitionIsNotOverwrittenByCancellation(
      ExecutionOutcome outcome)
  {
    var lateResult = new ResourceResult
    {
      ResourceId = "git",
      State = ExecutionState.Completed,
      Outcome = outcome,
      FinalCompliance = ComplianceStatus.Satisfied,
      Progress = 1,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var scheduler = new LateTransitionScheduler(lateResult);
    var dispatcher = new BlockingCleanupDispatcher();
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(
        provider,
        scheduler: scheduler,
        dispatcher: dispatcher);
    var apply = service.ApplyAsync(Request(), CancellationToken.None);

    await dispatcher.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    scheduler.ReleaseLateTransition.TrySetResult();
    await scheduler.TransitionCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    dispatcher.ReleaseCleanup.TrySetResult();
    var run = await apply.WaitAsync(TimeSpan.FromSeconds(5));
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(outcome, run.ResourceResults["git"].Outcome);
    Assert.Equal(outcome, persisted!.ResourceResults["git"].Outcome);
  }

  [Fact]
  public async Task ApplyAsync_FailedTransitionAfterTerminalUpdatesPersistedOutcome()
  {
    var lateFailure = new ResourceResult
    {
      ResourceId = "git",
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Late provider failure.",
          "The provider failed after terminal cancellation was persisted.")
      {
        ResourceId = "git"
      },
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var scheduler = new LateTransitionScheduler(lateFailure);
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider, scheduler: scheduler);

    var returned = await service.ApplyAsync(Request(), CancellationToken.None);
    scheduler.ReleaseLateTransition.TrySetResult();
    var finalized = await ((IEnvironmentRunFinalizationService)service)
        .WaitForRunFinalizationAsync(returned.RunId, CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(5));
    var persisted = await store.GetAsync(returned.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Cancelled, returned.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, finalized.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, persisted!.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, persisted.ResourceResults["git"].Outcome);
    Assert.Equal(lateFailure.Error, persisted.ResourceResults["git"].Error);
  }

  [Fact]
  public async Task ApplyAsync_InfiniteUndrainedFinalizationDoesNotDelayProvisionalRun()
  {
    var pendingFinalization = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = new FinalizationOnlyScheduler(pendingFinalization.Task);
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider, scheduler: scheduler);

    var returned = await service.ApplyAsync(Request(), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));
    using var observationCancellation = new CancellationTokenSource(
        TimeSpan.FromMilliseconds(100));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        ((IEnvironmentRunFinalizationService)service).WaitForRunFinalizationAsync(
            returned.RunId,
            observationCancellation.Token));
    Assert.Equal(ExecutionOutcome.Cancelled, returned.Outcome);
  }

  [Fact]
  public async Task ApplyAsync_LateAggregateFinalizationFaultIsObservable()
  {
    var finalization = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var scheduler = new FinalizationOnlyScheduler(finalization.Task);
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider, scheduler: scheduler);
    var persistenceFailure = new IOException("late transition persistence failed");
    var requiredFailure = new RequiredRunEventDeliveryException(
        new InvalidOperationException("required event delivery failed"));

    var returned = await service.ApplyAsync(Request(), CancellationToken.None);
    var aggregate = new AggregateException(persistenceFailure, requiredFailure);
    finalization.TrySetException(aggregate);

    var actual = await Assert.ThrowsAsync<AggregateException>(() =>
        ((IEnvironmentRunFinalizationService)service).WaitForRunFinalizationAsync(
            returned.RunId,
            CancellationToken.None));

    Assert.Same(aggregate, actual);
    Assert.Contains(persistenceFailure, actual.InnerExceptions);
    Assert.Contains(requiredFailure, actual.InnerExceptions);
  }

  [Fact]
  public async Task ApplyAsync_CompletedFinalizationRegistrationDoesNotRemainTracked()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var scheduler = new CompletedFinalizationRegisteringScheduler();
    var (service, _) = CreateService(provider, scheduler: scheduler);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
  }

  [Fact]
  public async Task ApplyAsync_NeverCompletingFinalizationsRemainBounded()
  {
    var scheduler = new BoundedFinalizationScheduler(int.MaxValue, lateResult: null);
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider, scheduler: scheduler);

    for (var index = 0; index < 65; index++)
    {
      await service.ApplyAsync(Request(), CancellationToken.None)
          .WaitAsync(TimeSpan.FromSeconds(1));
    }

    Assert.True(TrackedFinalizationCount(service) <= 64);
  }

  [Fact]
  public async Task WaitForRunFinalizationAsync_EvictedRegistrationReturnsBestEffortSnapshotWhileFinalizationContinues()
  {
    var lateFailure = new ResourceResult
    {
      ResourceId = "git",
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Late provider failure.",
          "The provider failed after provisional cancellation."),
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var finalizationFault = new IOException(
        $"evicted finalization fault {Guid.NewGuid():N}");
    var scheduler = new EvictedFirstFinalizationScheduler(lateFailure, finalizationFault);
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider, scheduler: scheduler);
    var first = await service.ApplyAsync(Request(), CancellationToken.None);
    for (var index = 0; index < 64; index++)
    {
      await service.ApplyAsync(Request(), CancellationToken.None)
          .WaitAsync(TimeSpan.FromSeconds(1));
    }

    Assert.Equal(64, TrackedFinalizationCount(service));
    Assert.True(scheduler.FirstFinalization.TryGetTarget(out var evictedFinalization));

    // Discoverability is bounded and best-effort. Once evicted, waiting returns the latest
    // durable snapshot; it does not imply that the detached finalization is definitive.
    var bestEffort = await ((IEnvironmentRunFinalizationService)service)
        .WaitForRunFinalizationAsync(first.RunId, CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));
    Assert.Equal(ExecutionOutcome.Cancelled, bestEffort.Outcome);

    scheduler.ReleaseFirstFinalization.TrySetResult();
    await scheduler.FirstTransitionPublished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await scheduler.FirstFinalizationFaulted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    var persisted = await WaitForPersistedOutcomeAsync(
        store,
        first.RunId,
        ExecutionOutcome.Failed);
    await WaitForTaskCompletionWithoutObservingFaultAsync(evictedFinalization!);

    Assert.Equal(ExecutionOutcome.Failed, persisted.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, persisted.ResourceResults["git"].Outcome);
    Assert.Equal(lateFailure.Error, persisted.ResourceResults["git"].Error);
    Assert.True(TaskFaultWasObserved(evictedFinalization!));
  }

  [Fact]
  public async Task TrackRunFinalization_ConcurrentRegistrationsNeverExceedDiscoverabilityBound()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);
    var track = typeof(EnvironmentRunService).GetMethod(
        "TrackRunFinalization",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)!;
    using var start = new ManualResetEventSlim();
    using var sampling = new CancellationTokenSource();
    var maximumObserved = 0;
    var sampler = Task.Run(async () =>
    {
      while (!sampling.IsCancellationRequested)
      {
        UpdateMaximum(ref maximumObserved, TrackedFinalizationCount(service));
        await Task.Yield();
      }
    });
    var registrations = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
    {
      start.Wait();
      for (var index = 0; index < 64; index++)
      {
        track.Invoke(service, [
          Guid.NewGuid(),
          new TaskCompletionSource(
              TaskCreationOptions.RunContinuationsAsynchronously).Task
        ]);
      }
    })).ToArray();

    start.Set();
    await Task.WhenAll(registrations).WaitAsync(TimeSpan.FromSeconds(5));
    UpdateMaximum(ref maximumObserved, TrackedFinalizationCount(service));
    await sampling.CancelAsync();
    await sampler.WaitAsync(TimeSpan.FromSeconds(1));

    Assert.InRange(maximumObserved, 1, 64);
    Assert.Equal(64, TrackedFinalizationCount(service));
  }

  [Fact]
  public async Task ApplyAsync_SuccessWaitsForNormalRunCleanupPolicy()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var dispatcher = new BlockingCleanupDispatcher();
    var (service, _) = CreateService(
        provider,
        dispatcher: dispatcher,
        scheduler: new ResourceScheduler(TimeSpan.FromMilliseconds(100)));

    var apply = service.ApplyAsync(Request(), CancellationToken.None);
    await dispatcher.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    bool completedBeforeRelease;
    bool cleanupTokenCanBeCanceled;
    try
    {
      await Task.Delay(TimeSpan.FromMilliseconds(250));
      completedBeforeRelease = apply.IsCompleted;
      cleanupTokenCanBeCanceled = dispatcher.CleanupToken.CanBeCanceled;
    }
    finally
    {
      dispatcher.ReleaseCleanup.TrySetResult();
    }

    var run = await apply.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(completedBeforeRelease);
    Assert.False(cleanupTokenCanBeCanceled);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
  }

  [Fact]
  public async Task ApplyAsync_SupportsSchedulerImplementingLegacyInterfaceContract()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var scheduler = new LegacyCompatibleScheduler();
    var (service, _) = CreateService(provider, scheduler: scheduler);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.True(scheduler.ExecuteCalled);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
  }

  [Fact]
  public async Task Recovery_RedetectsAndReplansFromAnIncompleteSnapshot()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(interrupted.RunId, candidate.RunId);
    Assert.Contains("git", candidate.PendingResourceIds);
    Assert.NotEqual(interrupted.RunId, recovered.RunId);
    Assert.Equal(interrupted.RunId, recovered.RetriedFromRunId);
    Assert.True(provider.DetectCalls >= 1);
    Assert.NotNull(recovered.Plan);
    Assert.NotNull(recovered.PlanApproval);
    Assert.Equal(ExecutionOutcome.NotRequired, recovered.ResourceResults["git"].Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Theory]
  [InlineData(false, true)]
  [InlineData(true, false)]
  public async Task RecoverAsync_RejectsMissingHistoricalApprovalEvidenceWithoutApplying(
      bool includePlan,
      bool includeApproval)
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var approved = InterruptedRun();
    var interrupted = approved with
    {
      Plan = includePlan ? approved.Plan : null,
      PlanApproval = includeApproval ? approved.PlanApproval : null
    };
    await store.CreateAsync(interrupted, CancellationToken.None);

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.RecoverAsync(interrupted.RunId, CancellationToken.None));

    Assert.Contains("approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.ApplyCalls);
    var persisted = await store.GetAsync(interrupted.RunId, CancellationToken.None);
    Assert.Null(persisted!.RecoveryClaimId);
  }

  [Theory]
  [InlineData("definition")]
  [InlineData("action")]
  [InlineData("privilege")]
  [InlineData("step")]
  public async Task RecoverAsync_FreshPlanOutsideHistoricalBoundaryDoesNotApply(
      string violation)
  {
    DeveloperProfile profile = Profile();
    if (violation == "definition")
    {
      ResourceDefinition changed = profile.Resources["git"] with
      {
        VersionConstraint = ">=9.0.0"
      };
      profile = profile with
      {
        Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = changed
        }
      };
    }

    var provider = new ScriptedProvider(Missing("git"))
    {
      PlanOperation = violation == "definition"
          ? null
          : (resource, state) => ExecutablePlan(resource, state) with
          {
            Steps =
            [
              new PlanStep
              {
                Id = violation == "step" ? "new-install-step" : "install",
                Description = "Fresh recovery work",
                Action = violation == "action" ? PlanAction.Upgrade : PlanAction.Install,
                PrivilegeRequirement = violation == "privilege"
                    ? PrivilegeRequirement.Administrator
                    : PrivilegeRequirement.CurrentUser,
                RestartPolicy = RestartPolicy.NoRestart
              }
            ]
          }
    };
    var (service, store) = CreateService(provider, profile);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    Assert.True(provider.DetectCalls >= 1);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Failed, recovered.Outcome);
    Assert.Null(recovered.PlanApproval);
    var error = Assert.Single(
        recovered.Plan!.Errors,
        candidate => candidate.Code == WdemErrorCode.ConfigurationError);
    Assert.Contains("prior approval", error.Summary, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RecoverAsync_ReplansDeferredResourceWithinHistoricalAuthorization()
  {
    var interrupted = await InterruptedDeferredRunAsync();
    var historicalProof = Assert.Single(interrupted.PlanApproval!.DeferredAuthorizations);
    Assert.Equal("dependent", historicalProof.ResourceId);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        interrupted.Plan!.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status);

    var dependencyInstalled = false;
    var provider = DeferredProvider(
        () => dependencyInstalled,
        () => dependencyInstalled = true);
    var (service, store) = CreateService(
        provider,
        Profile(includeDependentResource: true));
    await store.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(2, provider.ApplyCalls);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.ResourceResults["dependent"].Outcome);
    Assert.Equal(
        PlannedResourceStatus.Ready,
        recovered.Plan!.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status);
    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.RunId == recovered.RunId &&
        snapshot.Plan?.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status == PlannedResourceStatus.Deferred);
  }

  [Fact]
  public async Task RecoverAsync_AcceptsPersistedConcreteDeferredRefinementWithinProof()
  {
    var interrupted = await InterruptedDeferredRunAsync(afterRefinement: true);
    Assert.Single(interrupted.PlanApproval!.DeferredAuthorizations);
    Assert.NotEqual(interrupted.PlanApproval.InitialPlanFingerprint, interrupted.Plan!.Fingerprint);
    Assert.Equal(
        PlannedResourceStatus.Ready,
        interrupted.Plan.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status);

    var dependencyInstalled = true;
    var dependentApplyCalls = 0;
    var provider = DeferredProvider(
        () => dependencyInstalled,
        () => dependencyInstalled = true,
        applyOperation: (resource, _) =>
        {
          if (resource.Id == "dependent")
          {
            dependentApplyCalls++;
          }

          return ValueTask.FromResult(new ResourceApplyResult
          {
            ResourceId = resource.Id,
            Outcome = ApplyOutcome.Succeeded
          });
        });
    var (service, store) = CreateService(
        provider,
        Profile(includeDependentResource: true));
    await store.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, dependentApplyCalls);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.ResourceResults["dependent"].Outcome);
  }

  [Fact]
  public async Task RecoverAsync_RejectsDeferredRuntimeRefinementOutsideHistoricalAuthorization()
  {
    var interrupted = await InterruptedDeferredRunAsync();
    var dependencyInstalled = false;
    var dependentApplyCalls = 0;
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git" && dependencyInstalled
          ? Satisfied(resource.Id, "2.52.1")
          : Missing(resource.Id),
      PlanOperation = (resource, state) => resource.Id == "dependent" && !dependencyInstalled
          ? DependencyUnavailablePlan(resource)
          : ExecutablePlan(resource, state) with
          {
            Steps = state.Exists
                ? []
                :
                [
                  new PlanStep
                  {
                    Id = $"install-{resource.Id}",
                    Description = $"Configure {resource.Id}",
                    Action = resource.Id == "dependent"
                        ? PlanAction.Configure
                        : PlanAction.Install,
                    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
                    RestartPolicy = RestartPolicy.NoRestart
                  }
                ]
          },
      ApplyForResourceOperation = (resource, _) =>
      {
        if (resource.Id == "git")
        {
          dependencyInstalled = true;
        }
        else
        {
          dependentApplyCalls++;
        }

        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = resource.Id,
          Outcome = ApplyOutcome.Succeeded
        });
      },
      VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
          new VerificationResult
          {
            ResourceId = resource.Id,
            Compliance = ComplianceStatus.Satisfied,
            DetectedState = Satisfied(resource.Id, "2.52.1")
          })
    };
    var (service, store) = CreateService(
        provider,
        Profile(includeDependentResource: true));
    await store.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(0, dependentApplyCalls);
    Assert.Equal(
        WdemErrorCode.ConfigurationError,
        recovered.ResourceResults["dependent"].Error!.Code);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        recovered.Plan!.Resources.Single(resource =>
            resource.Definition.Id == "dependent").Status);
  }

  [Fact]
  public async Task RecoverAsync_RejectsTamperedNonDeferredPlanBesideDeferredProof()
  {
    var interrupted = await InterruptedDeferredRunAsync();
    Assert.Single(interrupted.PlanApproval!.DeferredAuthorizations);
    var approvedPlan = interrupted.Plan!;
    var tamperedResources = approvedPlan.Resources.Select(resource =>
        resource.Definition.Id == "git"
            ? resource with
            {
              ResourcePlan = resource.ResourcePlan with
              {
                Steps = resource.ResourcePlan.Steps.Select(step => step with
                {
                  Action = PlanAction.Upgrade
                }).ToArray()
              }
            }
            : resource).ToArray();
    var tamperedPlan = ExecutionPlanner.CreatePlan(
        approvedPlan.ProfileId,
        approvedPlan.ProfileVersion,
        approvedPlan.Layers,
        tamperedResources,
        approvedPlan.Errors);
    var tampered = interrupted with { Plan = tamperedPlan };
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(
        provider,
        Profile(includeDependentResource: true));
    await store.CreateAsync(tampered, CancellationToken.None);

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        service.RecoverAsync(tampered.RunId, CancellationToken.None));

    Assert.Contains("approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.ApplyCalls);
    var persisted = await store.GetAsync(tampered.RunId, CancellationToken.None);
    Assert.Null(persisted!.RecoveryClaimId);
  }

  [Fact]
  public async Task RecoverAsync_IgnoresSuccessfulInspectLinkedToPriorRun()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);
    var inspect = await service.InspectAsync(Request(), CancellationToken.None);
    inspect = await store.SaveAsync(
        inspect with { RetriedFromRunId = interrupted.RunId },
        CancellationToken.None);

    var recovered = await service.RecoverAsync(
        interrupted.RunId,
        CancellationToken.None);

    var persistedPrior = await store.GetAsync(interrupted.RunId, CancellationToken.None);
    Assert.Equal(RunMode.Inspect, inspect.Mode);
    Assert.Equal(ExecutionOutcome.Succeeded, inspect.Outcome);
    Assert.Equal(RunMode.Apply, recovered.Mode);
    Assert.NotEqual(inspect.RunId, recovered.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
  }

  [Fact]
  public async Task RecoverAsync_SuccessIsIdempotentAcrossServices()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = InterruptedRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persistedPrior = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    var recoveredAgain = await secondService.RecoverAsync(
        interrupted.RunId,
        CancellationToken.None);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(recovered.RunId, recoveredAgain.RunId);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
    Assert.Equal(ExecutionOutcome.Cancelled, persistedPrior.Outcome);
    Assert.NotNull(persistedPrior.EndedAtUtc);
    Assert.False(persistedPrior.ResourceResults["git"].DetectedBefore!.Exists);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == interrupted.RunId);
  }

  [Fact]
  public async Task RecoverAsync_DoesNotReuseEarlierPartialOrdinaryRetry()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"))
    {
      DetectState = resource => Satisfied(resource.Id, "2.52.1")
    };
    var (service, store) = CreateService(provider, Profile(includeBrokenResource: true));
    var prior = FailedRun("git", "broken") with
    {
      State = ExecutionState.Running,
      Outcome = null,
      EndedAtUtc = null
    };
    await store.CreateAsync(prior, CancellationToken.None);
    var ordinaryRetry = await service.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    var recovery = await service.RecoverAsync(prior.RunId, CancellationToken.None);
    var resumedAgain = await service.RecoverAsync(prior.RunId, CancellationToken.None);

    Assert.Equal(["broken", "git"], recovery.ResourceResults.Keys.Order());
    Assert.NotEqual(ordinaryRetry.RunId, recovery.RunId);
    Assert.Null(ordinaryRetry.RecoveredFromRunId);
    Assert.Equal(prior.RunId, recovery.RetriedFromRunId);
    Assert.Equal(prior.RunId, recovery.RecoveredFromRunId);
    Assert.Equal(recovery.RunId, resumedAgain.RunId);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulAttemptKeepsIncompletePriorAcrossServices(
      ApplyOutcome applyOutcome)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = applyOutcome,
        Error = applyOutcome == ApplyOutcome.Failed
            ? ProviderError("git", "apply failed")
            : null
      }
    };
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = InterruptedRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persistedPrior = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(
        applyOutcome == ApplyOutcome.Cancelled
            ? ExecutionOutcome.Cancelled
            : ExecutionOutcome.Failed,
        recovered.Outcome);
    Assert.Equal(ExecutionState.Running, persistedPrior!.State);
    Assert.Null(persistedPrior.Outcome);
    Assert.Null(persistedPrior.EndedAtUtc);
    Assert.Empty(persistedPrior.AcknowledgedRestartResourceIds);
    Assert.Contains(candidates, candidate => candidate.RunId == interrupted.RunId);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_IncludesCompletedRunWithPendingRestart()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(pendingRestart.RunId, candidate.RunId);
    Assert.Equal(["git"], candidate.PendingResourceIds);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_CompletedRunListsOnlyPendingRestartResources()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    var unrelatedFailure = FailedRun("broken").ResourceResults["broken"];
    await store.CreateAsync(pendingRestart with
    {
      ResourceResults = pendingRestart.ResourceResults
          .Append(new KeyValuePair<string, ResourceResult>("broken", unrelatedFailure))
          .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    }, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(["git"], candidate.PendingResourceIds);
  }

  [Fact]
  public async Task RecoverAsync_SuccessAcknowledgesPriorRestartCandidate()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var recovered = await service.RecoverAsync(
        pendingRestart.RunId,
        CancellationToken.None);
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persistedPrior = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        persistedPrior!.ResourceResults["git"].RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persistedPrior.RestartRequirements);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulAttemptKeepsPriorRestartCandidate(
      ApplyOutcome applyOutcome)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = applyOutcome,
        Error = applyOutcome == ApplyOutcome.Failed
            ? ProviderError("git", "apply failed")
            : null
      }
    };
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var recovered = await service.RecoverAsync(
        pendingRestart.RunId,
        CancellationToken.None);
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persistedPrior = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);

    Assert.Contains(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Empty(persistedPrior!.AcknowledgedRestartResourceIds);
    Assert.Equal(
        applyOutcome == ApplyOutcome.Cancelled
            ? ExecutionOutcome.Cancelled
            : ExecutionOutcome.Failed,
        recovered.Outcome);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_HidesRunWhileProviderIsApplying()
  {
    var applyEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async cancellationToken =>
      {
        applyEntered.SetResult();
        await releaseApply.Task.WaitAsync(cancellationToken);
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded
        };
      }
    };
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var applyTask = service.ApplyAsync(Request(), CancellationToken.None);

    try
    {
      await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

      var (recoveryService, _) = CreateService(
          new ScriptedProvider(Missing("git")),
          store: firstStore);
      var persisted = Assert.Single(
          await firstStore.ListAsync(CancellationToken.None));
      var candidates = await recoveryService.FindRecoveryCandidatesAsync(
          CancellationToken.None);

      Assert.Equal(ExecutionState.Running, persisted.State);
      Assert.Equal(ExecutionState.Running, persisted.ResourceResults["git"].State);
      Assert.Empty(candidates);
    }
    finally
    {
      releaseApply.TrySetResult();
      await applyTask;
    }
  }

  [Fact]
  public async Task AbandonAsync_CompletesInterruptedRunAsCancelledWithoutApplying()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);

    await service.AbandonAsync(interrupted.RunId, CancellationToken.None);

    var abandoned = await store.GetAsync(interrupted.RunId, CancellationToken.None);
    Assert.NotNull(abandoned);
    Assert.Equal(ExecutionState.Completed, abandoned.State);
    Assert.Equal(ExecutionOutcome.Cancelled, abandoned.Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task AbandonAsync_IncompleteRestartRunIsNotRecoverableAcrossServices()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = IncompleteRestartRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    await service.AbandonAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persisted = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Equal(ExecutionOutcome.Cancelled, persisted.Outcome);
    Assert.Contains("git", persisted.AcknowledgedRestartResourceIds);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == interrupted.RunId);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task AbandonAsync_AcknowledgesCompletedRestartCandidateWithoutApplying()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    await service.AbandonAsync(pendingRestart.RunId, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persisted = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        persisted!.ResourceResults["git"].RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persisted.RestartRequirements);
  }

  private static (EnvironmentRunService Service, InMemoryRunStore Store) CreateService(
      ScriptedProvider provider,
      DeveloperProfile? profile = null,
      IProfileCatalog? catalog = null,
      InMemoryRunStore? store = null,
      IRunEventSink? eventSink = null,
      IExecutionPlanner? planner = null,
      IResourceApplyDispatcher? dispatcher = null,
      IResourceScheduler? scheduler = null,
      TimeSpan? persistenceTimeout = null,
      IComplianceEvaluator? complianceEvaluator = null)
  {
    catalog ??= new FakeProfileCatalog(profile ?? Profile(), CanonicalProfilePath);
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = complianceEvaluator ?? new ComplianceEvaluator();
    store ??= new InMemoryRunStore();
    eventSink ??= new RunEventHub();
    return (
        new EnvironmentRunService(
            catalog,
            new ResourceGraphBuilder(),
            registry,
            compliance,
            planner ?? new ExecutionPlanner(registry, compliance),
            scheduler ?? new ResourceScheduler(),
            store,
            dispatcher ?? new DirectResourceApplyDispatcher(),
            timeProvider: null,
            eventSink,
            new LogRedactor(),
            persistenceTimeout),
        store);
  }

  private static int TrackedFinalizationCount(EnvironmentRunService service)
  {
    var field = typeof(EnvironmentRunService).GetField(
        "_runFinalizations",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic);
    var registry = Assert.IsAssignableFrom<object>(field!.GetValue(service));
    return Assert.IsType<int>(registry.GetType().GetProperty("Count")!.GetValue(registry));
  }

  private static async Task<ExecutionRun> WaitForPersistedOutcomeAsync(
      InMemoryRunStore store,
      Guid runId,
      ExecutionOutcome expectedOutcome)
  {
    for (var attempt = 0; attempt < 40; attempt++)
    {
      var persisted = await store.GetAsync(runId, CancellationToken.None);
      if (persisted?.Outcome == expectedOutcome)
      {
        return persisted;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(25));
    }

    throw new TimeoutException(
        $"Run '{runId:D}' did not persist outcome '{expectedOutcome}'.");
  }

  private static async Task WaitForTaskCompletionWithoutObservingFaultAsync(Task task)
  {
    for (var attempt = 0; attempt < 40 && !task.IsCompleted; attempt++)
    {
      await Task.Delay(TimeSpan.FromMilliseconds(25));
    }

    Assert.True(task.IsCompleted);
    Assert.True(task.IsFaulted);
  }

  private static bool TaskFaultWasObserved(Task task)
  {
    var contingentProperties = typeof(Task).GetField(
        "m_contingentProperties",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)!.GetValue(task);
    var exceptionsHolder = contingentProperties!.GetType().GetField(
        "m_exceptionsHolder",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)!.GetValue(contingentProperties);
    return (bool)exceptionsHolder!.GetType().GetField(
        "m_isHandled",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)!.GetValue(exceptionsHolder)!;
  }

  private static void UpdateMaximum(ref int location, int candidate)
  {
    var current = Volatile.Read(ref location);
    while (candidate > current)
    {
      var observed = Interlocked.CompareExchange(ref location, candidate, current);
      if (observed == current)
      {
        return;
      }

      current = observed;
    }
  }

  private sealed class FinalErrorComplianceEvaluator(StructuredError finalError)
      : IComplianceEvaluator
  {
    private readonly ComplianceEvaluator _inner = new();
    private int _calls;

    public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
    {
      if (Interlocked.Increment(ref _calls) <= 2)
      {
        return _inner.Evaluate(desired, current);
      }

      return new ComplianceResult(
          ComplianceStatus.ConfigurationMismatch,
          finalError.Summary,
          finalError);
    }
  }

  private sealed class ThrowingFreshComplianceEvaluator : IComplianceEvaluator
  {
    private readonly ComplianceEvaluator _inner = new();

    public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
    {
      if (current.Evidence.TryGetValue("phase", out var phase) &&
          string.Equals(phase, "fresh-deferred-detection", StringComparison.Ordinal))
      {
        throw new IOException("fresh compliance unavailable");
      }

      return _inner.Evaluate(desired, current);
    }
  }

  private static RunRequest Request() => new(
      "input/profile.yaml",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static DeveloperProfile Profile(
      bool includeBrokenResource = false,
      bool includeDependentResource = false,
      PrivilegeRequirement dependentPrivilege = PrivilegeRequirement.CurrentUser)
  {
    var required = new List<ProfileResourceReference>
    {
      new() { Id = "git" }
    };
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new ResourceDefinition
      {
        Id = "git",
        Type = "package",
        Provider = "fake",
        VersionConstraint = ">=2.50.0"
      }
    };
    if (includeBrokenResource)
    {
      required.Add(new ProfileResourceReference { Id = "broken" });
      resources.Add("broken", new ResourceDefinition
      {
        Id = "broken",
        Type = "package",
        Provider = "fake"
      });
    }

    if (includeDependentResource)
    {
      required.Add(new ProfileResourceReference { Id = "dependent" });
      resources.Add("dependent", new ResourceDefinition
      {
        Id = "dependent",
        Type = "package",
        Provider = "fake",
        Dependencies = ["git"],
        PrivilegeRequirement = dependentPrivilege
      });
    }

    return new DeveloperProfile
    {
      Id = "developer",
      Version = "1.0.0",
      DisplayName = "Developer",
      Description = "Developer workstation",
      RequiredResources = required,
      Resources = resources
    };
  }

  private static ExecutionRun FailedRun(params string[] resourceIds)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var planned = resourceIds.Select(id =>
    {
      var definition = new ResourceDefinition
      {
        Id = id,
        Type = "package",
        Provider = "fake",
        VersionConstraint = id == "git" ? ">=2.50.0" : null
      };
      return new PlannedResource
      {
        Definition = definition,
        Origin = ResourceOrigin.Required,
        Dependencies = [],
        ResourcePlan = new ResourcePlan
        {
          ResourceId = id,
          ResourceType = definition.Type,
          ProviderName = definition.Provider,
          DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(definition),
          Compliance = ComplianceStatus.Missing,
          IsExecutable = true,
          Steps =
          [
            new PlanStep
            {
              Id = "install",
              Description = $"Install {id}",
              Action = PlanAction.Install,
              PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
              RestartPolicy = RestartPolicy.NoRestart
            }
          ]
        },
        Status = PlannedResourceStatus.Ready,
        Risk = PlanRisk.Standard,
        RequiresElevation = false,
        IsDestructive = false,
        RestartPolicy = RestartPolicy.NoRestart
      };
    }).ToArray();
    var plan = ExecutionPlanner.CreatePlan(
        "developer",
        "1.0.0",
        [new ResourceGraphLayer(0, resourceIds)],
        planned,
        []);
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Apply,
      ProfileSourcePath = CanonicalProfilePath,
      ProfileId = "developer",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      StartedAtUtc = endedAt.AddMinutes(-1),
      EndedAtUtc = endedAt,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Machine = new MachineInformation("test", "x64", "test", "test"),
      Plan = plan,
      PlanApproval = new PlanApproval
      {
        InitialPlanFingerprint = plan.Fingerprint,
        ConfirmedAtUtc = endedAt.AddMinutes(-1),
        Source = PlanApprovalSource.ExplicitApplyRequest
      },
      ResourceResults = resourceIds.ToDictionary(
          id => id,
          id => new ResourceResult
          {
            ResourceId = id,
            State = ExecutionState.Completed,
            Outcome = ExecutionOutcome.Failed,
            FinalCompliance = ComplianceStatus.Missing,
            EndedAtUtc = endedAt
          },
          StringComparer.OrdinalIgnoreCase)
    };
  }

  private static ExecutionRun InterruptedRun()
  {
    var approved = FailedRun("git");
    return approved with
    {
      StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
      EndedAtUtc = null,
      State = ExecutionState.Running,
      Outcome = null,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Running,
          DetectedBefore = Missing("git")
        }
      }
    };
  }

  private static async Task<ExecutionRun> InterruptedDeferredRunAsync(
      bool afterRefinement = false)
  {
    var dependencyInstalled = false;
    var provider = DeferredProvider(
        () => dependencyInstalled,
        () => dependencyInstalled = true);
    var (service, store) = CreateService(
        provider,
        Profile(includeDependentResource: true));

    await service.ApplyAsync(Request(), CancellationToken.None);

    var approved = afterRefinement
        ? store.SavedSnapshots.First(snapshot =>
            snapshot.Plan?.Resources.Single(resource =>
                resource.Definition.Id == "dependent").Status == PlannedResourceStatus.Ready &&
            snapshot.PlanApproval?.DeferredAuthorizations.Count == 1)
        : store.SavedSnapshots[0];
    return approved with
    {
      StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
      EndedAtUtc = null,
      State = ExecutionState.Running,
      Outcome = null
    };
  }

  private static ExecutionRun RestartPendingRun()
  {
    var completedAt = DateTimeOffset.UtcNow;
    return FailedRun("git") with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = completedAt,
      RestartRequirements = [RestartPolicy.RestartRequired],
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          FinalCompliance = ComplianceStatus.Satisfied,
          EndedAtUtc = completedAt,
          RestartRequirement = RestartPolicy.RestartRequired
        }
      }
    };
  }

  private static ExecutionRun IncompleteRestartRun()
  {
    var completedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    return InterruptedRun() with
    {
      RestartRequirements = [RestartPolicy.RestartRequired],
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          FinalCompliance = ComplianceStatus.Satisfied,
          EndedAtUtc = completedAt,
          RestartRequirement = RestartPolicy.RestartRequired
        },
        ["pending"] = new()
        {
          ResourceId = "pending",
          State = ExecutionState.Running,
          DetectedBefore = Missing("pending")
        }
      }
    };
  }

  private static DetectedState Missing(string id) => new()
  {
    ResourceId = id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static DetectedState Satisfied(string id, string version)
  {
    Assert.True(SemanticVersion.TryParse(version, out var parsed));
    return new DetectedState
    {
      ResourceId = id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = version,
      InstalledVersions = [parsed]
    };
  }

  private static StructuredError ProviderError(string id, string detail) => new(
      WdemErrorCode.ProviderError,
      "Provider failed.",
      detail)
  {
    ResourceId = id,
    IsRetryable = true
  };

  private static ResourcePlan DependencyUnavailablePlan(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    ResourceType = resource.Type,
    ProviderName = resource.Provider,
    DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
    Compliance = ComplianceStatus.Missing,
    IsExecutable = false,
    StructuredErrors =
    [
      new StructuredError(
          WdemErrorCode.DependencyError,
          "Dependency is not installed yet.",
          "The declared dependency must complete before this resource can be planned.")
      {
        ResourceId = resource.Id
      }
    ]
  };

  private static ResourcePlan ExecutablePlan(
      ResourceDefinition resource,
      DetectedState state) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = state.Exists ? ComplianceStatus.Satisfied : ComplianceStatus.Missing,
        IsExecutable = true,
        Steps = state.Exists
        ? []
        :
        [
          new PlanStep
          {
            Id = $"install-{resource.Id}",
            Description = $"Install {resource.Id}",
            Action = PlanAction.Install,
            PrivilegeRequirement = resource.PrivilegeRequirement,
            RestartPolicy = resource.RestartPolicy
          }
        ]
      };

  private static ScriptedProvider DeferredProvider(
      Func<bool> dependencyInstalled,
      Action dependencyApplied,
      PrivilegeRequirement dependentPrivilege = PrivilegeRequirement.CurrentUser,
      Func<ResourceDefinition, IReadOnlyList<ProviderProgress>>? progressEventsForResource = null,
      Func<ResourceDefinition, CancellationToken, ValueTask<ResourceApplyResult>>?
          applyOperation = null) =>
      new(Missing("git"))
      {
        DetectState = resource => resource.Id == "git" && dependencyInstalled()
            ? Satisfied(resource.Id, "2.52.1")
            : Missing(resource.Id),
        PlanOperation = (resource, state) => resource.Id == "dependent" &&
            !dependencyInstalled()
                ? DependencyUnavailablePlan(resource)
                : ExecutablePlan(resource, state) with
                {
                  Steps = state.Exists
                      ? []
                      :
                      [
                        new PlanStep
                        {
                          Id = $"install-{resource.Id}",
                          Description = $"Install {resource.Id}",
                          Action = PlanAction.Install,
                          PrivilegeRequirement = resource.Id == "dependent"
                              ? dependentPrivilege
                              : resource.PrivilegeRequirement,
                          RestartPolicy = resource.RestartPolicy
                        }
                      ]
                },
        ProgressEventsForResource = progressEventsForResource,
        ApplyForResourceOperation = applyOperation ?? ((resource, _) =>
          {
            if (resource.Id == "git")
            {
              dependencyApplied();
            }

            return ValueTask.FromResult(new ResourceApplyResult
            {
              ResourceId = resource.Id,
              Outcome = ApplyOutcome.Succeeded
            });
          }),
        VerificationForResourceOperation = (resource, _) => ValueTask.FromResult(
            new VerificationResult
            {
              ResourceId = resource.Id,
              Compliance = ComplianceStatus.Satisfied,
              DetectedState = Satisfied(resource.Id, "2.52.1")
            })
      };

  private static ResourceApplyResult AppliedWithStepEvidence(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    Outcome = ApplyOutcome.Succeeded,
    StepResults =
    [
      new ProviderStepResult
      {
        StepId = $"install-{resource.Id}",
        Action = PlanAction.Install,
        Progress = 1,
        ProcessExitCode = 0,
        Succeeded = true
      }
    ]
  };

  private static string CanonicalProfilePath { get; } =
      Path.GetFullPath("input/profile.yaml");

  private sealed class FakeProfileCatalog(
      DeveloperProfile profile,
      string sourcePath) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(sourcePath, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(new ProfileLoadResult
      {
        Profile = profile,
        SourcePath = sourcePath
      });
    }

    public async Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        [await LoadFileAsync(sourcePath, cancellationToken)];
  }

  private sealed class FixedProfileCatalog(ProfileLoadResult result) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(result.SourcePath, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([result]);
  }

  private sealed class ScriptedProvider(params DetectedState[] states) : IResourceProvider
  {
    private readonly Queue<DetectedState> _states = new(states);

    public string ResourceType => "package";
    public string ProviderName => "fake";
    public ProviderCapabilities Capabilities { get; init; } = new()
    {
      MaxConcurrentOperations = 1
    };
    public int DetectCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public Func<ResourceDefinition, DetectedState>? DetectState { get; init; }
    public Func<ResourceDefinition, CancellationToken, ValueTask<DetectedState>>?
        DetectOperation
    { get; init; }
    public Func<ResourceDefinition, DetectedState, ResourcePlan>? PlanOperation { get; init; }
    public Func<CancellationToken, ValueTask<ResourceApplyResult>>? ApplyOperation { get; init; }
    public Func<ResourceDefinition, CancellationToken, ValueTask<ResourceApplyResult>>?
        ApplyForResourceOperation
    { get; init; }
    public Func<
        IProgress<ProviderProgress>?,
        CancellationToken,
        ValueTask<ResourceApplyResult>>? ApplyWithProgressOperation
    { get; init; }
    public Func<CancellationToken, ValueTask<VerificationResult>>? VerificationOperation { get; init; }
    public Func<ResourceDefinition, CancellationToken, ValueTask<VerificationResult>>?
        VerificationForResourceOperation
    { get; init; }
    public IReadOnlyList<ProviderProgress> ProgressEvents { get; init; } = [];
    public Func<ResourceDefinition, IReadOnlyList<ProviderProgress>>?
        ProgressEventsForResource
    { get; init; }
    public RestartPolicy PlannedRestartPolicy { get; init; }
    public bool IncludeExecutionPrecondition { get; init; }
    public List<string> DetectedResourceIds { get; } = [];
    public ResourceApplyResult ApplyResult { get; init; } = new()
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.Succeeded
    };
    public VerificationResult VerificationResult { get; init; } = new()
    {
      ResourceId = "git",
      Compliance = ComplianceStatus.Satisfied,
      DetectedState = new DetectedState
      {
        ResourceId = "git",
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        Version = "2.52.1",
        InstalledVersions = [new SemanticVersion(2, 52, 1)]
      }
    };

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      DetectCalls++;
      DetectedResourceIds.Add(resource.Id);
      if (DetectOperation is not null)
      {
        return DetectOperation(resource, cancellationToken);
      }

      if (DetectState is not null)
      {
        return ValueTask.FromResult(DetectState(resource));
      }

      if (_states.Count > 1)
      {
        return ValueTask.FromResult(_states.Dequeue());
      }

      return ValueTask.FromResult(_states.Peek());
    }

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken)
    {
      if (PlanOperation is not null)
      {
        return ValueTask.FromResult(PlanOperation(resource, currentState));
      }

      var detectionSucceeded = currentState.Outcome == DetectionOutcome.Succeeded;
      var satisfied = detectionSucceeded && currentState.Exists;
      return ValueTask.FromResult(new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        ExecutionPreconditionFingerprint = IncludeExecutionPrecondition
            ? new string('A', 64)
            : null,
        Compliance = !detectionSucceeded
            ? ComplianceStatus.DetectionFailed
            : satisfied ? ComplianceStatus.Satisfied : ComplianceStatus.Missing,
        IsExecutable = detectionSucceeded,
        Steps = satisfied || !detectionSucceeded
            ? []
            :
            [
              new PlanStep
              {
                Id = "install",
                Description = "Install git",
                Action = PlanAction.Install,
                PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
                RestartPolicy = PlannedRestartPolicy
              }
            ]
      });
    }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      foreach (var progressEvent in ProgressEventsForResource?.Invoke(resource) ?? ProgressEvents)
      {
        progress?.Report(progressEvent);
      }

      return ApplyForResourceOperation?.Invoke(resource, cancellationToken) ??
          ApplyWithProgressOperation?.Invoke(progress, cancellationToken) ??
          ApplyOperation?.Invoke(cancellationToken) ??
          ValueTask.FromResult(ApplyResult);
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      VerifyCalls++;
      return VerificationForResourceOperation?.Invoke(resource, cancellationToken) ??
          VerificationOperation?.Invoke(cancellationToken) ??
          ValueTask.FromResult(VerificationResult);
    }
  }

  private sealed class TransformingPlanner(
      IExecutionPlanner inner,
      Func<ExecutionPlan, ExecutionPlan> transform) : IExecutionPlanner
  {
    public async Task<ExecutionPlan> CreateAsync(
        ResourceGraph graph,
        IReadOnlyDictionary<string, DetectedState> detectedStates,
        string profileId,
        string profileVersion,
        CancellationToken cancellationToken) => transform(await inner.CreateAsync(
            graph,
        detectedStates,
        profileId,
        profileVersion,
        cancellationToken));

    public Task<PlannedResource> ReplanResourceAsync(
        ResolvedResource resource,
        DetectedState detectedState,
        string approvedDefinitionFingerprint,
        CancellationToken cancellationToken) => inner.ReplanResourceAsync(
          resource,
          detectedState,
          approvedDefinitionFingerprint,
          cancellationToken);
  }

  private sealed class ThrowingReplanPlanner(
      IExecutionPlanner inner,
      Exception exception) : IExecutionPlanner
  {
    public Task<ExecutionPlan> CreateAsync(
        ResourceGraph graph,
        IReadOnlyDictionary<string, DetectedState> detectedStates,
        string profileId,
        string profileVersion,
        CancellationToken cancellationToken) => inner.CreateAsync(
          graph,
          detectedStates,
          profileId,
          profileVersion,
          cancellationToken);

    public Task<PlannedResource> ReplanResourceAsync(
        ResolvedResource resource,
        DetectedState detectedState,
        string approvedDefinitionFingerprint,
        CancellationToken cancellationToken) => Task.FromException<PlannedResource>(exception);
  }

  private sealed class InMemoryRunStore : IExecutionRunStore
  {
    private readonly Dictionary<Guid, bool> _recoveryOperations = [];
    private readonly Dictionary<Guid, ExecutionRun> _runs;

    public InMemoryRunStore(Dictionary<Guid, ExecutionRun>? runs = null)
    {
      _runs = runs ?? [];
    }

    public IReadOnlyList<StructuredError> Diagnostics => [];
    public List<ExecutionRun> SavedSnapshots { get; } = [];
    public Func<ExecutionRun, CancellationToken, Task>? SaveOperation { get; init; }
    public Func<Guid, RunLogEntry, CancellationToken, Task>? AppendLogOperation { get; set; }
    public Func<Guid, ApprovedResourceSeal, CancellationToken, Task>? SealOperation { get; init; }

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _runs.Add(run.RunId, run);
      SavedSnapshots.Add(run);
      return Task.CompletedTask;
    }

    public Task CreateAsync(
        ExecutionRun run,
        IReadOnlyList<ApprovedResourceSeal> approvedResources,
        CancellationToken cancellationToken) => CreateAsync(run, cancellationToken);

    public Task SealApprovedResourceAsync(
        Guid runId,
        ApprovedResourceSeal approvedResource,
        CancellationToken cancellationToken) => SealOperation?.Invoke(
            runId,
            approvedResource,
            cancellationToken) ?? Task.CompletedTask;

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(_runs.GetValueOrDefault(runId));
    }

    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      IReadOnlyList<ExecutionRun> result = _runs.Values
          .Where(run => run.State != ExecutionState.Completed)
          .ToArray();
      return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult<IReadOnlyList<ExecutionRun>>(_runs.Values.ToArray());
    }

    public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock (_recoveryOperations)
      {
        if (_recoveryOperations.ContainsKey(runId))
        {
          return Task.FromResult<IAsyncDisposable?>(null);
        }

        _recoveryOperations.Add(runId, true);
        return Task.FromResult<IAsyncDisposable?>(
            new AsyncAction(() =>
            {
              lock (_recoveryOperations)
              {
                _recoveryOperations.Remove(runId);
              }
            }));
      }
    }

    public async Task<ExecutionRun> SaveAsync(
        ExecutionRun run,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (SaveOperation is not null)
      {
        await SaveOperation(run, cancellationToken);
      }

      lock (_runs)
      {
        if (!_runs.TryGetValue(run.RunId, out var current) ||
            current.Revision != run.Revision ||
            current.RecoveryClaimId != run.RecoveryClaimId)
        {
          throw new InvalidOperationException("The execution run snapshot is stale.");
        }

        var saved = run with { Revision = checked(run.Revision + 1) };
        _runs[run.RunId] = saved;
        SavedSnapshots.Add(saved);
        return saved;
      }
    }

    public Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock (_runs)
      {
        if (!_runs.TryGetValue(run.RunId, out var current))
        {
          throw new KeyNotFoundException(
              $"Execution run '{run.RunId:D}' does not exist.");
        }

        if (current.Revision != expectedRevision ||
            current.RecoveryClaimId != expectedRecoveryClaimId)
        {
          return Task.FromResult(false);
        }

        _runs[run.RunId] = run;
        SavedSnapshots.Add(run);
        return Task.FromResult(true);
      }
    }

    public async Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken)
    {
      if (AppendLogOperation is not null)
      {
        await AppendLogOperation(runId, entry, cancellationToken);
      }
    }

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunLogEntry>>([]);

    private sealed class AsyncAction(Action action) : IAsyncDisposable
    {
      public ValueTask DisposeAsync()
      {
        action();
        return ValueTask.CompletedTask;
      }
    }
  }

  private sealed class LateCancellingEventSink(CancellationTokenSource cancellation)
      : IRunEventSink, IDisposable
  {
    private readonly RunEventHub _inner = new();

    public IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer) =>
        _inner.Subscribe(observer);

    public IDisposable SubscribeRequired(Func<RunEvent, CancellationToken, Task> observer) =>
        _inner.SubscribeRequired(observer);

    public IDisposable SubscribeScoped(Func<RunEvent, CancellationToken, Task> observer) =>
        _inner.SubscribeScoped(observer);

    public IDisposable SubscribeRequiredScoped(
        Func<RunEvent, CancellationToken, Task> observer) =>
        _inner.SubscribeRequiredScoped(observer);

    public void BindCurrentScopeToRun(Guid runId) => _inner.BindCurrentScopeToRun(runId);

    public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
      if (runEvent.ResourceId == "dependent" &&
          runEvent.Kind == RunEventKind.StepProgress)
      {
        cancellation.Cancel();
        return Task.FromCanceled(cancellation.Token);
      }

      return _inner.PublishAsync(runEvent, cancellationToken);
    }

    public void Dispose() => _inner.Dispose();
  }

  private sealed class CleanupFailingDispatcher : IResourceApplyDispatcher
  {
    private readonly DirectResourceApplyDispatcher _direct = new();

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => _direct.ApplyAsync(
            runId,
            provider,
            resource,
            plan,
            progress,
            cancellationToken);

    public Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromException(new IOException("elevated cleanup unavailable"));
  }

  private sealed class BlockingCleanupDispatcher : IResourceApplyDispatcher
  {
    private readonly DirectResourceApplyDispatcher _direct = new();

    public TaskCompletionSource CleanupStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCleanup { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationDrainDeadline? ApplyDeadline { get; private set; }
    public CancellationToken CleanupToken { get; private set; }

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => _direct.ApplyAsync(
            runId,
            provider,
            resource,
            plan,
            progress,
            cancellationToken);

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken,
        CancellationDrainDeadline? cancellationDeadline)
    {
      ApplyDeadline = cancellationDeadline;
      return ApplyAsync(
          runId,
          provider,
          resource,
          plan,
          progress,
          cancellationToken);
    }

    public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
      CleanupToken = cancellationToken;
      CleanupStarted.TrySetResult();
      await ReleaseCleanup.Task;
    }
  }

  private sealed class LegacyCompatibleScheduler : IResourceScheduler
  {
    private readonly ResourceScheduler _inner = new();

    public bool ExecuteCalled { get; private set; }

    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null)
    {
      ExecuteCalled = true;
      return _inner.ExecuteAsync(
          plan,
          executeAsync,
          capabilitiesFor,
          maximumConcurrency,
          cancellationToken,
          transitionAsync);
    }
  }

  private sealed class LateTransitionScheduler(ResourceResult lateResult) : IResourceScheduler
  {
    public TaskCompletionSource ReleaseLateTransition { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public Task TransitionCompletion { get; private set; } = Task.CompletedTask;

    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null)
    {
      TransitionCompletion = PublishLateTransitionAsync(transitionAsync);
      IReadOnlyDictionary<string, ResourceResult> results =
          new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
          {
            [lateResult.ResourceId] = lateResult with
            {
              Outcome = ExecutionOutcome.Cancelled,
              Error = new StructuredError(
                  WdemErrorCode.CancellationError,
                  "Resource execution was cancelled.",
                  "The cancellation drain deadline elapsed.")
              {
                ResourceId = lateResult.ResourceId
              }
            }
          };
      return Task.FromResult(new SchedulerResult
      {
        Results = results,
        UndrainedCompletion = TransitionCompletion
      });
    }

    private async Task PublishLateTransitionAsync(
        Func<ResourceResult, Task>? transitionAsync)
    {
      await ReleaseLateTransition.Task;
      if (transitionAsync is not null)
      {
        await transitionAsync(lateResult);
      }
    }
  }

  private sealed class EvictedFirstFinalizationScheduler(
      ResourceResult lateResult,
      Exception finalizationFault) : IResourceScheduler
  {
    private int _calls;

    public TaskCompletionSource ReleaseFirstFinalization { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstTransitionPublished { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstFinalizationFaulted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public WeakReference<Task> FirstFinalization { get; private set; } = new(
        Task.CompletedTask);

    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null)
    {
      Task finalization;
      if (Interlocked.Increment(ref _calls) == 1)
      {
        finalization = PublishFirstLateTransitionAndFaultAsync(transitionAsync);
        FirstFinalization = new WeakReference<Task>(finalization);
      }
      else
      {
        finalization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
      }

      return Task.FromResult(new SchedulerResult
      {
        Results = plan.Resources.ToDictionary(
            resource => resource.Definition.Id,
            resource => new ResourceResult
            {
              ResourceId = resource.Definition.Id,
              State = ExecutionState.Completed,
              Outcome = ExecutionOutcome.Cancelled,
              EndedAtUtc = DateTimeOffset.UtcNow
            },
            StringComparer.OrdinalIgnoreCase),
        UndrainedCompletion = finalization
      });
    }

    private async Task PublishFirstLateTransitionAndFaultAsync(
        Func<ResourceResult, Task>? transitionAsync)
    {
      await ReleaseFirstFinalization.Task;
      if (transitionAsync is not null)
      {
        await transitionAsync(lateResult);
      }

      FirstTransitionPublished.TrySetResult();
      FirstFinalizationFaulted.TrySetResult();
      throw finalizationFault;
    }
  }

  private sealed class FinalizationOnlyScheduler(Task finalization) : IResourceScheduler
  {
    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null) => Task.FromResult(
          new SchedulerResult
          {
            Results = plan.Resources.ToDictionary(
                resource => resource.Definition.Id,
                resource => new ResourceResult
                {
                  ResourceId = resource.Definition.Id,
                  State = ExecutionState.Completed,
                  Outcome = ExecutionOutcome.Cancelled,
                  EndedAtUtc = DateTimeOffset.UtcNow
                },
                StringComparer.OrdinalIgnoreCase),
            UndrainedCompletion = finalization
          });
  }

  private sealed class BoundedFinalizationScheduler(
      int pendingFinalizationCount,
      ResourceResult? lateResult) : IResourceScheduler
  {
    private int _calls;

    public TaskCompletionSource ReleaseLateTransition { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public Task LastFinalization { get; private set; } = Task.CompletedTask;

    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null)
    {
      var call = Interlocked.Increment(ref _calls);
      var finalization = call <= pendingFinalizationCount
          ? new TaskCompletionSource(
              TaskCreationOptions.RunContinuationsAsynchronously).Task
          : PublishLateTransitionAsync(transitionAsync);
      LastFinalization = finalization;
      return Task.FromResult(new SchedulerResult
      {
        Results = plan.Resources.ToDictionary(
            resource => resource.Definition.Id,
            resource => new ResourceResult
            {
              ResourceId = resource.Definition.Id,
              State = ExecutionState.Completed,
              Outcome = ExecutionOutcome.Cancelled,
              EndedAtUtc = DateTimeOffset.UtcNow
            },
            StringComparer.OrdinalIgnoreCase),
        UndrainedCompletion = finalization
      });
    }

    private async Task PublishLateTransitionAsync(
        Func<ResourceResult, Task>? transitionAsync)
    {
      await ReleaseLateTransition.Task;
      if (transitionAsync is not null && lateResult is not null)
      {
        await transitionAsync(lateResult);
      }
    }
  }

  private sealed class CompletedFinalizationRegisteringScheduler : IResourceScheduler
  {
    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync = null) => ExecuteAsync(
            plan,
            executeAsync,
            capabilitiesFor,
            maximumConcurrency,
            cancellationToken,
            transitionAsync,
            cancellationDeadline: null,
            registerUndrainedCompletion: null);

    public Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        Func<ResourceResult, Task>? transitionAsync,
        CancellationDrainDeadline? cancellationDeadline,
        Action<Task>? registerUndrainedCompletion)
    {
      registerUndrainedCompletion?.Invoke(Task.CompletedTask);
      var resourceId = Assert.Single(plan.Resources).Definition.Id;
      return Task.FromResult(new SchedulerResult
      {
        Results = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
        {
          [resourceId] = new ResourceResult
          {
            ResourceId = resourceId,
            State = ExecutionState.Completed,
            Outcome = ExecutionOutcome.Cancelled,
            EndedAtUtc = DateTimeOffset.UtcNow
          }
        },
        UndrainedCompletion = Task.CompletedTask
      });
    }
  }
}
