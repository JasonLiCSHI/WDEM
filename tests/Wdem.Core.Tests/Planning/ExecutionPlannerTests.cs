using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Xunit;

namespace Wdem.Core.Tests.Planning;

public sealed class ExecutionPlannerTests
{
  [Fact]
  public async Task CreateAsync_SatisfiedResource_IsAlreadySatisfiedWithoutApply()
  {
    var planner = Planner(new StubProvider());

    var plan = await planner.CreateAsync(
        Graph(Resource("git")),
        States(State("git", exists: true)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var resource = Assert.Single(plan.Resources);
    Assert.Equal(PlannedResourceStatus.AlreadySatisfied, resource.Status);
    Assert.Equal(ComplianceStatus.Satisfied, resource.ResourcePlan.Compliance);
    Assert.Equal(PlanAction.None, Assert.Single(resource.ResourcePlan.Steps).Action);
    Assert.False(resource.ResourcePlan.RequiresApply);
    Assert.True(plan.IsExecutable);
  }

  [Fact]
  public async Task CreateAsync_SameEffectiveInput_HasStablePlanIdentityAndOrdering()
  {
    var firstResource = Resource("tool", dependencies: ["runtime"]);
    var secondResource = Resource("runtime");
    var provider = new StubProvider();
    var planner = Planner(provider);
    var firstGraph = Graph([secondResource], [firstResource]);
    var secondGraph = Graph(
        new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
        {
          ["tool"] = firstResource,
          ["runtime"] = secondResource
        },
        [new ResourceGraphLayer(0, ["runtime"]), new ResourceGraphLayer(1, ["tool"])]);

    var first = await planner.CreateAsync(
        firstGraph,
        States(State("runtime", false), State("tool", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);
    var second = await planner.CreateAsync(
        secondGraph,
        States(State("tool", false), State("runtime", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal(first.PlanId, second.PlanId);
    Assert.Equal(first.Fingerprint, second.Fingerprint);
    Assert.Equal(["runtime", "tool"], first.Resources.Select(item => item.Definition.Id));
    Assert.Equal(
        first.Resources.Select(item => item.ResourcePlan.DesiredStateFingerprint),
        second.Resources.Select(item => item.ResourcePlan.DesiredStateFingerprint));
  }

  [Fact]
  public async Task CreateAsync_ModifyingPlan_ExposesAuditAndRiskMetadata()
  {
    var resource = Resource("sdk") with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      RestartPolicy = RestartPolicy.RestartRecommended
    };
    var provider = new StubProvider(plan: (definition, _) => ValidPlan(
        definition,
        ComplianceStatus.Missing,
        new PlanStep
        {
          Id = "install",
          Description = "Install SDK",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.RestartRecommended,
          IsDestructive = true,
          Reason = "Replaces an existing preview installation"
        }));

    var plan = await Planner(provider).CreateAsync(
        Graph(resource),
        States(State("sdk", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var item = Assert.Single(plan.Resources);
    Assert.Equal(PlannedResourceStatus.Ready, item.Status);
    Assert.True(item.RequiresElevation);
    Assert.True(item.IsDestructive);
    Assert.Equal(PlanRisk.Destructive, item.Risk);
    Assert.Equal("Replaces an existing preview installation", item.Reason);
    Assert.Equal(ResourceOrigin.Required, item.Origin);
    Assert.Equal("test", item.ResourcePlan.ProviderName);
    Assert.Equal(PlanAction.Install, Assert.Single(item.ResourcePlan.Steps).Action);
    Assert.Equal(RestartPolicy.RestartRecommended, item.RestartPolicy);
  }

  [Theory]
  [InlineData(DetectionOutcome.Unsupported, ComplianceStatus.Unsupported, PlannedResourceStatus.Unsupported)]
  [InlineData(DetectionOutcome.Failed, ComplianceStatus.DetectionFailed, PlannedResourceStatus.DetectionFailed)]
  public async Task CreateAsync_NonRemediableDetectionState_IsNotExecutable(
      DetectionOutcome outcome,
      ComplianceStatus compliance,
      PlannedResourceStatus expectedStatus)
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(resource, compliance));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with { Outcome = outcome }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(expectedStatus, Assert.Single(plan.Resources).Status);
    Assert.NotEmpty(plan.Errors);
  }

  [Fact]
  public async Task CreateAsync_MissingProvider_MakesWholePlanNonExecutable()
  {
    var plan = await Planner().CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    var error = Assert.Single(plan.Errors);
    Assert.Equal(WdemErrorCode.ProviderError, error.Code);
    Assert.Equal("git", error.ResourceId);
  }

  [Fact]
  public async Task CreateAsync_InvalidProviderParameters_PreservesStructuredDiagnostics()
  {
    var expected = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Invalid source",
        "The requested source is not trusted.")
    {
      ResourceId = "git",
      SuggestedAction = "Choose a trusted source."
    };
    var provider = new StubProvider(validation: _ => ProviderValidationResult.Invalid(expected));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(expected, Assert.Single(plan.Errors));
    Assert.Equal(expected, Assert.Single(plan.Resources).Diagnostics.Single());
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_LegacyValidationText_IsRedacted()
  {
    var provider = new StubProvider(validation: _ => ProviderValidationResult.Invalid(
        "download token=top-secret is rejected"));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.DoesNotContain("top-secret", Assert.Single(plan.Errors).Detail, StringComparison.Ordinal);
    Assert.Contains("[REDACTED]", Assert.Single(plan.Errors).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_MixedValidationDiagnostics_AreMergedAndDeduplicated()
  {
    var provider = new StubProvider(validation: resource => new ProviderValidationResult
    {
      Errors = ["shared failure", "legacy-only failure", "legacy-only failure"],
      StructuredErrors =
      [
        new StructuredError(
            WdemErrorCode.ConfigurationError,
            "Structured validation failed.",
            "shared failure")
        {
          ResourceId = resource.Id
        },
        new StructuredError(
            WdemErrorCode.ProviderError,
            "Structured validation failed.",
            "structured-only failure")
        {
          ResourceId = resource.Id
        }
      ]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(3, plan.Errors.Count);
    Assert.Single(plan.Errors, error => error.Detail == "shared failure");
    Assert.Single(plan.Errors, error => error.Detail == "structured-only failure");
    Assert.Single(plan.Errors, error => error.Detail == "legacy-only failure");
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_CombinedValidationDiagnostics_EnforceSingleLimit()
  {
    var provider = new StubProvider(validation: resource => new ProviderValidationResult
    {
      Errors = Enumerable.Range(0, 60).Select(index => $"legacy-{index}").ToArray(),
      StructuredErrors = Enumerable.Range(0, 60).Select(index => new StructuredError(
          WdemErrorCode.ConfigurationError,
          "Structured validation failed.",
          $"structured-{index}")
      {
        ResourceId = resource.Id
      }).ToArray()
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var error = Assert.Single(plan.Errors);
    Assert.Equal(WdemErrorCode.ProviderError, error.Code);
    Assert.Contains("limit", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_SecretParameters_AreAvailableToProviderButRedactedFromPlan()
  {
    string? providerValue = null;
    var provider = new StubProvider(plan: (resource, _) =>
    {
      providerValue = resource.Parameters["access_token"];
      return ValidPlan(resource, ComplianceStatus.Missing, Step("install"));
    });
    var resource = Resource("git") with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["access_token"] = "provider-secret",
        ["channel"] = "stable"
      }
    };

    var plan = await Planner(provider).CreateAsync(
        Graph(resource),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal("provider-secret", providerValue);
    var planned = Assert.Single(plan.Resources);
    Assert.Equal("[REDACTED]", planned.Definition.Parameters["access_token"]);
    Assert.Equal("stable", planned.Definition.Parameters["channel"]);
    Assert.DoesNotContain("provider-secret", plan.Fingerprint, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_NullProviderPlan_IsRejected()
  {
    var provider = new StubProvider(plan: (_, _) => null!);

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_ProviderException_IsRedactedAndCaptured()
  {
    var provider = new StubProvider(plan: (_, _) => throw new InvalidOperationException(
        "request password=hunter2 failed"));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var error = Assert.Single(plan.Errors);
    Assert.Equal(WdemErrorCode.ProviderError, error.Code);
    Assert.DoesNotContain("hunter2", error.Detail, StringComparison.Ordinal);
    Assert.Contains("[REDACTED]", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_CallerCancellation_IsPropagated()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planner(new StubProvider())
        .CreateAsync(
            Graph(Resource("git")),
            States(State("git", false)),
            "developer",
            "1.0.0",
            cancellation.Token));
  }

  [Fact]
  public async Task CreateAsync_SpuriousProviderCancellation_IsProviderError()
  {
    var provider = new StubProvider(plan: (_, _) => throw new OperationCanceledException(
        "provider cancelled itself"));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Theory]
  [InlineData("other", null, "test")]
  [InlineData("git", "BAD-FINGERPRINT", "test")]
  [InlineData("git", null, "other")]
  public async Task CreateAsync_MalformedProviderIdentity_IsRejected(
      string resourceId,
      string? fingerprint,
      string providerName)
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install")) with
    {
      ResourceId = resourceId,
      DesiredStateFingerprint = fingerprint ?? ResourceDefinitionFingerprint.Create(resource),
      ProviderName = providerName
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_DuplicateStepIds_AreRejectedCaseInsensitively()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install"),
        Step("INSTALL")));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Contains("duplicate", Assert.Single(plan.Errors).Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(ComplianceStatus.Satisfied, PlanAction.Install)]
  [InlineData(ComplianceStatus.Unsupported, PlanAction.Install)]
  [InlineData(ComplianceStatus.DetectionFailed, PlanAction.Configure)]
  public async Task CreateAsync_NonRemediableStatusWithModifyingStep_IsRejected(
      ComplianceStatus compliance,
      PlanAction action)
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        compliance,
        Step("invalid") with { Action = action }));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", compliance == ComplianceStatus.Satisfied)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, Assert.Single(plan.Resources).Status);
  }

  [Fact]
  public async Task CreateAsync_FailedDependency_BlocksAllTransitiveDependents()
  {
    var provider = new StubProvider(validation: resource => resource.Id == "runtime"
        ? ProviderValidationResult.Invalid("runtime source unavailable")
        : ProviderValidationResult.Valid);
    var graph = Graph(
        [Resource("runtime")],
        [Resource("sdk", dependencies: ["runtime"])],
        [Resource("ide", dependencies: ["sdk"])]);

    var plan = await Planner(provider).CreateAsync(
        graph,
        States(State("runtime", false), State("sdk", false), State("ide", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, plan.Resources[0].Status);
    Assert.Equal(PlannedResourceStatus.Blocked, plan.Resources[1].Status);
    Assert.Equal(["runtime"], plan.Resources[1].BlockedBy);
    Assert.Equal(PlannedResourceStatus.Blocked, plan.Resources[2].Status);
    Assert.Equal(["sdk"], plan.Resources[2].BlockedBy);
  }

  [Fact]
  public async Task CreateAsync_PreservesGraphLayerOrderAndDeepSnapshotsInputs()
  {
    var dependencies = new List<string> { "runtime" };
    var parameters = new Dictionary<string, string?> { ["channel"] = "stable" };
    var steps = new List<PlanStep> { Step("install") };
    var runtime = Resource("runtime");
    var tool = Resource("tool", dependencies) with { Parameters = parameters };
    var provider = new StubProvider(plan: (resource, _) => new ResourcePlan
    {
      ResourceId = resource.Id,
      ResourceType = resource.Type,
      ProviderName = resource.Provider,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps = steps
    });

    var plan = await Planner(provider).CreateAsync(
        Graph([runtime], [tool]),
        States(State("runtime", false), State("tool", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);
    dependencies.Clear();
    parameters["channel"] = "preview";
    steps.Clear();

    Assert.Equal([["runtime"], ["tool"]], plan.Layers.Select(layer => layer.ResourceIds));
    Assert.Equal(["runtime"], plan.Resources[1].Dependencies);
    Assert.Equal("stable", plan.Resources[1].Definition.Parameters["channel"]);
    Assert.Single(plan.Resources[1].ResourcePlan.Steps);
    AssertCannotMutateList(plan.Resources, plan.Resources[0]);
    AssertCannotMutateList(plan.Layers, new ResourceGraphLayer(9, ["injected"]));
    AssertCannotMutateList(plan.Errors, new StructuredError(
        WdemErrorCode.ProviderError,
        "injected",
        "injected"));
  }

  [Fact]
  public async Task CreateAsync_ExcessiveResourceCount_IsRejectedBeforeCallingProviders()
  {
    var provider = new StubProvider();
    var resources = Enumerable.Range(0, ExecutionPlanner.MaxResourceCount + 1)
        .Select(index => Resource($"resource-{index:D5}"))
        .ToArray();
    var graph = Graph(resources);

    var plan = await Planner(provider).CreateAsync(
        graph,
        resources.ToDictionary(
            resource => resource.Id,
            resource => State(resource.Id, false),
            StringComparer.OrdinalIgnoreCase),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Empty(plan.Resources);
    Assert.Equal(0, provider.PlanCalls);
    Assert.Equal(WdemErrorCode.DependencyError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_ExcessiveSteps_IsRejected()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Enumerable.Range(0, ExecutionPlanner.MaxStepsPerResource + 1)
            .Select(index => Step($"step-{index}"))
            .ToArray()));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
    Assert.Empty(Assert.Single(plan.Resources).ResourcePlan.Steps);
  }

  [Fact]
  public async Task CreateAsync_DetectedStateForDifferentResource_IsRejectedBeforePlanning()
  {
    var provider = new StubProvider();

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        new Dictionary<string, DetectedState>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = State("other-resource", false)
        },
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DetectionError, Assert.Single(plan.Errors).Code);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_CaseSensitiveDetectedStateDictionary_IsNormalized()
  {
    var provider = new StubProvider();
    var states = new Dictionary<string, DetectedState>(StringComparer.Ordinal)
    {
      ["GIT"] = State("git", false)
    };

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        states,
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.True(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Ready, Assert.Single(plan.Resources).Status);
    Assert.Equal(1, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_CaseInsensitiveDuplicateDetectedStateKeys_AreRejected()
  {
    var provider = new StubProvider();
    var states = new Dictionary<string, DetectedState>(StringComparer.Ordinal)
    {
      ["git"] = State("git", false),
      ["GIT"] = State("GIT", false)
    };

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        states,
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Empty(plan.Resources);
    Assert.Contains("duplicate", Assert.Single(plan.Errors).Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_AnyDetectedStateKeyValueIdentityMismatch_IsRejectedAtBoundary()
  {
    var provider = new StubProvider();
    var states = new Dictionary<string, DetectedState>(StringComparer.Ordinal)
    {
      ["git"] = State("git", false),
      ["unrelated-key"] = State("different-resource", false)
    };

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        states,
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Empty(plan.Resources);
    Assert.Contains("identity", Assert.Single(plan.Errors).Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Theory]
  [InlineData(DetectionOutcome.Failed, ComplianceStatus.Missing, PlannedResourceStatus.DetectionFailed)]
  [InlineData(DetectionOutcome.Cancelled, ComplianceStatus.Satisfied, PlannedResourceStatus.DetectionFailed)]
  [InlineData(DetectionOutcome.Unsupported, ComplianceStatus.Missing, PlannedResourceStatus.Unsupported)]
  public async Task CreateAsync_FailedDetectionCannotBeOverriddenByProviderPlan(
      DetectionOutcome outcome,
      ComplianceStatus providerCompliance,
      PlannedResourceStatus expectedStatus)
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        providerCompliance,
        providerCompliance == ComplianceStatus.Missing ? Step("install") :
            Step("none") with { Action = PlanAction.None }));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with { Outcome = outcome }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(expectedStatus, Assert.Single(plan.Resources).Status);
    Assert.Equal(WdemErrorCode.DetectionError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_UnknownDetectionOutcome_IsRejected()
  {
    var plan = await Planner(new StubProvider()).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with { Outcome = (DetectionOutcome)999 }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_UnknownProviderEnums_AreRejectedWithoutRetainingSteps()
  {
    var providers = new[]
    {
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          (ComplianceStatus)999,
          Step("install"))),
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("install") with { Action = (PlanAction)999 })),
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("install") with { PrivilegeRequirement = (PrivilegeRequirement)999 })),
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("install") with { RestartPolicy = (RestartPolicy)999 }))
    };

    foreach (var provider in providers)
    {
      var plan = await Planner(provider).CreateAsync(
          Graph(Resource("git")),
          States(State("git", false)),
          "developer",
          "1.0.0",
          CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
      Assert.Empty(Assert.Single(plan.Resources).ResourcePlan.Steps);
    }
  }

  [Fact]
  public async Task CreateAsync_UnknownDefinitionEnums_AreRejectedBeforeProviderCall()
  {
    var provider = new StubProvider();
    var resource = Resource("git") with
    {
      PrivilegeRequirement = (PrivilegeRequirement)999,
      RestartPolicy = (RestartPolicy)999
    };

    var plan = await Planner(provider).CreateAsync(
        Graph(resource),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_UnknownResourceOrigin_IsRejectedBeforeProviderCall()
  {
    var provider = new StubProvider();
    var resource = Resource("git");
    var graph = new ResourceGraph(
        new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = new(resource, (ResourceOrigin)999, new HashSet<string>())
        },
        [new ResourceGraphLayer(0, ["git"])]);

    var plan = await Planner(provider).CreateAsync(
        graph,
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
    Assert.Equal(0, provider.PlanCalls);
  }

  [Fact]
  public async Task CreateAsync_ExecutablePlanWithErrors_IsInvalidAndBlocksDependent()
  {
    var providerDiagnostic = new StructuredError(
        WdemErrorCode.DownloadError,
        "Source rejected",
        "Source trust validation failed.")
    {
      ResourceId = "runtime"
    };
    var provider = new StubProvider(plan: (resource, _) => resource.Id == "runtime"
        ? ValidPlan(resource, ComplianceStatus.Missing, Step("install")) with
        {
          IsExecutable = true,
          Error = "source is unavailable",
          StructuredErrors = [providerDiagnostic]
        }
        : ValidPlan(resource, ComplianceStatus.Missing, Step("install")));

    var plan = await Planner(provider).CreateAsync(
        Graph(
            [Resource("runtime")],
            [Resource("tool", dependencies: ["runtime"])]),
        States(State("runtime", false), State("tool", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, plan.Resources[0].Status);
    Assert.False(plan.Resources[0].ResourcePlan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Blocked, plan.Resources[1].Status);
  }

  [Theory]
  [InlineData(
      DetectionOutcome.Failed,
      ComplianceStatus.DetectionFailed,
      PlannedResourceStatus.DetectionFailed,
      WdemErrorCode.DetectionError)]
  [InlineData(
      DetectionOutcome.Unsupported,
      ComplianceStatus.Unsupported,
      PlannedResourceStatus.Unsupported,
      WdemErrorCode.ProviderError)]
  [InlineData(
      DetectionOutcome.Cancelled,
      ComplianceStatus.DetectionFailed,
      PlannedResourceStatus.DetectionFailed,
      WdemErrorCode.CancellationError)]
  public async Task CreateAsync_NonExecutableTerminalPlanWithErrors_PreservesStatusAndDiagnostics(
      DetectionOutcome outcome,
      ComplianceStatus compliance,
      PlannedResourceStatus expectedStatus,
      WdemErrorCode errorCode)
  {
    var sourceDiagnostic = new StructuredError(
        errorCode,
        "Detection did not succeed.",
        "token=terminal-secret unavailable")
    {
      ResourceId = "runtime"
    };
    var provider = new StubProvider(plan: (resource, _) => resource.Id == "runtime"
        ? ValidPlan(resource, compliance) with
        {
          IsExecutable = false,
          Error = "token=terminal-secret unavailable",
          StructuredErrors = [sourceDiagnostic]
        }
        : ValidPlan(resource, ComplianceStatus.Missing, Step("install")));

    var plan = await Planner(provider).CreateAsync(
        Graph(
            [Resource("runtime")],
            [Resource("tool", dependencies: ["runtime"])]),
        States(
            State("runtime", false) with { Outcome = outcome },
            State("tool", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    var terminal = plan.Resources[0];
    Assert.Equal(expectedStatus, terminal.Status);
    Assert.Equal(compliance, terminal.ResourcePlan.Compliance);
    Assert.False(terminal.ResourcePlan.IsExecutable);
    var diagnostic = Assert.Single(terminal.Diagnostics);
    Assert.Equal(errorCode, diagnostic.Code);
    Assert.NotSame(sourceDiagnostic, diagnostic);
    Assert.DoesNotContain("terminal-secret", diagnostic.Detail, StringComparison.Ordinal);
    Assert.Equal(PlannedResourceStatus.Blocked, plan.Resources[1].Status);
    Assert.Equal(["runtime"], plan.Resources[1].BlockedBy);
  }

  [Fact]
  public async Task CreateAsync_ExecutableTerminalPlanWithErrors_IsMalformedAndInvalid()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.DetectionFailed) with
    {
      IsExecutable = true,
      Error = "detection failed",
      StructuredErrors =
      [
        new StructuredError(
            WdemErrorCode.DetectionError,
            "Detection failed.",
            "detection failed")
        {
          ResourceId = resource.Id
        }
      ]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with { Outcome = DetectionOutcome.Failed }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, Assert.Single(plan.Resources).Status);
    Assert.Contains(
        plan.Errors,
        error => error.Detail.Contains("executable", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task CreateAsync_NonExecutableRemediablePlanWithErrors_RemainsInvalid()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing) with
    {
      IsExecutable = false,
      Error = "source unavailable"
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, Assert.Single(plan.Resources).Status);
    Assert.Contains(plan.Errors, error => error.Detail == "source unavailable");
  }

  [Fact]
  public async Task CreateAsync_NoneStepsAndDeclarations_DoNotRaiseRuntimeRisk()
  {
    var resource = Resource("git") with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      RestartPolicy = RestartPolicy.RestartRequired
    };
    var provider = new StubProvider(plan: (definition, _) => ValidPlan(
        definition,
        ComplianceStatus.Satisfied,
        Step("none") with
        {
          Action = PlanAction.None,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.RestartRequired,
          IsDestructive = true
        }));

    var plan = await Planner(provider).CreateAsync(
        Graph(resource),
        States(State("git", true)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var item = Assert.Single(plan.Resources);
    Assert.Equal(PlannedResourceStatus.AlreadySatisfied, item.Status);
    Assert.False(item.RequiresElevation);
    Assert.False(item.IsDestructive);
    Assert.Equal(PlanRisk.None, item.Risk);
    Assert.Equal(RestartPolicy.NoRestart, item.RestartPolicy);
    Assert.Equal(PrivilegeRequirement.Administrator, item.Definition.PrivilegeRequirement);
  }

  [Fact]
  public async Task CreateAsync_ReorderedSameLayerAndDependencies_HasCanonicalIdentity()
  {
    var alpha = Resource("alpha");
    var beta = Resource("Beta");
    var toolFirst = Resource("tool", dependencies: ["Beta", "alpha"]);
    var toolSecond = Resource("tool", dependencies: ["alpha", "Beta"]);
    var first = await Planner(new StubProvider()).CreateAsync(
        Graph([beta, alpha], [toolFirst]),
        States(State("tool", false), State("Beta", false), State("alpha", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);
    var second = await Planner(new StubProvider()).CreateAsync(
        Graph([alpha, beta], [toolSecond]),
        States(State("alpha", false), State("Beta", false), State("tool", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal(first.PlanId, second.PlanId);
    Assert.Equal(first.Fingerprint, second.Fingerprint);
    Assert.Equal(["alpha", "Beta", "tool"], first.Resources.Select(item => item.Definition.Id));
    Assert.Equal(["alpha", "Beta"], first.Resources[2].Dependencies);
    Assert.Equal(first.Layers, second.Layers, ResourceGraphLayerComparer.Instance);
  }

  [Fact]
  public async Task CreateAsync_DiagnosticIdentityMismatch_IsMalformedProviderResult()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install")) with
    {
      IsExecutable = false,
      StructuredErrors =
      [
        new StructuredError(WdemErrorCode.DownloadError, "failed", "failed")
        {
          ResourceId = "other",
          StepId = "missing-step"
        }
      ]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var error = Assert.Single(plan.Errors);
    Assert.Equal(WdemErrorCode.ProviderError, error.Code);
    Assert.Contains("identity", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(Assert.Single(plan.Resources).ResourcePlan.Steps);
  }

  [Fact]
  public async Task CreateAsync_DiagnosticWithUnknownErrorCode_IsMalformedProviderResult()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install")) with
    {
      IsExecutable = false,
      StructuredErrors =
      [
        new StructuredError((WdemErrorCode)999, "failed", "failed")
        {
          ResourceId = "git",
          StepId = "install"
        }
      ]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_ProviderDiagnostic_IsClonedAndAllTextIsSanitized()
  {
    var diagnostic = new StructuredError(
        WdemErrorCode.DownloadError,
        "Download\r\nfailed",
        "token=diagnostic-secret\u0001 failed")
    {
      ResourceId = "git",
      StepId = "install",
      ProcessExitCode = 42,
      LogLocation = @"C:\Users\Alice\wdem.log",
      SuggestedAction = "use password=diagnostic-password",
      IsRetryable = true,
      UnderlyingException = new InvalidOperationException("token=exception-secret")
    };
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install")) with
    {
      IsExecutable = false,
      StructuredErrors = [diagnostic]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var error = Assert.Single(plan.Resources).Diagnostics
        .Single(item => item.Code == WdemErrorCode.DownloadError);
    Assert.NotSame(diagnostic, error);
    var visible = string.Join('|',
        error.Summary,
        error.Detail,
        error.LogLocation,
        error.SuggestedAction);
    Assert.DoesNotContain("diagnostic-secret", visible, StringComparison.Ordinal);
    Assert.DoesNotContain("diagnostic-password", visible, StringComparison.Ordinal);
    Assert.DoesNotContain("Alice", visible, StringComparison.Ordinal);
    Assert.DoesNotContain("exception-secret", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain('\r', visible);
    Assert.DoesNotContain('\n', visible);
    Assert.DoesNotContain('\u0001', visible);
    Assert.Equal(42, error.ProcessExitCode);
    Assert.True(error.IsRetryable);
    Assert.Equal(typeof(InvalidOperationException).FullName, error.UnderlyingExceptionType);
    Assert.Null(error.UnderlyingException);
  }

  [Fact]
  public async Task CreateAsync_FailedDetection_PreservesSanitizedStructuredDiagnostic()
  {
    var source = new StructuredError(
        WdemErrorCode.PermissionError,
        "Denied",
        "password=detection-secret")
    {
      ResourceId = "git"
    };
    var provider = new StubProvider(plan: (resource, _) =>
        ValidPlan(resource, ComplianceStatus.DetectionFailed));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with
        {
          Outcome = DetectionOutcome.Failed,
          StructuredError = source
        }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var error = Assert.Single(plan.Errors);
    Assert.Equal(WdemErrorCode.PermissionError, error.Code);
    Assert.NotSame(source, error);
    Assert.DoesNotContain("detection-secret", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_InvalidStepIdAndOversizedText_AreRejectedWithBoundedOutput()
  {
    var longText = new string('x', ExecutionPlanner.MaxTextFieldByteCount + 1);
    var providers = new[]
    {
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("contains whitespace"))),
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("install") with { Description = longText })),
      new StubProvider(plan: (resource, _) => ValidPlan(
          resource,
          ComplianceStatus.Missing,
          Step("install") with { Reason = longText }))
    };

    foreach (var provider in providers)
    {
      var plan = await Planner(provider).CreateAsync(
          Graph(Resource("git")),
          States(State("git", false)),
          "developer",
          "1.0.0",
          CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(Assert.Single(plan.Resources).ResourcePlan.Steps);
      Assert.True(Assert.Single(plan.Errors).Detail.Length <=
          ExecutionPlanner.MaxTextFieldByteCount);
    }
  }

  [Fact]
  public async Task CreateAsync_TotalProviderTextBudget_IsBoundedBeforeHashingOutput()
  {
    var description = new string('x', ExecutionPlanner.MaxTextFieldByteCount);
    var reason = new string('y', 256);
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Enumerable.Range(0, ExecutionPlanner.MaxStepsPerResource)
            .Select(index => Step($"step-{index}") with
            {
              Description = description,
              Reason = reason
            })
            .ToArray()));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Empty(plan.Resources);
    Assert.Empty(plan.Layers);
    Assert.Equal(WdemErrorCode.ProviderError, Assert.Single(plan.Errors).Code);
  }

  [Fact]
  public async Task CreateAsync_PreservesProviderStepExecutionOrder()
  {
    var provider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("download"),
        Step("verify"),
        Step("install")));

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal(
        ["download", "verify", "install"],
        Assert.Single(plan.Resources).ResourcePlan.Steps.Select(step => step.Id));
  }

  [Fact]
  public async Task CreateAsync_EmptyOrMalformedGraph_IsNotExecutable()
  {
    var empty = await Planner().CreateAsync(
        new ResourceGraph(
            new Dictionary<string, ResolvedResource>(),
            Array.Empty<ResourceGraphLayer>()),
        new Dictionary<string, DetectedState>(),
        "developer",
        "1.0.0",
        CancellationToken.None);
    var cycleLike = await Planner(new StubProvider()).CreateAsync(
        new ResourceGraph(
            GraphNodes(Resource("git")),
            Array.Empty<ResourceGraphLayer>()),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(empty.IsExecutable);
    Assert.False(cycleLike.IsExecutable);
    Assert.All(empty.Errors.Concat(cycleLike.Errors), error =>
        Assert.Equal(WdemErrorCode.DependencyError, error.Code));
  }

  [Theory]
  [InlineData(">=3.0", null, "2.52.1", ComplianceStatus.VersionMismatch)]
  [InlineData(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      null, ComplianceStatus.ConfigurationMismatch)]
  public async Task CreateAsync_ProviderCannotOverrideEvaluatorCompliance(
      string? versionConstraint,
      string? actualHash,
      string? detectedVersion,
      ComplianceStatus expectedCompliance)
  {
    var resource = Resource("git") with
    {
      VersionConstraint = versionConstraint,
      Parameters = actualHash is null
          ? new Dictionary<string, string?>()
          : new Dictionary<string, string?>
          {
            ["expectedSha256"] = new string('a', 64)
          }
    };
    var provider = new StubProvider(plan: (definition, _) =>
        ValidPlan(definition, ComplianceStatus.Satisfied));

    var plan = await Planner(provider).CreateAsync(
        Graph(resource),
        States(State("git", true) with
        {
          Version = detectedVersion,
          ConfigurationHash = actualHash
        }),
        "developer",
        "1.0.0",
        CancellationToken.None);

    var planned = Assert.Single(plan.Resources);
    Assert.False(plan.IsExecutable);
    Assert.Equal(PlannedResourceStatus.Invalid, planned.Status);
    Assert.Contains(plan.Errors, error =>
        error.Detail.Contains(expectedCompliance.ToString(), StringComparison.Ordinal));
  }

  [Fact]
  public async Task CreateAsync_UsesInjectedComplianceEvaluatorAsAuthority()
  {
    var evaluator = new StubComplianceEvaluator(new ComplianceResult(
        ComplianceStatus.Satisfied,
        "The injected evaluator classified the resource."));
    var provider = new StubProvider(plan: (resource, _) =>
        ValidPlan(resource, ComplianceStatus.Satisfied));
    var planner = new ExecutionPlanner(
        new ResourceProviderRegistry([provider]),
        evaluator);

    var plan = await planner.CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal(1, evaluator.EvaluateCalls);
    Assert.Equal(PlannedResourceStatus.AlreadySatisfied, Assert.Single(plan.Resources).Status);
  }

  [Fact]
  public async Task CreateAsync_MalformedResourceDefinitions_ReturnStructuredBoundaryPlans()
  {
    var malformed = new[]
    {
      Resource("git") with { Dependencies = null! },
      Resource("git") with { Parameters = null! },
      Resource("git") with
      {
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
          ["token"] = "first",
          ["TOKEN"] = "second"
        }
      },
      Resource("git") with
      {
        Parameters = new Dictionary<string, string?> { ["channel"] = null }
      },
      Resource("git") with { Dependencies = new ThrowingReadOnlyList<string>() },
      Resource("bad\u0001id")
    };

    foreach (var resource in malformed)
    {
      var provider = new StubProvider();
      ExecutionPlan? plan = null;
      var exception = await Record.ExceptionAsync(async () =>
          plan = await Planner(provider).CreateAsync(
              Graph(resource),
              States(State(resource.Id, false)),
              "developer",
              "1.0.0",
              CancellationToken.None));

      Assert.Null(exception);
      Assert.NotNull(plan);
      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Resources);
      Assert.NotEmpty(plan.Errors);
      Assert.All(plan.Errors, error => Assert.True(
          error.ResourceId is null || !error.ResourceId.Any(char.IsControl)));
      Assert.Equal(0, provider.PlanCalls);
    }
  }

  [Fact]
  public async Task CreateAsync_StructuredDiagnosticsWithSameDetailUseFullAuditIdentity()
  {
    var provider = new StubProvider(validation: resource => new ProviderValidationResult
    {
      StructuredErrors =
      [
        new StructuredError(WdemErrorCode.ConfigurationError, "first", "shared")
        {
          ResourceId = resource.Id,
          ProcessExitCode = 1
        },
        new StructuredError(WdemErrorCode.ConfigurationError, "second", "shared")
        {
          ResourceId = resource.Id,
          ProcessExitCode = 2
        }
      ]
    });

    var plan = await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.Equal(2, plan.Errors.Count);
    Assert.Equal([1, 2], plan.Errors.Select(error => error.ProcessExitCode));
  }

  [Fact]
  public async Task CreateAsync_StepDescriptionIsValidatedAndStoredAfterSanitization()
  {
    var invalidProvider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install") with { Description = "\u0001\u0002" }));
    var validProvider = new StubProvider(plan: (resource, _) => ValidPlan(
        resource,
        ComplianceStatus.Missing,
        Step("install") with { Description = "Install\u0001Git" }));

    var invalid = await Planner(invalidProvider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);
    var valid = await Planner(validProvider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false)),
        "developer",
        "1.0.0",
        CancellationToken.None);

    Assert.False(invalid.IsExecutable);
    Assert.Empty(Assert.Single(invalid.Resources).ResourcePlan.Steps);
    Assert.Equal("Install Git", Assert.Single(
        Assert.Single(valid.Resources).ResourcePlan.Steps).Description);
  }

  [Fact]
  public async Task CreateAsync_DiagnosticAuditFieldsParticipateInFingerprint()
  {
    var pairs = new (StructuredError First, StructuredError Second)[]
    {
      (Diagnostic(exitCode: 1), Diagnostic(exitCode: 2)),
      (Diagnostic(logLocation: "first.log"), Diagnostic(logLocation: "second.log")),
      (Diagnostic(suggestedAction: "first"), Diagnostic(suggestedAction: "second")),
      (Diagnostic(retryable: false), Diagnostic(retryable: true)),
      (Diagnostic(exception: new InvalidOperationException("same")),
          Diagnostic(exception: new ArgumentException("same"))),
      (Diagnostic(exception: new InvalidOperationException("first")),
          Diagnostic(exception: new InvalidOperationException("second")))
    };

    foreach (var pair in pairs)
    {
      var first = await TerminalDiagnosticPlan(pair.First);
      var second = await TerminalDiagnosticPlan(pair.Second);

      Assert.NotEqual(first.Fingerprint, second.Fingerprint);
      Assert.NotEqual(first.PlanId, second.PlanId);
    }
  }

  [Fact]
  public async Task CreateAsync_ExternalFieldCollectionAndTotalBudgetsAreStructuredFailures()
  {
    var oversizedField = State("git", true) with
    {
      Version = new string('x', ExecutionPlanner.MaxTextFieldByteCount + 1)
    };
    var oversizedCollection = State("git", true) with
    {
      Evidence = Enumerable.Range(0, 10_001).ToDictionary(
          index => $"key-{index}",
          _ => string.Empty)
    };
    var oversizedTotal = State("git", true) with
    {
      Evidence = Enumerable.Range(0, 1_025).ToDictionary(
          index => $"key-{index}",
          _ => new string('x', ExecutionPlanner.MaxTextFieldByteCount))
    };

    foreach (var state in new[] { oversizedField, oversizedCollection, oversizedTotal })
    {
      var provider = new StubProvider();
      var plan = await Planner(provider).CreateAsync(
          Graph(Resource("git")),
          States(state),
          "developer",
          "1.0.0",
          CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Resources);
      Assert.NotEmpty(plan.Errors);
      Assert.Equal(0, provider.PlanCalls);
    }
  }

  [Fact]
  public async Task CreateAsync_ResourceFieldCollectionAndTotalBudgetsAreStructuredFailures()
  {
    var oversizedField = Resource("git") with
    {
      DisplayName = new string('x', ExecutionPlanner.MaxTextFieldByteCount + 1)
    };
    var oversizedCollection = Resource("git") with
    {
      Parameters = Enumerable.Range(0, 10_001).ToDictionary(
          index => $"key-{index}",
          _ => (string?)string.Empty)
    };
    var oversizedTotal = Resource("git") with
    {
      Parameters = Enumerable.Range(0, 1_025).ToDictionary(
          index => $"key-{index}",
          _ => (string?)new string('x', ExecutionPlanner.MaxTextFieldByteCount))
    };

    foreach (var resource in new[] { oversizedField, oversizedCollection, oversizedTotal })
    {
      var provider = new StubProvider();
      var plan = await Planner(provider).CreateAsync(
          Graph(resource),
          States(State("git", false)),
          "developer",
          "1.0.0",
          CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Resources);
      Assert.NotEmpty(plan.Errors);
      Assert.Equal(0, provider.PlanCalls);
    }
  }

  private static StructuredError Diagnostic(
      int? exitCode = null,
      string? logLocation = null,
      string? suggestedAction = null,
      bool retryable = false,
      Exception? exception = null) => new(
          WdemErrorCode.DetectionError,
          "Detection failed.",
          "shared detail")
      {
        ResourceId = "git",
        ProcessExitCode = exitCode,
        LogLocation = logLocation,
        SuggestedAction = suggestedAction,
        IsRetryable = retryable,
        UnderlyingException = exception
      };

  private static async Task<ExecutionPlan> TerminalDiagnosticPlan(StructuredError diagnostic)
  {
    var provider = new StubProvider(plan: (resource, _) =>
        ValidPlan(resource, ComplianceStatus.DetectionFailed) with
        {
          StructuredErrors = [diagnostic]
        });
    return await Planner(provider).CreateAsync(
        Graph(Resource("git")),
        States(State("git", false) with { Outcome = DetectionOutcome.Failed }),
        "developer",
        "1.0.0",
        CancellationToken.None);
  }

  private static ExecutionPlanner Planner(params IResourceProvider[] providers) => new(
      new ResourceProviderRegistry(providers),
      new ComplianceEvaluator());

  private static ResourceGraph Graph(params ResourceDefinition[][] layers)
  {
    var resources = layers.SelectMany(layer => layer).ToArray();
    return Graph(
        resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase),
        layers.Select((layer, index) =>
            new ResourceGraphLayer(index, layer.Select(resource => resource.Id).ToArray())).ToArray());
  }

  private static ResourceGraph Graph(params ResourceDefinition[] resources) => Graph(
      resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase),
      [new ResourceGraphLayer(0, resources.Select(resource => resource.Id).ToArray())]);

  private static ResourceGraph Graph(
      IReadOnlyDictionary<string, ResourceDefinition> resources,
      IReadOnlyList<ResourceGraphLayer> layers) => new(
          resources.ToDictionary(
              pair => pair.Key,
              pair => new ResolvedResource(
                  pair.Value,
                  ResourceOrigin.Required,
                  new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
              StringComparer.OrdinalIgnoreCase),
          layers);

  private static IReadOnlyDictionary<string, ResolvedResource> GraphNodes(
      params ResourceDefinition[] resources) => Graph(resources).Nodes;

  private static ResourceDefinition Resource(
      string id,
      IReadOnlyList<string>? dependencies = null) => new()
      {
        Id = id,
        Type = "package",
        Provider = "test",
        Dependencies = dependencies ?? []
      };

  private static DetectedState State(string id, bool exists) => new()
  {
    ResourceId = id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = exists
  };

  private static IReadOnlyDictionary<string, DetectedState> States(
      params DetectedState[] states) => states.ToDictionary(
          state => state.ResourceId,
          StringComparer.OrdinalIgnoreCase);

  private static ResourcePlan ValidPlan(
      ResourceDefinition resource,
      ComplianceStatus compliance,
      params PlanStep[] steps) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = compliance,
        IsExecutable = compliance is ComplianceStatus.Satisfied or
            ComplianceStatus.Missing or
            ComplianceStatus.VersionMismatch or
            ComplianceStatus.ConfigurationMismatch,
        Steps = steps.Length == 0 && compliance == ComplianceStatus.Satisfied
            ? [Step("none") with { Action = PlanAction.None }]
            : steps
      };

  private static PlanStep Step(string id) => new()
  {
    Id = id,
    Description = "Install resource",
    Action = PlanAction.Install,
    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
    RestartPolicy = RestartPolicy.NoRestart
  };

  private static void AssertCannotMutateList<T>(IReadOnlyList<T> list, T value)
  {
    if (list is IList<T> mutable)
    {
      if (mutable.Count == 0)
      {
        Assert.Throws<NotSupportedException>(() => mutable.Add(value));
      }
      else
      {
        Assert.Throws<NotSupportedException>(() => mutable[0] = value);
      }
    }
  }

  private sealed class ResourceGraphLayerComparer : IEqualityComparer<ResourceGraphLayer>
  {
    public static ResourceGraphLayerComparer Instance { get; } = new();

    public bool Equals(ResourceGraphLayer? x, ResourceGraphLayer? y)
    {
      if (ReferenceEquals(x, y))
      {
        return true;
      }

      return x is not null && y is not null &&
          x.Index == y.Index &&
          x.ResourceIds.SequenceEqual(y.ResourceIds, StringComparer.Ordinal);
    }

    public int GetHashCode(ResourceGraphLayer obj) => obj.Index;
  }

  private sealed class StubProvider(
      Func<ResourceDefinition, ProviderValidationResult>? validation = null,
      Func<ResourceDefinition, DetectedState, ResourcePlan>? plan = null) : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "test";
    public ProviderCapabilities Capabilities { get; } = new();
    public int PlanCalls { get; private set; }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return ValueTask.FromResult(validation?.Invoke(resource) ?? ProviderValidationResult.Valid);
    }

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      PlanCalls++;
      return ValueTask.FromResult(plan is not null
          ? plan(resource, currentState)
          : currentState.Exists
              ? ValidPlan(resource, ComplianceStatus.Satisfied)
              : ValidPlan(resource, ComplianceStatus.Missing, Step("install")));
    }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class StubComplianceEvaluator(ComplianceResult result) : IComplianceEvaluator
  {
    public int EvaluateCalls { get; private set; }

    public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
    {
      EvaluateCalls++;
      return result;
    }
  }

  private sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
  {
    public int Count => 1;
    public T this[int index] => throw new InvalidOperationException("snapshot failed");

    public IEnumerator<T> GetEnumerator() =>
        throw new InvalidOperationException("snapshot failed");

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
  }
}
