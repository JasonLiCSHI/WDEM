using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Wdem.Windows.Persistence;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Persistence;

public sealed class JsonExecutionRunStoreTests : IDisposable
{
  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-run-store-{Guid.NewGuid():N}");
  private readonly JsonExecutionRunStore _store;

  public JsonExecutionRunStoreTests()
  {
    _store = new JsonExecutionRunStore(new WdemDataPaths(_directory), new LogRedactor());
  }

  [Fact]
  public void Diagnostics_AreAvailableThroughStoreContract()
  {
    IExecutionRunStore store = _store;

    Assert.Empty(store.Diagnostics);
  }

  [Fact]
  public async Task CreateAsync_ProtectsOriginalApprovedResourceAndKeepsPublicSnapshotRedacted()
  {
    var protector = new DeterministicApprovedResourceProtector();
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        protector);
    var run = SampleRun();
    var planned = Assert.Single(run.Plan!.Resources);
    var original = planned.Definition with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = "original-secret"
      }
    };
    var executablePlan = planned.ResourcePlan with
    {
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          PrivilegeRequirement = PrivilegeRequirement.Administrator
        }
      ]
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          planned with
          {
            Definition = original,
            ResourcePlan = executablePlan,
            RequiresElevation = true,
            Risk = PlanRisk.Elevated
          }
        ]
      }
    };

    await store.CreateAsync(
        run,
        [new ApprovedResourceSeal(original, executablePlan)],
        CancellationToken.None);

    var approved = await ((IApprovedResourceStore)store).GetApprovedResourceAsync(
        run.RunId,
        original.Id,
        CancellationToken.None);
    var publicSnapshot = await File.ReadAllTextAsync(store.SnapshotPath(run.RunId));
    var protectedSnapshot = await File.ReadAllTextAsync(store.ApprovedResourcesPath(run.RunId));
    Assert.NotNull(approved);
    Assert.Equal("original-secret", approved.Definition.Parameters["password"]);
    Assert.Equal(executablePlan.ResourceId, approved.Plan.ResourceId);
    Assert.Equal(executablePlan.ResourceType, approved.Plan.ResourceType);
    Assert.Equal(executablePlan.ProviderName, approved.Plan.ProviderName);
    Assert.Equal(executablePlan.DesiredStateFingerprint, approved.Plan.DesiredStateFingerprint);
    Assert.Equal(executablePlan.Steps.Single(), approved.Plan.Steps.Single());
    Assert.Equal(
        ApprovedResourceFingerprint.Create(original, executablePlan),
        approved.Fingerprint);
    Assert.DoesNotContain("original-secret", publicSnapshot, StringComparison.Ordinal);
    Assert.DoesNotContain("original-secret", protectedSnapshot, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateSealIdsThatOmitAnElevatedResource()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("seal-secret");
    var first = Assert.Single(run.Plan!.Resources);
    var secondDefinition = first.Definition with { Id = "second-admin-resource" };
    var secondPlan = first.ResourcePlan with
    {
      ResourceId = secondDefinition.Id,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(secondDefinition)
    };
    var second = first with
    {
      Definition = secondDefinition,
      ResourcePlan = secondPlan
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Layers = [new ResourceGraphLayer(0, [first.Definition.Id, secondDefinition.Id])],
        Resources = [first, second]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(
          run.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        [secondDefinition.Id] = run.ResourceResults[first.Definition.Id] with
        {
          ResourceId = secondDefinition.Id
        }
      }
    };
    var duplicate = new ApprovedResourceSeal(first.Definition, first.ResourcePlan);

    await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
        run,
        [duplicate, duplicate],
        CancellationToken.None));
  }

  [Fact]
  public async Task SealApprovedResourceAsync_ProtectsDeferredPlanAfterSafeReplan()
  {
    var protector = new DeterministicApprovedResourceProtector();
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        protector);
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var executablePlan = deferred.ResourcePlan with
    {
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "install-deferred",
          Description = "Install deferred resource",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };

    await store.CreateAsync(run, [], CancellationToken.None);
    await store.SaveAsync(
        PromoteDeferredRun(run, executablePlan),
        CancellationToken.None);
    await store.SealApprovedResourceAsync(
        run.RunId,
        new ApprovedResourceSeal(deferred.Definition, executablePlan),
        CancellationToken.None);

    var approved = await store.GetApprovedResourceAsync(
        run.RunId,
        deferred.Definition.Id,
        CancellationToken.None);
    Assert.NotNull(approved);
    Assert.Equal("deferred-secret", approved.Definition.Parameters["password"]);
    Assert.Equal(executablePlan.ResourceId, approved.Plan.ResourceId);
    Assert.Equal(executablePlan.DesiredStateFingerprint, approved.Plan.DesiredStateFingerprint);
    Assert.Equal(executablePlan.Compliance, approved.Plan.Compliance);
    Assert.Equal(executablePlan.IsExecutable, approved.Plan.IsExecutable);
    Assert.Equal(executablePlan.Steps.Single(), approved.Plan.Steps.Single());
    Assert.DoesNotContain(
        "deferred-secret",
        await File.ReadAllTextAsync(store.ApprovedResourcesPath(run.RunId)),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task SealApprovedResourceAsync_RejectsChangedDeferredDefinition()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = DeferredElevatedRun("approved-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    await store.CreateAsync(run, [], CancellationToken.None);
    var approvedPlan = deferred.ResourcePlan with
    {
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "install-deferred",
          Description = "Install deferred resource",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    await store.SaveAsync(
        PromoteDeferredRun(run, approvedPlan),
        CancellationToken.None);
    var changed = deferred.Definition with
    {
      Parameters = new Dictionary<string, string?>(deferred.Definition.Parameters)
      {
        ["password"] = "changed-secret"
      }
    };
    var changedPlan = deferred.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(changed),
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "install-deferred",
          Description = "Install deferred resource",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };

    await Assert.ThrowsAsync<InvalidOperationException>(() => store.SealApprovedResourceAsync(
        run.RunId,
        new ApprovedResourceSeal(changed, changedPlan),
        CancellationToken.None));

    Assert.False(File.Exists(store.ApprovedResourcesPath(run.RunId)));
  }

  [Theory]
  [InlineData("duplicate")]
  [InlineData("missing")]
  [InlineData("extra")]
  [InlineData("fingerprint")]
  [InlineData("run-id")]
  public async Task SealApprovedResourceAsync_CorruptExistingSealThrowsWithoutRewrite(
      string corruption)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = DeferredElevatedRunWithTwoResources();
    var first = run.Plan!.Resources[0];
    var second = run.Plan.Resources[1];
    var firstPlan = ExecutableDeferredPlan(first);
    var secondPlan = ExecutableDeferredPlan(second);

    await store.CreateAsync(run, [], CancellationToken.None);
    var firstPromoted = await store.SaveAsync(
        PromoteDeferredResources(run, (first.Definition.Id, firstPlan)),
        CancellationToken.None);
    await store.SealApprovedResourceAsync(
        run.RunId,
        new ApprovedResourceSeal(first.Definition, firstPlan),
        CancellationToken.None);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var resources = document["resources"]!.AsArray();
    switch (corruption)
    {
      case "duplicate":
        resources.Add(resources[0]!.DeepClone());
        break;
      case "missing":
        resources.Clear();
        break;
      case "extra":
        var extra = resources[0]!.DeepClone().AsObject();
        extra["resourceId"] = "unexpected";
        resources.Add(extra);
        break;
      case "fingerprint":
        resources[0]!["fingerprint"] = new string('A', 64);
        break;
      case "run-id":
        document["runId"] = Guid.NewGuid();
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
    }
    var corruptSidecar = document.ToJsonString();
    await File.WriteAllTextAsync(path, corruptSidecar);
    await store.SaveAsync(
        PromoteDeferredResources(
            firstPromoted,
            (first.Definition.Id, firstPlan),
            (second.Definition.Id, secondPlan)),
        CancellationToken.None);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.SealApprovedResourceAsync(
            run.RunId,
            new ApprovedResourceSeal(second.Definition, secondPlan),
            CancellationToken.None));

    Assert.Equal(WdemErrorCode.PermissionError, exception.Error.Code);
    Assert.False(exception.Error.IsRetryable);
    Assert.Equal(corruptSidecar, await File.ReadAllTextAsync(path));
  }

  [Fact]
  public async Task CreateAsync_RejectsDeferredResourceWithoutAuthorization()
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    run = run with
    {
      Plan = run.Plan with
      {
        Resources = [deferred with { DeferredAuthorization = null }]
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Fact]
  public async Task CreateAsync_RejectsAuthorizationOnConcreteResource()
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          deferred with
          {
            Status = PlannedResourceStatus.Ready,
            ResourcePlan = deferred.ResourcePlan with { IsExecutable = true }
          }
        ]
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Fact]
  public async Task CreateAsync_RejectsDeferredAuthorizationWithoutModifyingAction()
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var authorization = Assert.IsType<DeferredPlanAuthorization>(
        deferred.DeferredAuthorization);
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          deferred with
          {
            DeferredAuthorization = authorization with { AllowedActions = [] }
          }
        ]
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Theory]
  [InlineData("action")]
  [InlineData("privilege")]
  [InlineData("restart")]
  [InlineData("risk")]
  public async Task CreateAsync_RejectsUndefinedDeferredAuthorizationEnum(string field)
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var authorization = Assert.IsType<DeferredPlanAuthorization>(
        deferred.DeferredAuthorization);
    var invalid = field switch
    {
      "action" => authorization with { AllowedActions = [(PlanAction)int.MaxValue] },
      "privilege" => authorization with
      {
        MaximumPrivilege = (PrivilegeRequirement)int.MaxValue
      },
      "restart" => authorization with
      {
        MaximumRestartPolicy = (RestartPolicy)int.MaxValue
      },
      "risk" => authorization with { MaximumRisk = (PlanRisk)int.MaxValue },
      _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Resources = [deferred with { DeferredAuthorization = invalid }]
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Fact]
  public async Task CreateAsync_RejectsDeferredAuthorizationWithoutNotice()
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var authorization = Assert.IsType<DeferredPlanAuthorization>(
        deferred.DeferredAuthorization);
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          deferred with
          {
            DeferredAuthorization = authorization with { DynamicPlanNotice = " " }
          }
        ]
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Theory]
  [InlineData("privilege")]
  [InlineData("restart")]
  [InlineData("risk")]
  [InlineData("destructive")]
  public async Task CreateAsync_RejectsDeferredSummaryOutsideAuthorization(string field)
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var invalid = field switch
    {
      "privilege" => deferred with { RequiresElevation = false },
      "restart" => deferred with { RestartPolicy = RestartPolicy.RestartRecommended },
      "risk" => deferred with { Risk = PlanRisk.Standard },
      "destructive" => deferred with { IsDestructive = true },
      _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    run = run with
    {
      Plan = run.Plan with { Resources = [invalid] }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Theory]
  [InlineData("privilege")]
  [InlineData("restart")]
  public async Task CreateAsync_RejectsDeferredAuthorizationBeyondDefinition(string field)
  {
    var run = DeferredElevatedRun("deferred-secret");
    var deferred = Assert.Single(run.Plan!.Resources);
    var authorization = Assert.IsType<DeferredPlanAuthorization>(
        deferred.DeferredAuthorization);
    var invalid = field switch
    {
      "privilege" => deferred with
      {
        Definition = deferred.Definition with
        {
          PrivilegeRequirement = PrivilegeRequirement.CurrentUser
        }
      },
      "restart" => deferred with
      {
        RestartPolicy = RestartPolicy.RestartRecommended,
        DeferredAuthorization = authorization with
        {
          MaximumRestartPolicy = RestartPolicy.RestartRecommended
        },
        ResourcePlan = deferred.ResourcePlan with
        {
          Steps =
          [
            deferred.ResourcePlan.Steps.Single() with
            {
              RestartPolicy = RestartPolicy.RestartRecommended
            }
          ]
        }
      },
      _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    run = run with
    {
      Plan = run.Plan with { Resources = [invalid] }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
  }

  [Fact]
  public async Task CreateAndGet_AllowsInspectWithDeferredPlanWithoutApproval()
  {
    var run = DeferredElevatedRun("inspect-secret") with
    {
      Mode = RunMode.Inspect,
      PlanApproval = null
    };

    await _store.CreateAsync(run, CancellationToken.None);
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(RunMode.Inspect, restored!.Mode);
    Assert.Null(restored.PlanApproval);
    Assert.Equal(PlannedResourceStatus.Deferred, Assert.Single(restored.Plan!.Resources).Status);
  }

  [Fact]
  public async Task CreateAndGet_AllowsNonExecutableApplyWithDeferredPlanWithoutApproval()
  {
    var run = DeferredElevatedRun("rejected-apply-secret");
    run = run with
    {
      Plan = run.Plan! with
      {
        IsExecutable = false,
        Errors =
        [
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "The reviewed execution plan has changed.",
              "The plan must be reviewed again before applying it.")
        ]
      },
      PlanApproval = null
    };

    await _store.CreateAsync(run, CancellationToken.None);
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(RunMode.Apply, restored!.Mode);
    Assert.False(restored.Plan!.IsExecutable);
    Assert.Single(restored.Plan.Errors);
    Assert.Null(restored.PlanApproval);
  }

  [Fact]
  public async Task CreateAsync_RejectsExecutableApplyWithDeferredPlanWithoutApproval()
  {
    var run = DeferredElevatedRun("unapproved-apply-secret") with
    {
      PlanApproval = null
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));

    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task CreateAndGet_PreservesMinimalDeferredApprovalProof()
  {
    const string secret = "deferred-profile-secret";
    var run = DeferredElevatedRun(secret);

    await _store.CreateAsync(run, CancellationToken.None);
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    var approval = Assert.IsType<PlanApproval>(restored!.PlanApproval);
    Assert.Equal(run.PlanApproval!.InitialPlanFingerprint, approval.InitialPlanFingerprint);
    Assert.Equal(PlanApprovalSource.DesktopReviewedPlan, approval.Source);
    var proof = Assert.Single(approval.DeferredAuthorizations);
    Assert.Equal("git", proof.ResourceId);
    Assert.Equal(ResourceDefinitionFingerprint.Create(
        run.Plan!.Resources.Single().Definition), proof.DefinitionFingerprint);
    Assert.Equal([PlanAction.Install], proof.AllowedActions);
    Assert.DoesNotContain(
        secret,
        await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId)),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_RejectsApprovalForDifferentInitialPlanFingerprint()
  {
    var run = DeferredElevatedRun("different-plan-secret");
    run = run with
    {
      PlanApproval = run.PlanApproval! with
      {
        InitialPlanFingerprint = new string('E', 64)
      }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));

    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task GetAsync_RejectsTamperedDeferredApprovalProof()
  {
    var run = DeferredElevatedRun("deferred-profile-secret");
    await _store.CreateAsync(run, CancellationToken.None);
    var path = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    document["planApproval"]!["deferredAuthorizations"]![0]!["allowedActions"]![0] =
        "configure";
    await File.WriteAllTextAsync(path, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(path));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(path)!, $"{run.RunId:D}.json.corrupted.*"));
  }

  [Fact]
  public async Task SaveAsync_RejectsChangedPlanApproval()
  {
    var run = DeferredElevatedRun("deferred-profile-secret");
    await _store.CreateAsync(run, CancellationToken.None);
    var changed = run with
    {
      PlanApproval = run.PlanApproval! with
      {
        Source = PlanApprovalSource.CommandLine
      }
    };

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.SaveAsync(changed, CancellationToken.None));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(PlanApprovalSource.DesktopReviewedPlan, restored!.PlanApproval!.Source);
  }

  [Theory]
  [InlineData("privilege")]
  [InlineData("restart")]
  [InlineData("destructive")]
  public async Task SaveAsync_RejectsPromotedDeferredPlanWithUnsafeNoneStep(
      string unsafeField)
  {
    var run = DeferredElevatedRun("deferred-profile-secret");
    await _store.CreateAsync(run, CancellationToken.None);
    var deferred = Assert.Single(run.Plan!.Resources);
    var executablePlan = deferred.ResourcePlan with
    {
      IsExecutable = true,
      Steps =
      [
        deferred.ResourcePlan.Steps.Single(),
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
    };

    await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(
        PromoteDeferredRun(run, executablePlan),
        CancellationToken.None));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(
        PlannedResourceStatus.Deferred,
        Assert.Single(restored!.Plan!.Resources).Status);
  }

  [Fact]
  public async Task GetApprovedResourceAsync_TamperedCiphertextIsRejected()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("tamper-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var payload = document["resources"]![0]!["protectedPayload"]!.GetValue<string>();
    document["resources"]![0]!["protectedPayload"] =
        $"{(payload[0] == 'A' ? 'B' : 'A')}{payload[1..]}";
    await File.WriteAllTextAsync(path, document.ToJsonString());

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.GetApprovedResourceAsync(
            run.RunId,
            "git",
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
    Assert.Contains(store.Diagnostics, error => error.Code == WdemErrorCode.PermissionError);
  }

  [Theory]
  [InlineData("run-id")]
  [InlineData("null-envelope")]
  [InlineData("duplicate-entry")]
  [InlineData("invalid-payload")]
  public async Task GetApprovedResourceAsync_CorruptEnvelopeThrowsStoreException(
      string corruption)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("get-corrupt-envelope-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var resources = document["resources"]!.AsArray();
    switch (corruption)
    {
      case "run-id":
        document["runId"] = Guid.NewGuid();
        break;
      case "null-envelope":
        await File.WriteAllTextAsync(path, "null");
        break;
      case "duplicate-entry":
        resources.Add(resources[0]!.DeepClone());
        break;
      case "invalid-payload":
        resources[0]!["protectedPayload"] = "not-base64";
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
    }

    if (corruption != "null-envelope")
    {
      await File.WriteAllTextAsync(path, document.ToJsonString());
    }

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.GetApprovedResourceAsync(
            run.RunId,
            "git",
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Theory]
  [InlineData("extra-entry")]
  [InlineData("missing-unrelated-entry")]
  [InlineData("duplicate-unrelated-entry")]
  [InlineData("corrupt-unrelated-payload")]
  [InlineData("malformed-unrelated-claims")]
  public async Task GetApprovedResourceAsync_ValidatesEntireEnvelopeBeforeAbsentTarget(
      string corruption)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = await CreateWithTwoApprovedResourcesAsync(store);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var resources = document["resources"]!.AsArray();
    var unrelated = resources.Single(node =>
        node!["resourceId"]!.GetValue<string>() == "node")!;
    switch (corruption)
    {
      case "extra-entry":
        var extra = unrelated.DeepClone().AsObject();
        extra["resourceId"] = "unexpected";
        resources.Add(extra);
        break;
      case "missing-unrelated-entry":
        resources.Remove(unrelated);
        break;
      case "duplicate-unrelated-entry":
        resources.Add(unrelated.DeepClone());
        break;
      case "corrupt-unrelated-payload":
        unrelated["protectedPayload"] = "not-base64";
        break;
      case "malformed-unrelated-claims":
        unrelated["claimedPlanFingerprints"] = new JsonArray("not-a-fingerprint");
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
    }

    await File.WriteAllTextAsync(path, document.ToJsonString());

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.GetApprovedResourceAsync(
            run.RunId,
            "absent-resource",
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_MalformedSidecarThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("malformed-sidecar-secret");
    await CreateWithApprovedResourceAsync(store, run);
    await File.WriteAllTextAsync(store.ApprovedResourcesPath(run.RunId), "{");
    var planned = Assert.Single(run.Plan!.Resources);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan),
            CancellationToken.None));

    Assert.Equal(WdemErrorCode.PermissionError, exception.Error.Code);
    Assert.False(exception.Error.IsRetryable);
  }

  [Theory]
  [InlineData("run-id")]
  [InlineData("missing-entry")]
  [InlineData("duplicate-entry")]
  [InlineData("entry-fingerprint")]
  [InlineData("protected-payload")]
  public async Task ClaimApprovedResourceAsync_CorruptEnvelopeThrowsStoreException(
      string corruption)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("wrong-envelope-run-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var resources = document["resources"]!.AsArray();
    switch (corruption)
    {
      case "run-id":
        document["runId"] = Guid.NewGuid();
        break;
      case "missing-entry":
        resources.Clear();
        break;
      case "duplicate-entry":
        resources.Add(resources[0]!.DeepClone());
        break;
      case "entry-fingerprint":
        resources[0]!["fingerprint"] = new string('A', 64);
        break;
      case "protected-payload":
        resources[0]!["protectedPayload"] = "not-base64";
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
    }

    await File.WriteAllTextAsync(path, document.ToJsonString());
    var planned = Assert.Single(run.Plan!.Resources);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan),
            CancellationToken.None));

    Assert.Equal(WdemErrorCode.PermissionError, exception.Error.Code);
    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_NullResourceEntryThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("null-resource-entry-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    document["resources"] = new JsonArray((JsonNode?)null);
    await File.WriteAllTextAsync(path, document.ToJsonString());
    var planned = Assert.Single(run.Plan!.Resources);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Theory]
  [InlineData("extra-entry")]
  [InlineData("duplicate-unrelated-entry")]
  [InlineData("corrupt-unrelated-payload")]
  [InlineData("malformed-unrelated-claims")]
  public async Task ClaimApprovedResourceAsync_ValidatesEntireMultiResourceEnvelopeBeforeClaim(
      string corruption)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = await CreateWithTwoApprovedResourcesAsync(store);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var resources = document["resources"]!.AsArray();
    var unrelated = resources.Single(node =>
        node!["resourceId"]!.GetValue<string>() == "node")!;
    switch (corruption)
    {
      case "extra-entry":
        var extra = unrelated.DeepClone().AsObject();
        extra["resourceId"] = "unexpected";
        resources.Add(extra);
        break;
      case "duplicate-unrelated-entry":
        resources.Add(unrelated.DeepClone());
        break;
      case "corrupt-unrelated-payload":
        unrelated["protectedPayload"] = "not-base64";
        break;
      case "malformed-unrelated-claims":
        unrelated["claimedPlanFingerprints"] = new JsonArray("not-a-fingerprint");
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
    }

    var corruptSidecar = document.ToJsonString();
    await File.WriteAllTextAsync(path, corruptSidecar);
    var requested = run.Plan!.Resources.Single(resource =>
        resource.Definition.Id == "git");

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            requested.Definition.Id,
            ApprovedResourceFingerprint.Create(
                requested.Definition,
                requested.ResourcePlan),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
    Assert.Equal(corruptSidecar, await File.ReadAllTextAsync(path));
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_ValidatesEntireEnvelopeBeforeAbsentTarget()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = await CreateWithTwoApprovedResourcesAsync(store);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var unrelated = document["resources"]!.AsArray().Single(node =>
        node!["resourceId"]!.GetValue<string>() == "node")!;
    unrelated["protectedPayload"] = "not-base64";
    await File.WriteAllTextAsync(path, document.ToJsonString());

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            "absent-resource",
            new string('A', 64),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_ValidatesEntireEnvelopeBeforeReplayDecision()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = await CreateWithTwoApprovedResourcesAsync(store);
    var requested = run.Plan!.Resources.Single(resource =>
        resource.Definition.Id == "git");
    var fingerprint = ApprovedResourceFingerprint.Create(
        requested.Definition,
        requested.ResourcePlan);
    var first = await store.ClaimApprovedResourceAsync(
        run.RunId,
        requested.Definition.Id,
        fingerprint,
        CancellationToken.None);
    Assert.NotNull(first);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var unrelated = document["resources"]!.AsArray().Single(node =>
        node!["resourceId"]!.GetValue<string>() == "node")!;
    unrelated["protectedPayload"] = "not-base64";
    await File.WriteAllTextAsync(path, document.ToJsonString());

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            requested.Definition.Id,
            fingerprint,
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_UnrelatedResourceRevisionAdvanceDoesNotRejectTarget()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var observed = await CreateWithTwoApprovedResourcesAsync(store);
    var target = observed.Plan!.Resources.Single(resource =>
        resource.Definition.Id == "git");
    var unrelated = observed.ResourceResults["node"] with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var advanced = await store.SaveAsync(observed with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(
          observed.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        ["node"] = unrelated
      }
    }, CancellationToken.None);
    Assert.True(advanced.Revision > observed.Revision);

    var claim = await store.ClaimApprovedResourceAsync(
        observed.RunId,
        target.Definition.Id,
        ApprovedResourceFingerprint.Create(target.Definition, target.ResourcePlan),
        CancellationToken.None);

    Assert.NotNull(claim);
    Assert.Equal(target.Definition.Id, claim.Definition.Id);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_ProtectedDefinitionMismatchThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new PassThroughApprovedResourceProtector());
    var run = ElevatedRunWithSecret("protected-definition-mismatch-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var entry = document["resources"]![0]!.AsObject();
    var payload = JsonNode.Parse(Convert.FromBase64String(
        entry["protectedPayload"]!.GetValue<string>()))!.AsObject();
    payload["definition"]!["id"] = "different-resource";
    entry["protectedPayload"] = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()));
    await File.WriteAllTextAsync(path, document.ToJsonString());
    var planned = Assert.Single(run.Plan!.Resources);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_PersistedPlanMismatchThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("persisted-plan-mismatch-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var planned = Assert.Single(run.Plan!.Resources);
    var changedPlan = planned.ResourcePlan with
    {
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          Id = "changed-after-approval"
        }
      ]
    };
    var saved = await store.SaveAsync(
        run with
        {
          Plan = run.Plan with
          {
            Resources = [planned with { ResourcePlan = changedPlan }]
          }
        },
        CancellationToken.None);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, changedPlan),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_DependencyMismatchThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("dependency-mismatch-secret");
    var planned = Assert.Single(run.Plan!.Resources);
    run = run with
    {
      Plan = run.Plan with
      {
        Resources = [planned with { Dependencies = ["dependency"] }]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(
          run.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        ["dependency"] = new ResourceResult
        {
          ResourceId = "dependency",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded
        }
      }
    };
    await CreateWithApprovedResourceAsync(store, run);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ClaimApprovedResourceAsync_MissingAdministratorSegmentThrowsStoreException()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("missing-segment-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var planned = Assert.Single(run.Plan!.Resources);

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        store.ClaimApprovedResourceAsync(
            run.RunId,
            planned.Definition.Id,
            new string('A', 64),
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task ElevatedWorker_CorruptApprovalSidecarReturnsPermissionFailureWithoutApplying()
  {
    var paths = new WdemDataPaths(_directory);
    var store = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("worker-corrupt-sidecar-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    document["runId"] = Guid.NewGuid();
    await File.WriteAllTextAsync(path, document.ToJsonString());
    var provider = new OriginalValueRecordingProvider();
    var worker = new ElevatedResourceWorker(
        store,
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var planned = Assert.Single(run.Plan!.Resources);

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan)),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task GetApprovedResourceAsync_DifferentUserProtectorIsRejected()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("identity-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector("first-user"));
    await CreateWithApprovedResourceAsync(creator, run);
    var otherUser = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector("second-user"));

    var exception = await Assert.ThrowsAsync<ApprovedResourceStoreException>(() =>
        otherUser.GetApprovedResourceAsync(
            run.RunId,
            "git",
            CancellationToken.None));

    Assert.False(exception.Error.IsRetryable);
  }

  [Fact]
  public async Task SaveAsync_TerminalRunRemovesApprovedResourceSnapshot()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("terminal-secret");
    await CreateWithApprovedResourceAsync(store, run);

    await store.SaveAsync(
        run with
        {
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        },
        CancellationToken.None);

    Assert.False(File.Exists(store.ApprovedResourcesPath(run.RunId)));
  }

  [Fact]
  public async Task SaveAsync_TerminalCleanupFailurePersistsRunAndRetriesCleanup()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("cleanup-secret");
    await CreateWithApprovedResourceAsync(store, run);
    var approvedPath = store.ApprovedResourcesPath(run.RunId);
    ExecutionRun saved;

    await using (var sidecarLock = new FileStream(
                     approvedPath,
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.None))
    {
      saved = await store.SaveAsync(
          run with
          {
            State = ExecutionState.Completed,
            Outcome = ExecutionOutcome.Succeeded,
            EndedAtUtc = DateTimeOffset.UtcNow
          },
          CancellationToken.None);

      Assert.Equal(ExecutionState.Completed, saved.State);
      Assert.True(File.Exists(approvedPath));
      var diagnostic = Assert.Single(store.Diagnostics);
      Assert.Equal(WdemErrorCode.PermissionError, diagnostic.Code);
      Assert.True(diagnostic.IsRetryable);
      Assert.IsType<IOException>(diagnostic.UnderlyingException);
    }

    var restored = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.NotNull(restored);
    Assert.Equal(ExecutionState.Completed, restored.State);
    Assert.False(File.Exists(approvedPath));
  }

  [Fact]
  public async Task ElevatedWorker_NewStoreInstanceExecutesWithProtectedOriginalValues()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("worker-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    var hostStore = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var provider = new OriginalValueRecordingProvider();
    var planned = run.Plan!.Resources.Single();
    var worker = new ElevatedResourceWorker(
        hostStore,
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(
                planned.Definition,
                planned.ResourcePlan)),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal("worker-secret", provider.AppliedResource!.Parameters["password"]);
  }

  [Fact]
  public async Task ElevatedWorker_ReopenedStoreCannotReplayApprovedSegment()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("one-time-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    var planned = Assert.Single(run.Plan!.Resources);
    var request = new ElevatedResourceRequest(
        run.RunId,
        planned.Definition.Id,
        ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan));
    var firstProvider = new OriginalValueRecordingProvider();
    var firstWorker = new ElevatedResourceWorker(
        new JsonExecutionRunStore(
            paths,
            new LogRedactor(),
            new DeterministicApprovedResourceProtector()),
        new ResourceProviderRegistry([firstProvider]),
        new LogRedactor());

    var first = await firstWorker.ApplyAsync(request, null, CancellationToken.None);

    var replayProvider = new OriginalValueRecordingProvider();
    var reopenedWorker = new ElevatedResourceWorker(
        new JsonExecutionRunStore(
            paths,
            new LogRedactor(),
            new DeterministicApprovedResourceProtector()),
        new ResourceProviderRegistry([replayProvider]),
        new LogRedactor());
    var replay = await reopenedWorker.ApplyAsync(request, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, first.Outcome);
    Assert.Equal(ApplyOutcome.Failed, replay.Outcome);
    Assert.Equal(1, firstProvider.ApplyCalls);
    Assert.Equal(0, replayProvider.ApplyCalls);
  }

  [Fact]
  public async Task ElevatedWorker_ConcurrentStoresHaveSingleApprovedSegmentWinner()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("concurrent-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    var planned = Assert.Single(run.Plan!.Resources);
    var request = new ElevatedResourceRequest(
        run.RunId,
        planned.Definition.Id,
        ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan));
    var providers = new[]
    {
      new OriginalValueRecordingProvider(),
      new OriginalValueRecordingProvider()
    };
    var workers = providers.Select(provider => new ElevatedResourceWorker(
        new JsonExecutionRunStore(
            paths,
            new LogRedactor(),
            new DeterministicApprovedResourceProtector()),
        new ResourceProviderRegistry([provider]),
        new LogRedactor())).ToArray();

    var results = await Task.WhenAll(workers.Select(worker =>
        worker.ApplyAsync(request, null, CancellationToken.None)));

    Assert.Single(results, result => result.Outcome == ApplyOutcome.Succeeded);
    Assert.Single(results, result => result.Outcome == ApplyOutcome.Failed);
    Assert.Equal(1, providers.Sum(provider => provider.ApplyCalls));
  }

  [Fact]
  public async Task ElevatedWorker_StaleTargetStateCannotConsumeApprovedSegment()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("stale-revision-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    var staleHostStore = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var observed = Assert.IsType<ExecutionRun>(
        await staleHostStore.GetAsync(run.RunId, CancellationToken.None));
    var advancingStore = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var planned = Assert.Single(run.Plan!.Resources);
    var advanced = await advancingStore.SaveAsync(observed with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(
          observed.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        [planned.Definition.Id] = observed.ResourceResults[planned.Definition.Id] with
        {
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        }
      }
    }, CancellationToken.None);
    Assert.True(advanced.Revision > observed.Revision);
    var fingerprint = ApprovedResourceFingerprint.Create(
        planned.Definition,
        planned.ResourcePlan);

    var staleClaim = await staleHostStore.ClaimApprovedResourceAsync(
        run.RunId,
        planned.Definition.Id,
        fingerprint,
        CancellationToken.None);

    Assert.Null(staleClaim);
    var provider = new OriginalValueRecordingProvider();
    var freshWorker = new ElevatedResourceWorker(
        new JsonExecutionRunStore(
            paths,
            new LogRedactor(),
            new DeterministicApprovedResourceProtector()),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var result = await freshWorker.ApplyAsync(
        new ElevatedResourceRequest(run.RunId, planned.Definition.Id, fingerprint),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ElevatedWorker_TerminalRunCannotClaimApprovedSegment()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("terminal-race-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    await creator.SaveAsync(
        run with
        {
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Cancelled,
          EndedAtUtc = DateTimeOffset.UtcNow
        },
        CancellationToken.None);
    var planned = Assert.Single(run.Plan!.Resources);
    var provider = new OriginalValueRecordingProvider();
    var worker = new ElevatedResourceWorker(
        new JsonExecutionRunStore(
            paths,
            new LogRedactor(),
            new DeterministicApprovedResourceProtector()),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(planned.Definition, planned.ResourcePlan)),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ElevatedWorker_LockedApprovalSidecarRefusesThenNextRequestSucceeds()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("retry-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await CreateWithApprovedResourceAsync(creator, run);
    var hostStore = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var provider = new OriginalValueRecordingProvider();
    var planned = run.Plan!.Resources.Single();
    var worker = new ElevatedResourceWorker(
        hostStore,
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var request = new ElevatedResourceRequest(
        run.RunId,
        planned.Definition.Id,
        ApprovedResourceFingerprint.Create(
            planned.Definition,
            planned.ResourcePlan));
    ResourceApplyResult refused;

    await using (var sidecarLock = new FileStream(
                     creator.ApprovedResourcesPath(run.RunId),
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.None))
    {
      refused = await worker.ApplyAsync(request, null, CancellationToken.None);
    }

    Assert.Equal(ApplyOutcome.Failed, refused.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, refused.Error!.Code);
    Assert.True(refused.Error.IsRetryable);
    Assert.Equal(0, provider.ApplyCalls);
    var diagnostic = Assert.Single(hostStore.Diagnostics);
    Assert.Equal(WdemErrorCode.PermissionError, diagnostic.Code);
    Assert.True(diagnostic.IsRetryable);
    Assert.IsType<IOException>(diagnostic.UnderlyingException);

    var succeeded = await worker.ApplyAsync(request, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, succeeded.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal("retry-secret", provider.AppliedResource!.Parameters["password"]);
  }

  [Fact]
  public void CurrentUserApprovedResourceProtector_RoundTripsWithBoundEntropy()
  {
    var protector = new CurrentUserApprovedResourceProtector();
    var plaintext = System.Text.Encoding.UTF8.GetBytes("dpapi-secret");
    var entropy = SHA256.HashData("run-resource-fingerprint"u8.ToArray());

    var protectedData = protector.Protect(plaintext, entropy);
    var restored = protector.Unprotect(protectedData, entropy);

    Assert.False(plaintext.SequenceEqual(protectedData));
    Assert.Equal(plaintext, restored);
    Assert.Throws<CryptographicException>(() => protector.Unprotect(
        protectedData,
        SHA256.HashData("different-binding"u8.ToArray())));
  }

  [Fact]
  public async Task TryAcquireRecoveryOperationAsync_PropagatesAndRecordsUnexpectedIoFailure()
  {
    var runId = Guid.NewGuid();
    var expected = new IOException("access denied", unchecked((int)0x80070005));
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw expected);

    var actual = await Assert.ThrowsAsync<IOException>(
        () => store.TryAcquireRecoveryOperationAsync(runId, CancellationToken.None));

    Assert.Same(expected, actual);
    var diagnostic = Assert.Single(store.Diagnostics);
    Assert.Equal(WdemErrorCode.DetectionError, diagnostic.Code);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.Ordinal);
    Assert.Contains(expected.Message, diagnostic.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(unchecked((int)0x80040020))]
  [InlineData(unchecked((int)0x80040021))]
  public async Task TryAcquireRecoveryOperationAsync_PropagatesBusyLowWordFromOtherFacility(
      int hresult)
  {
    var runId = Guid.NewGuid();
    var expected = new IOException("interface failure", hresult);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw expected);

    var actual = await Assert.ThrowsAsync<IOException>(
        () => store.TryAcquireRecoveryOperationAsync(runId, CancellationToken.None));

    Assert.Same(expected, actual);
    var diagnostic = Assert.Single(store.Diagnostics);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.Ordinal);
    Assert.Contains(expected.Message, diagnostic.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(unchecked((int)0x80070020))]
  [InlineData(unchecked((int)0x80070021))]
  public async Task TryAcquireRecoveryOperationAsync_ReturnsBusyForSharingAndLockViolations(
      int hresult)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw new IOException("busy", hresult));

    var operation = await store.TryAcquireRecoveryOperationAsync(
        Guid.NewGuid(),
        CancellationToken.None);

    Assert.Null(operation);
    Assert.Empty(store.Diagnostics);
  }

  [Fact]
  public async Task CreateAndAppendLog_RoundTripsRunAndNeverPersistsSecrets()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        new RunLogEntry(
            1,
            DateTimeOffset.UtcNow,
            ProviderLogLevel.Info,
            "git",
            "git:install",
            "Authorization: Bearer abc.def.ghi",
            new StructuredError(
                WdemErrorCode.ProviderError,
                "token=summary-secret",
                "password=detail-secret"),
            RunEventKind.StepProgress,
            0.75,
            ExecutionState.Completed,
            ExecutionOutcome.Failed),
        CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var log = await File.ReadAllTextAsync(_store.LogPath(run.RunId));

    Assert.True(
        restored is not null,
        string.Join(Environment.NewLine, _store.Diagnostics.Select(error => error.Detail)));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal(run.ProfileId, restored.ProfileId);
    Assert.True(restored.ResourceResults.ContainsKey("GIT"));
    Assert.Equal("git", Assert.Single(restored.Graph!.Nodes).Key);
    Assert.Equal("git", Assert.Single(restored.Plan!.Resources).Definition.Id);
    Assert.Equal("2.52.0", restored.ResourceResults["git"].DetectedBefore!.Version);
    Assert.True(Assert.Single(restored.ResourceResults["git"].StepResults).ProcessSucceeded);
    Assert.DoesNotContain("abc.def.ghi", log, StringComparison.Ordinal);
    Assert.DoesNotContain("summary-secret", log, StringComparison.Ordinal);
    Assert.DoesNotContain("detail-secret", log, StringComparison.Ordinal);
    Assert.Equal("Authorization: Bearer ***", page[0].Message);
    Assert.Equal("token=[REDACTED]", page[0].Error!.Summary);
    Assert.Equal(ExecutionState.Completed, page[0].State);
    Assert.Equal(ExecutionOutcome.Failed, page[0].Outcome);
  }

  [Fact]
  public async Task ReadLogPageAsync_AcceptsLegacyEntryWithoutRestartRequirement()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    const string legacyEntry =
        "{\"sequence\":1,\"timestampUtc\":\"2026-08-30T00:00:00+00:00\"," +
        "\"level\":\"Info\",\"resourceId\":\"git\",\"stepId\":null," +
        "\"message\":\"legacy event\"}\n";
    await File.WriteAllTextAsync(_store.LogPath(run.RunId), legacyEntry);

    var entry = Assert.Single(await _store.ReadLogPageAsync(
        run.RunId,
        0,
        10,
        CancellationToken.None));

    Assert.Equal("legacy event", entry.Message);
    Assert.Null(entry.RestartRequirement);
  }

  [Fact]
  public async Task CreateAsync_RoundTripsRecoveryClaimWithoutWeakeningRedaction()
  {
    var claimId = Guid.NewGuid();
    var claimedAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new MutableTimeProvider(claimedAt));
    var run = SampleRun() with
    {
      Revision = 7,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = claimedAt,
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["token=acknowledgement-secret"],
          StringComparer.OrdinalIgnoreCase),
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = SampleResourceResult() with
        {
          DetectedBefore = MissingEvidence("password=evidence-secret")
        }
      }
    };

    await store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(store.SnapshotPath(run.RunId));
    var restored = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(7, restored!.Revision);
    Assert.Equal(claimId, restored.RecoveryClaimId);
    Assert.Equal(claimedAt, restored.RecoveryClaimedAtUtc);
    Assert.Contains("token=***", restored.AcknowledgedRestartResourceIds);
    Assert.Equal(
        "password=***",
        restored.ResourceResults["git"].DetectedBefore!.Evidence["detail"]);
    Assert.DoesNotContain("acknowledgement-secret", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("evidence-secret", disk, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_RoundTripsRecoveryAssociation()
  {
    var priorRunId = Guid.NewGuid();
    var run = SampleRun() with
    {
      RetriedFromRunId = priorRunId,
      RecoveredFromRunId = priorRunId
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(priorRunId, restored!.RetriedFromRunId);
    Assert.Equal(priorRunId, restored.RecoveredFromRunId);
  }

  [Fact]
  public async Task GetAsync_OldSnapshotWithoutRecoveryAssociationDefaultsToNull()
  {
    var run = SampleRun() with { RetriedFromRunId = Guid.NewGuid() };
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    Assert.True(document.Remove("recoveredFromRunId"));
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.NotNull(restored);
    Assert.Equal(run.RetriedFromRunId, restored.RetriedFromRunId);
    Assert.Null(restored.RecoveredFromRunId);
  }

  [Theory]
  [InlineData(-1, false, false)]
  [InlineData(0, true, false)]
  [InlineData(0, false, true)]
  public async Task CreateAsync_RejectsInvalidRecoveryClaimMetadata(
      long revision,
      bool includeClaimId,
      bool includeClaimedAt)
  {
    var run = SampleRun() with
    {
      Revision = revision,
      RecoveryClaimId = includeClaimId ? Guid.NewGuid() : null,
      RecoveryClaimedAtUtc = includeClaimedAt ? DateTimeOffset.UtcNow : null
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task CreateAsync_RejectsRecoveryClaimTimestampBeyondClockSkew()
  {
    var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var clock = new MutableTimeProvider(now);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        clock);
    var valid = SampleRun() with
    {
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = now.AddMinutes(5)
    };
    await store.CreateAsync(valid, CancellationToken.None);
    var invalid = SampleRun() with
    {
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.MaxValue
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        store.CreateAsync(invalid, CancellationToken.None));

    Assert.NotNull(await store.GetAsync(valid.RunId, CancellationToken.None));
    Assert.False(File.Exists(store.SnapshotPath(invalid.RunId)));
  }

  [Fact]
  public async Task AppendLogAsync_RedactsStandaloneBearerWithoutConsumingDiagnosticText()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        new RunLogEntry(
            1,
            DateTimeOffset.UtcNow,
            ProviderLogLevel.Info,
            "git",
            "git:install",
            "using bearer abc.def.ghi, provider retry scheduled"),
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain("abc.def.ghi", disk, StringComparison.Ordinal);
    Assert.Equal("using bearer ***, provider retry scheduled", Assert.Single(page).Message);
  }

  [Theory]
  [InlineData("abcdefghijklmnop")]
  [InlineData("abcdefghijklmnopqrstuvw")]
  public async Task AppendLogAsync_RedactsOpaqueRfc6750BearerTokensOnDisk(string token)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = $"Bearer {token}" },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain(token, disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***", Assert.Single(page).Message);
  }

  [Fact]
  public async Task AppendLogAsync_RedactsShortBearerTokensFromMessageAndErrorOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with
        {
          Message = "Bearer abc123",
          Error = new StructuredError(
              WdemErrorCode.ProviderError,
              "Bearer hunter2",
              "Bearer detail1")
        },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var entry = Assert.Single(await _store.ReadLogPageAsync(
        run.RunId, 0, 10, CancellationToken.None));

    Assert.DoesNotContain("abc123", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("hunter2", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("detail1", disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***", entry.Message);
    Assert.DoesNotContain("hunter2", entry.Error!.Summary, StringComparison.Ordinal);
    Assert.DoesNotContain("detail1", entry.Error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AppendLogAsync_PreservesBearerProtocolDiagnosticOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = "Bearer RFC6750 support enabled" },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));

    Assert.Contains("Bearer RFC6750 support enabled", disk, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AppendLogAsync_RedactsJwtAndPreservesSentencePeriodOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = "Bearer abc.def.ghi." },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain("abc.def.ghi", disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***.", Assert.Single(page).Message);
  }

  [Fact]
  public async Task CreateAsync_WritesCamelCaseSnapshotAndNormalizesProgress()
  {
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        ["git"] = SampleResourceResult() with
        {
          Progress = double.PositiveInfinity,
          StepResults = [SampleStepResult() with { Progress = -1 }]
        }
      }
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var json = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Contains("\"profileId\"", json, StringComparison.Ordinal);
    Assert.Contains("\"state\": \"Running\"", json, StringComparison.Ordinal);
    Assert.DoesNotContain("\"ProfileId\"", json, StringComparison.Ordinal);
    Assert.Equal(1, restored!.ResourceResults["git"].Progress);
    Assert.Equal(0, restored.ResourceResults["git"].StepResults[0].Progress);
  }

  [Fact]
  public async Task LegacySnapshotWithoutRecoveryMetadataStartsAtRevisionZero()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document.Remove("revision");
    document.Remove("recoveryClaimId");
    document.Remove("recoveryClaimedAtUtc");
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var saved = await _store.SaveAsync(
        restored! with { RestartReasons = ["legacy upgraded"] },
        CancellationToken.None);

    Assert.Equal(0, restored.Revision);
    Assert.Equal(1, saved.Revision);
    Assert.Equal(1, (await _store.GetAsync(run.RunId, CancellationToken.None))!.Revision);
  }

  [Fact]
  public async Task GetAsync_RestoresGraphAndParameterCaseInsensitivityAndImmutability()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    var nodes = restored!.Graph!.Nodes;
    var parameters = nodes["GIT"].Definition.Parameters;
    Assert.Equal("user", parameters["SCOPE"]);
    Assert.True(((ICollection<KeyValuePair<string, ResolvedResource>>)nodes).IsReadOnly);
    Assert.True(((ICollection<KeyValuePair<string, string?>>)parameters).IsReadOnly);
  }

  [Fact]
  public async Task GetAsync_RestoresAllNestedPlanAndGraphCollectionsAsReadOnly()
  {
    var diagnostic = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider unavailable",
        "Provider unavailable");
    var definition = SampleDefinition() with { Dependencies = ["dependency"] };
    var plan = SamplePlan();
    var plannedResource = Assert.Single(plan.Resources) with
    {
      Definition = definition,
      Dependencies = ["dependency"],
      BlockedBy = ["blocker"],
      Diagnostics = [diagnostic],
      ResourcePlan = Assert.Single(plan.Resources).ResourcePlan with
      {
        StructuredErrors = [diagnostic]
      }
    };
    var run = SampleRun() with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            ["git"] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>(["consumer"], StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, ["git"])]),
      Plan = plan with
      {
        Layers = [new ResourceGraphLayer(0, ["git"])],
        Resources = [plannedResource],
        Errors = [diagnostic]
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);

    var restored = (await _store.GetAsync(run.RunId, CancellationToken.None))!;
    var restoredResource = Assert.Single(restored.Plan!.Resources);

    AssertReadOnly(restored.Graph!.TopologicalLayers);
    AssertReadOnly(Assert.Single(restored.Graph.TopologicalLayers).ResourceIds);
    AssertReadOnly(restored.Plan.Layers);
    AssertReadOnly(Assert.Single(restored.Plan.Layers).ResourceIds);
    AssertReadOnly(restored.Plan.Resources);
    AssertReadOnly(restored.Plan.Errors);
    AssertReadOnly(restoredResource.Dependencies);
    AssertReadOnly(restoredResource.BlockedBy);
    AssertReadOnly(restoredResource.Diagnostics);
    AssertReadOnly(restoredResource.Definition.Dependencies);
    AssertReadOnly(restoredResource.ResourcePlan.Steps);
    AssertReadOnly(restoredResource.ResourcePlan.StructuredErrors);
  }

  [Fact]
  public async Task SaveAsync_AtomicallyReplacesSnapshotAndListsOnlyIncompleteRuns()
  {
    var incomplete = SampleRun();
    var complete = SampleRun() with
    {
      RunId = Guid.NewGuid(),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(incomplete, CancellationToken.None);
    await _store.CreateAsync(complete, CancellationToken.None);

    var current = incomplete;
    foreach (var index in Enumerable.Range(1, 8))
    {
      current = await _store.SaveAsync(
          current with { RestartReasons = [$"save-{index}"] },
          CancellationToken.None);
    }

    var discovered = await _store.ListIncompleteAsync(CancellationToken.None);

    Assert.Single(discovered);
    Assert.Equal(incomplete.RunId, discovered[0].RunId);
    Assert.Equal(8, discovered[0].Revision);
    Assert.False(File.Exists(_store.SnapshotPath(incomplete.RunId) + ".tmp"));
    Assert.NotNull(await _store.GetAsync(incomplete.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task ListAsync_ReturnsCompletedAndIncompleteRunsAcrossStoreInstances()
  {
    var incomplete = SampleRun();
    var complete = SampleRun() with
    {
      RunId = Guid.NewGuid(),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(incomplete, CancellationToken.None);
    await _store.CreateAsync(complete, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());

    var allRuns = await otherStore.ListAsync(CancellationToken.None);
    var incompleteRuns = await otherStore.ListIncompleteAsync(CancellationToken.None);

    Assert.Equal(
        new[] { complete.RunId, incomplete.RunId }.Order().ToArray(),
        allRuns.Select(run => run.RunId).Order().ToArray());
    Assert.Equal(incomplete.RunId, Assert.Single(incompleteRuns).RunId);
  }

  [Fact]
  public async Task SaveAsync_PersistsRestartAcknowledgementAcrossStoreInstances()
  {
    var resource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RestartRequirement = RestartPolicy.RestartRequired
    };
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = resource
      },
      RestartRequirements = [RestartPolicy.RestartRequired]
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var secondStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var restored = (await secondStore.GetAsync(run.RunId, CancellationToken.None))!;

    await secondStore.SaveAsync(restored with
    {
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["git"],
          StringComparer.OrdinalIgnoreCase)
    }, CancellationToken.None);

    var thirdStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var acknowledged = (await thirdStore.GetAsync(run.RunId, CancellationToken.None))!;
    Assert.Contains("git", acknowledged.AcknowledgedRestartResourceIds);
    Assert.Equal([RestartPolicy.RestartRequired], acknowledged.RestartRequirements);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        acknowledged.ResourceResults["git"].RestartRequirement);
  }

  [Fact]
  public async Task SaveAsync_RejectsCompletedRunRegression()
  {
    var completed = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = SampleResourceResult() with
        {
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        }
      }
    };
    await _store.CreateAsync(completed, CancellationToken.None);

    await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(
        completed with
        {
          State = ExecutionState.Running,
          Outcome = null,
          EndedAtUtc = null
        },
        CancellationToken.None));

    var restored = await _store.GetAsync(completed.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, restored!.State);
    Assert.Equal(ExecutionOutcome.Succeeded, restored.Outcome);
  }

  [Fact]
  public async Task TrySaveAsync_RejectsTerminalResourceRegression()
  {
    var terminalResource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = terminalResource
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var replacement = run with
    {
      Revision = 1,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = terminalResource with
        {
          State = ExecutionState.Running,
          Outcome = null,
          EndedAtUtc = null
        }
      }
    };

    await Assert.ThrowsAsync<InvalidOperationException>(() => _store.TrySaveAsync(
        replacement,
        expectedRevision: 0,
        expectedRecoveryClaimId: null,
        CancellationToken.None));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, restored!.ResourceResults["git"].State);
    Assert.Equal(ExecutionOutcome.Succeeded, restored.ResourceResults["git"].Outcome);
  }

  public static TheoryData<ExecutionOutcome?, ExecutionOutcome?, bool>
      TerminalResourceOutcomeTransitions
  {
    get
    {
      ExecutionOutcome?[] outcomes =
      [
        null,
        ExecutionOutcome.Succeeded,
        ExecutionOutcome.NotRequired,
        ExecutionOutcome.Failed,
        ExecutionOutcome.Skipped,
        ExecutionOutcome.Cancelled
      ];
      var data = new TheoryData<ExecutionOutcome?, ExecutionOutcome?, bool>();
      foreach (var current in outcomes)
      {
        foreach (var replacement in outcomes)
        {
          data.Add(current, replacement, IsAllowedTerminalTransition(current, replacement));
        }
      }

      return data;
    }
  }

  public static TheoryData<ExecutionOutcome?, ExecutionOutcome?, bool>
      TerminalRunOutcomeTransitions
  {
    get
    {
      ExecutionOutcome[] currentOutcomes =
      [
        ExecutionOutcome.Succeeded,
        ExecutionOutcome.NotRequired,
        ExecutionOutcome.Failed,
        ExecutionOutcome.Skipped,
        ExecutionOutcome.Cancelled
      ];
      ExecutionOutcome?[] replacementOutcomes =
      [
        null,
        ExecutionOutcome.Succeeded,
        ExecutionOutcome.NotRequired,
        ExecutionOutcome.Failed,
        ExecutionOutcome.Skipped,
        ExecutionOutcome.Cancelled
      ];
      var data = new TheoryData<ExecutionOutcome?, ExecutionOutcome?, bool>();
      foreach (var current in currentOutcomes)
      {
        foreach (var replacement in replacementOutcomes)
        {
          data.Add(current, replacement, IsAllowedTerminalTransition(current, replacement));
        }
      }

      return data;
    }
  }

  [Theory]
  [MemberData(nameof(TerminalResourceOutcomeTransitions))]
  public async Task SaveAsync_EnforcesMonotonicTerminalResourceOutcome(
      ExecutionOutcome? currentOutcome,
      ExecutionOutcome? replacementOutcome,
      bool allowed)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var currentResource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = currentOutcome,
      EndedAtUtc = endedAt
    };
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = currentResource
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var replacement = run with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = currentResource with { Outcome = replacementOutcome }
      }
    };

    if (allowed)
    {
      var saved = await _store.SaveAsync(replacement, CancellationToken.None);
      Assert.Equal(replacementOutcome, saved.ResourceResults["git"].Outcome);
    }
    else
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
          _store.SaveAsync(replacement, CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(currentOutcome, restored!.ResourceResults["git"].Outcome);
    }
  }

  [Theory]
  [MemberData(nameof(TerminalRunOutcomeTransitions))]
  public async Task SaveAsync_EnforcesMonotonicTerminalRunOutcome(
      ExecutionOutcome? currentOutcome,
      ExecutionOutcome? replacementOutcome,
      bool allowed)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var terminalResource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = endedAt
    };
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = currentOutcome,
      EndedAtUtc = endedAt,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = terminalResource
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var replacement = run with { Outcome = replacementOutcome };

    if (allowed)
    {
      var saved = await _store.SaveAsync(replacement, CancellationToken.None);
      Assert.Equal(replacementOutcome, saved.Outcome);
    }
    else if (replacementOutcome is null)
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<ArgumentException>(() =>
          _store.SaveAsync(replacement, CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(currentOutcome, restored!.Outcome);
    }
    else
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
          _store.SaveAsync(replacement, CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(currentOutcome, restored!.Outcome);
    }
  }

  [Theory]
  [MemberData(nameof(TerminalResourceOutcomeTransitions))]
  public async Task TrySaveAsync_EnforcesMonotonicTerminalResourceOutcome(
      ExecutionOutcome? currentOutcome,
      ExecutionOutcome? replacementOutcome,
      bool allowed)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var currentResource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = currentOutcome,
      EndedAtUtc = endedAt
    };
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = currentResource
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var replacement = run with
    {
      Revision = 1,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = currentResource with { Outcome = replacementOutcome }
      }
    };

    if (allowed)
    {
      Assert.True(await _store.TrySaveAsync(
          replacement,
          expectedRevision: 0,
          expectedRecoveryClaimId: null,
          CancellationToken.None));
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(replacementOutcome, restored!.ResourceResults["git"].Outcome);
    }
    else
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<InvalidOperationException>(() => _store.TrySaveAsync(
          replacement,
          expectedRevision: 0,
          expectedRecoveryClaimId: null,
          CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(currentOutcome, restored!.ResourceResults["git"].Outcome);
    }
  }

  [Theory]
  [MemberData(nameof(TerminalRunOutcomeTransitions))]
  public async Task TrySaveAsync_EnforcesMonotonicTerminalRunOutcome(
      ExecutionOutcome? currentOutcome,
      ExecutionOutcome? replacementOutcome,
      bool allowed)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var terminalResource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = endedAt
    };
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = currentOutcome,
      EndedAtUtc = endedAt,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = terminalResource
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var replacement = run with { Revision = 1, Outcome = replacementOutcome };

    if (allowed)
    {
      Assert.True(await _store.TrySaveAsync(
          replacement,
          expectedRevision: 0,
          expectedRecoveryClaimId: null,
          CancellationToken.None));
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(replacementOutcome, restored!.Outcome);
    }
    else if (replacementOutcome is null)
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<ArgumentException>(() => _store.TrySaveAsync(
          replacement,
          expectedRevision: 0,
          expectedRecoveryClaimId: null,
          CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      Assert.Equal(before, after);
    }
    else
    {
      var snapshotPath = _store.SnapshotPath(run.RunId);
      var before = await File.ReadAllBytesAsync(snapshotPath);
      await Assert.ThrowsAsync<InvalidOperationException>(() => _store.TrySaveAsync(
          replacement,
          expectedRevision: 0,
          expectedRecoveryClaimId: null,
          CancellationToken.None));
      var after = await File.ReadAllBytesAsync(snapshotPath);
      Assert.Equal(before, after);
    }

    if (!allowed)
    {
      var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
      Assert.Equal(currentOutcome, restored!.Outcome);
    }
  }

  [Fact]
  public async Task CreateAsync_RedactsAcknowledgedRestartResourceIds()
  {
    var run = SampleRun() with
    {
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["token=restart-ack-secret"],
          StringComparer.OrdinalIgnoreCase)
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.DoesNotContain("restart-ack-secret", disk, StringComparison.Ordinal);
    Assert.Contains("token=***", restored!.AcknowledgedRestartResourceIds);
  }

  [Fact]
  public async Task SaveAsync_CoordinatesAcrossStoreInstances()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var payload = new string('x', 256 * 1024);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var saves = Enumerable.Range(1, 16).Select(index =>
    {
      var store = index % 2 == 0 ? _store : otherStore;
      return AttemptSaveAsync(
          store,
          run with { RestartReasons = [$"{index}:{payload}"] },
          start.Task);
    }).ToArray();

    start.SetResult();
    var errors = await Task.WhenAll(saves);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Single(errors, error => error is null);
    Assert.Equal(15, errors.Count(error => error is InvalidOperationException));
    Assert.NotNull(restored);
    Assert.Equal(1, restored.Revision);
    Assert.Single(restored.RestartReasons);
  }

  [Fact]
  public async Task SaveAsync_ConcurrentSameRevisionHasSingleWinner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var saves = new[]
    {
      AttemptSaveAsync(
          _store,
          run with { RestartReasons = ["first"] },
          start.Task),
      AttemptSaveAsync(
          otherStore,
          run with { RestartReasons = ["second"] },
          start.Task)
    };

    start.SetResult();
    var errors = await Task.WhenAll(saves);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Single(errors, error => error is null);
    Assert.Single(errors, error => error is InvalidOperationException);
    Assert.Equal(1, restored!.Revision);
    Assert.Single(restored.RestartReasons);
  }

  [Fact]
  public async Task SaveAsync_WithoutClaimOwnerCannotClearActiveClaim()
  {
    var claimId = Guid.NewGuid();
    var claimed = SampleRun() with
    {
      Revision = 3,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(claimed, CancellationToken.None);

    await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(
        claimed with
        {
          RecoveryClaimId = null,
          RecoveryClaimedAtUtc = null
        },
        CancellationToken.None));

    var restored = await _store.GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Equal(3, restored!.Revision);
    Assert.Equal(claimId, restored.RecoveryClaimId);
  }

  [Fact]
  public async Task TrySaveAsync_RejectsTerminalizationByWrongClaimOwner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var claimId = Guid.NewGuid();
    var claimed = run with
    {
      Revision = 1,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    Assert.True(await _store.TrySaveAsync(
        claimed,
        expectedRevision: 0,
        expectedRecoveryClaimId: null,
        CancellationToken.None));
    var terminal = claimed with
    {
      Revision = 2,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Cancelled,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };

    var saved = await _store.TrySaveAsync(
        terminal,
        expectedRevision: 1,
        expectedRecoveryClaimId: Guid.NewGuid(),
        CancellationToken.None);

    var persisted = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.False(saved);
    Assert.Equal(claimId, persisted!.RecoveryClaimId);
    Assert.Equal(1, persisted.Revision);
  }

  [Fact]
  public async Task TrySaveAsync_StaleClaimOwnerCannotOverwriteNewOwner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var firstClaimId = Guid.NewGuid();
    var firstClaim = run with
    {
      Revision = 1,
      RecoveryClaimId = firstClaimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
    };
    Assert.True(await _store.TrySaveAsync(
        firstClaim,
        expectedRevision: 0,
        expectedRecoveryClaimId: null,
        CancellationToken.None));
    var secondClaimId = Guid.NewGuid();
    var secondClaim = firstClaim with
    {
      Revision = 2,
      RecoveryClaimId = secondClaimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    Assert.True(await _store.TrySaveAsync(
        secondClaim,
        expectedRevision: 1,
        expectedRecoveryClaimId: firstClaimId,
        CancellationToken.None));

    var staleTerminal = firstClaim with
    {
      Revision = 2,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };
    var saved = await _store.TrySaveAsync(
        staleTerminal,
        expectedRevision: 1,
        expectedRecoveryClaimId: firstClaimId,
        CancellationToken.None);

    var persisted = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.False(saved);
    Assert.Equal(2, persisted!.Revision);
    Assert.Equal(secondClaimId, persisted.RecoveryClaimId);
    Assert.Equal(ExecutionState.Running, persisted.State);
  }

  [Fact]
  public async Task ReadLogPageAsync_OrdersAndPagesByExclusiveSequence()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 5))
    {
      await _store.AppendLogAsync(
          run.RunId,
          SampleLog(sequence),
          CancellationToken.None);
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 2, 2, CancellationToken.None);

    Assert.Equal([3L, 4L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task ReadLogPageAsync_SeeksFromCursorWithoutParsingEarlierPages()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 3))
    {
      await _store.AppendLogAsync(run.RunId, SampleLog(sequence), CancellationToken.None);
    }

    await using (var stream = new FileStream(
        _store.LogPath(run.RunId), FileMode.Open, FileAccess.Write, FileShare.Read))
    {
      stream.WriteByte((byte)'!');
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 2, 10, CancellationToken.None);

    Assert.Equal(3, Assert.Single(page).Sequence);
  }

  [Fact]
  public async Task AppendLogAsync_RejectsDuplicateOrOutOfOrderSequence()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None));
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None));
  }

  [Theory]
  [InlineData("level")]
  [InlineData("error")]
  [InlineData("kind")]
  [InlineData("state")]
  [InlineData("outcome")]
  [InlineData("restartRequirement")]
  public async Task AppendLogAsync_RejectsUndefinedEnumsBeforeWriting(string invalidField)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var entry = invalidField switch
    {
      "error" => SampleLog(1) with
      {
        Error = new StructuredError(
              (WdemErrorCode)999,
              "Invalid code",
              "Invalid code")
      },
      "kind" => SampleLog(1) with { Kind = (RunEventKind)999 },
      "state" => SampleLog(1) with { State = (ExecutionState)999 },
      "outcome" => SampleLog(1) with { Outcome = (ExecutionOutcome)999 },
      "restartRequirement" => SampleLog(1) with
      {
        RestartRequirement = (RestartPolicy)999
      },
      _ => SampleLog(1) with { Level = (ProviderLogLevel)999 }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.AppendLogAsync(run.RunId, entry, CancellationToken.None));
    Assert.False(File.Exists(_store.LogPath(run.RunId)));
  }

  [Fact]
  public async Task AppendLogAsync_CoordinatesLogAndIndexAcrossStoreInstances()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var payload = new string('x', 256 * 1024);

    foreach (var sequence in Enumerable.Range(1, 16))
    {
      var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      async Task AppendAfterStartAsync(JsonExecutionRunStore store)
      {
        await start.Task;
        await store.AppendLogAsync(
            run.RunId,
            SampleLog(sequence) with { Message = payload },
            CancellationToken.None);
      }

      var attempts = new[]
      {
        Record.ExceptionAsync(() => AppendAfterStartAsync(_store)),
        Record.ExceptionAsync(() => AppendAfterStartAsync(otherStore))
      };
      start.SetResult();
      var errors = await Task.WhenAll(attempts);

      Assert.Single(errors, error => error is null);
      Assert.Single(errors, error => error is InvalidOperationException);
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 100, CancellationToken.None);
    Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value),
        page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_TrimsCrashPartialTailBeforeAppending()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    await File.AppendAllTextAsync(_store.LogPath(run.RunId), "{\"sequence\":");

    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal([1L, 2L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_PreservesCompleteTailMissingFinalNewline()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    var logPath = _store.LogPath(run.RunId);
    var existing = (await File.ReadAllTextAsync(logPath)).TrimEnd('\r', '\n');
    await File.WriteAllTextAsync(logPath, existing);

    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal([1L, 2L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_UsesPersistedTailMetadataInsteadOfRescanningHistory()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);
    await using (var stream = new FileStream(
        _store.LogPath(run.RunId), FileMode.Open, FileAccess.Write, FileShare.Read))
    {
      stream.WriteByte((byte)'!');
    }

    await _store.AppendLogAsync(run.RunId, SampleLog(3), CancellationToken.None);

    var tail = File.ReadLines(_store.LogPath(run.RunId)).Last();
    Assert.Contains("\"sequence\":3", tail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("sequence")]
  [InlineData("start")]
  [InlineData("end")]
  public async Task ReadLogPageAsync_AtomicallyRebuildsAnyCorruptCompleteIndexRecord(
      string corruptField)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 3))
    {
      await _store.AppendLogAsync(run.RunId, SampleLog(sequence), CancellationToken.None);
    }

    const int recordSize = sizeof(long) * 3;
    var indexPath = _store.LogIndexPath(run.RunId);
    var indexBytes = await File.ReadAllBytesAsync(indexPath);
    var secondRecord = recordSize;
    var corruptOffset = corruptField switch
    {
      "sequence" => secondRecord,
      "start" => secondRecord + sizeof(long),
      _ => secondRecord + (sizeof(long) * 2)
    };
    var corruptValue = corruptField switch
    {
      "sequence" => 99L,
      "start" => 0L,
      _ => BinaryPrimitives.ReadInt64LittleEndian(
          indexBytes.AsSpan((recordSize * 2) + (sizeof(long) * 2), sizeof(long)))
    };
    BinaryPrimitives.WriteInt64LittleEndian(
        indexBytes.AsSpan(corruptOffset, sizeof(long)),
        corruptValue);
    await File.WriteAllBytesAsync(indexPath, indexBytes);
    var reopenedStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());

    var page = await reopenedStore.ReadLogPageAsync(
        run.RunId, 0, 10, CancellationToken.None);

    Assert.Equal([1L, 2L, 3L], page.Select(entry => entry.Sequence));
    var repairedIndex = await File.ReadAllBytesAsync(indexPath);
    Assert.Equal(2L, BinaryPrimitives.ReadInt64LittleEndian(
        repairedIndex.AsSpan(secondRecord, sizeof(long))));
    Assert.Empty(Directory.GetFiles(
        Path.GetDirectoryName(indexPath)!,
        Path.GetFileName(indexPath) + ".*.tmp"));
  }

  [Theory]
  [InlineData(-1, 1)]
  [InlineData(0, 0)]
  [InlineData(0, 1001)]
  public async Task ReadLogPageAsync_RejectsInvalidPagination(long afterSequence, int take)
  {
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        _store.ReadLogPageAsync(Guid.NewGuid(), afterSequence, take, CancellationToken.None));
  }

  [Fact]
  public async Task Operations_HaveDeterministicMissingAndDuplicateRunBehavior()
  {
    var run = SampleRun();

    Assert.Null(await _store.GetAsync(run.RunId, CancellationToken.None));
    await _store.CreateAsync(run, CancellationToken.None);
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.SaveAsync(run with { RunId = Guid.NewGuid() }, CancellationToken.None));
    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.AppendLogAsync(Guid.NewGuid(), SampleLog(1), CancellationToken.None));
  }

  [Fact]
  public async Task GetAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();
    Directory.CreateDirectory(new WdemDataPaths(_directory).RunsDirectory);

    Assert.Null(await _store.GetAsync(runId, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task SaveAsync_MissingRunDoesNotLeaveLockFile()
  {
    var run = SampleRun();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.SaveAsync(run, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{run.RunId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task AppendLogAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.AppendLogAsync(runId, SampleLog(1), CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task ReadLogPageAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.ReadLogPageAsync(runId, 0, 10, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Theory]
  [InlineData(null, true)]
  [InlineData(ExecutionOutcome.Succeeded, false)]
  public async Task CreateAsync_RejectsCompletedRunMissingTerminalFields(
      ExecutionOutcome? outcome,
      bool includeEndedAt)
  {
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = outcome,
      EndedAtUtc = includeEndedAt ? DateTimeOffset.UtcNow : null
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task SaveAsync_RejectsTerminalFieldsOnIncompleteRun()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);

    await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(
        run with
        {
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        },
        CancellationToken.None));
  }

  [Fact]
  public async Task Operation_ObservesPreCancelledTokenBeforeIo()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        _store.CreateAsync(SampleRun(), cancellation.Token));
    Assert.False(Directory.Exists(new WdemDataPaths(_directory).RunsDirectory));
  }

  [Fact]
  public void AtomicPersistence_UsesOneSharedDurableByteWriter()
  {
    var privateMethods = typeof(JsonExecutionRunStore).GetMethods(
        System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Static |
        System.Reflection.BindingFlags.Instance);

    Assert.DoesNotContain(privateMethods, method => method.Name == "WriteSnapshotAsync");
    Assert.Single(privateMethods, method => method.Name == "WriteBytesAtomicallyAsync");
  }

  [Fact]
  public async Task ListIncompleteAsync_PreservesMalformedSnapshotAndExposesDiagnostic()
  {
    Directory.CreateDirectory(new WdemDataPaths(_directory).RunsDirectory);
    var runId = Guid.NewGuid();
    var snapshotPath = _store.SnapshotPath(runId);
    await File.WriteAllTextAsync(snapshotPath, "{ malformed json");

    var runs = await _store.ListIncompleteAsync(CancellationToken.None);

    Assert.Empty(runs);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{runId:D}.json.corrupted.*"));
    var diagnostic = Assert.Single(_store.Diagnostics);
    Assert.Equal(WdemErrorCode.DetectionError, diagnostic.Code);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("selectedOptionalResourceIds")]
  [InlineData("profileId")]
  public async Task GetAsync_PreservesSnapshotWithNullRequiredMember(string propertyName)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document[propertyName] = null;
    await File.WriteAllTextAsync(
        snapshotPath,
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task GetAsync_PreservesSnapshotWithInvalidStateCombination()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["state"] = "Completed";
    await File.WriteAllTextAsync(
        snapshotPath,
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task GetAsync_PreservesSnapshotWithNullPlanResource()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["plan"]!["resources"]!.AsArray()[0] = null;
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Theory]
  [InlineData("number")]
  [InlineData("numericString")]
  [InlineData("unknownString")]
  public async Task GetAsync_PreservesSnapshotWithInvalidEnum(string invalidKind)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["state"] = invalidKind switch
    {
      "number" => JsonValue.Create(999),
      "numericString" => JsonValue.Create("999"),
      _ => JsonValue.Create("UnknownState")
    };
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task GetAsync_PreservesSnapshotWithMalformedExecutionPreconditionFingerprint()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["plan"]!["resources"]!.AsArray()[0]!["resourcePlan"]![
        "executionPreconditionFingerprint"] = "not-a-sha256";
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task CreateAndSave_RedactSnapshotMessagesAndErrors()
  {
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        ["git"] = SampleResourceResult() with
        {
          Message = "password=snapshot-secret",
          Error = new StructuredError(
              WdemErrorCode.ProviderError,
              "token=snapshot-summary",
              "Authorization: Bearer snapshot.detail.token")
        }
      }
    };

    await _store.CreateAsync(run, CancellationToken.None);
    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.DoesNotContain("snapshot-secret", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("snapshot-summary", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("snapshot.detail.token", disk, StringComparison.Ordinal);
    Assert.Equal("password=***", restored!.ResourceResults["git"].Message);
  }

  [Fact]
  public async Task CreateAsync_RedactsSensitiveProfileParametersByKey()
  {
    var definition = SampleDefinition() with
    {
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["clientSecret"] = "hunter2",
        ["access_token"] = "abc123",
        ["githubToken"] = "github-credential",
        ["databasePassword"] = "database-credential",
        ["serviceSecret"] = "service-credential",
        ["api-key"] = "api-credential",
        ["thumb_print"] = "thumbprint-credential",
        ["scope"] = "user",
        ["passwordPolicy"] = "standard"
      }
    };
    var run = SampleRun() with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            ["git"] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, ["git"])])
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var parameters = restored!.Graph!.Nodes["git"].Definition.Parameters;

    Assert.DoesNotContain("hunter2", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("abc123", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("github-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("database-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("service-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("api-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("thumbprint-credential", disk, StringComparison.Ordinal);
    Assert.Equal("***", parameters["clientSecret"]);
    Assert.Equal("***", parameters["access_token"]);
    Assert.Equal("***", parameters["githubToken"]);
    Assert.Equal("***", parameters["databasePassword"]);
    Assert.Equal("***", parameters["serviceSecret"]);
    Assert.Equal("***", parameters["api-key"]);
    Assert.Equal("***", parameters["thumb_print"]);
    Assert.Equal("user", parameters["scope"]);
    Assert.Equal("standard", parameters["passwordPolicy"]);
  }

  [Fact]
  public void WindowsMachineInformationProvider_CapturesFactsFromItsEnvironmentAbstraction()
  {
    var machine = new WindowsMachineInformationProvider(new StubMachineInformationSource())
        .GetMachineInformation();

    Assert.Equal("Windows test version", machine.OperatingSystem);
    Assert.Equal("Arm64", machine.Architecture);
    Assert.Equal("TESTBOX", machine.ComputerName);
    Assert.Equal("test-user", machine.UserName);
  }

  private static bool IsAllowedTerminalTransition(
      ExecutionOutcome? current,
      ExecutionOutcome? replacement) =>
      current == replacement ||
      current == ExecutionOutcome.Cancelled &&
      replacement is ExecutionOutcome.Succeeded or
          ExecutionOutcome.NotRequired or
          ExecutionOutcome.Failed;

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private static ExecutionRun SampleRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = @"C:\profiles\developer.json",
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(["git"], StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow,
    State = ExecutionState.Running,
    Machine = new MachineInformation("Windows", "X64", "DEVBOX", "developer"),
    Graph = SampleGraph(),
    Plan = SamplePlan(),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = SampleResourceResult()
    },
    RestartRequirements = [RestartPolicy.RestartRecommended],
    RestartReasons = ["PATH changed"]
  };

  private static Task CreateWithApprovedResourceAsync(
      JsonExecutionRunStore store,
      ExecutionRun run)
  {
    var planned = Assert.Single(run.Plan!.Resources);
    return store.CreateAsync(
        run,
        [new ApprovedResourceSeal(planned.Definition, planned.ResourcePlan)],
        CancellationToken.None);
  }

  private static async Task<ExecutionRun> CreateWithTwoApprovedResourcesAsync(
      JsonExecutionRunStore store)
  {
    var run = ElevatedRunWithSecret("first-approved-secret");
    var first = Assert.Single(run.Plan!.Resources);
    var secondDefinition = first.Definition with
    {
      Id = "node",
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = "second-approved-secret"
      }
    };
    var secondPlan = first.ResourcePlan with
    {
      ResourceId = secondDefinition.Id,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(secondDefinition)
    };
    var second = first with
    {
      Definition = secondDefinition,
      ResourcePlan = secondPlan
    };
    run = run with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            [first.Definition.Id] = run.Graph!.Nodes[first.Definition.Id],
            [secondDefinition.Id] = new(
                secondDefinition,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, [first.Definition.Id, secondDefinition.Id])]),
      Plan = run.Plan with
      {
        Layers = [new ResourceGraphLayer(0, [first.Definition.Id, secondDefinition.Id])],
        Resources = [first, second]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(
          run.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        [secondDefinition.Id] = run.ResourceResults[first.Definition.Id] with
        {
          ResourceId = secondDefinition.Id
        }
      }
    };
    await store.CreateAsync(
        run,
        [
          new ApprovedResourceSeal(first.Definition, first.ResourcePlan),
          new ApprovedResourceSeal(second.Definition, second.ResourcePlan)
        ],
        CancellationToken.None);
    return run;
  }

  private static ExecutionRun ElevatedRunWithSecret(string secret)
  {
    var run = SampleRun();
    var planned = run.Plan!.Resources.Single();
    var definition = planned.Definition with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = secret
      }
    };
    var plan = planned.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(definition),
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          PrivilegeRequirement = PrivilegeRequirement.Administrator
        }
      ]
    };
    return run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          planned with
          {
            Definition = definition,
            ResourcePlan = plan,
            RequiresElevation = true,
            Risk = PlanRisk.Elevated
          }
        ]
      }
    };
  }

  private static ExecutionRun DeferredElevatedRun(string secret)
  {
    var run = SampleRun();
    var planned = run.Plan!.Resources.Single();
    var definition = planned.Definition with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = secret
      }
    };
    var placeholder = planned.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(definition),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = false,
      Steps =
      [
        new PlanStep
        {
          Id = "deferred-refinement",
          Description = "Authorize deferred install after dependency re-detection.",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart,
          Reason = "Plan after dependency re-detection."
        }
      ]
    };
    var initialPlanFingerprint = new string('D', 64);
    return run with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            [definition.Id] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, [definition.Id])]),
      Plan = run.Plan with
      {
        Fingerprint = initialPlanFingerprint,
        Resources =
        [
          planned with
          {
            Definition = definition,
            ResourcePlan = placeholder,
            Status = PlannedResourceStatus.Deferred,
            RequiresElevation = true,
            Risk = PlanRisk.Elevated,
            DeferredAuthorization = new DeferredPlanAuthorization
            {
              AllowedActions = [PlanAction.Install],
              MaximumPrivilege = PrivilegeRequirement.Administrator,
              MaximumRestartPolicy = RestartPolicy.NoRestart,
              MaximumRisk = PlanRisk.Elevated,
              AllowDestructive = false,
              DynamicPlanNotice = "Plan after dependency re-detection."
            }
          }
        ]
      },
      PlanApproval = new PlanApproval
      {
        InitialPlanFingerprint = initialPlanFingerprint,
        ConfirmedAtUtc = DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
        Source = PlanApprovalSource.DesktopReviewedPlan,
        DeferredAuthorizations =
        [
          new DeferredAuthorizationProof
          {
            ResourceId = definition.Id,
            ResourceType = definition.Type,
            ProviderName = definition.Provider,
            DefinitionFingerprint = placeholder.DesiredStateFingerprint,
            Origin = ResourceOrigin.Required,
            Dependencies = [],
            AllowedActions = [PlanAction.Install],
            MaximumPrivilege = PrivilegeRequirement.Administrator,
            MaximumRestartPolicy = RestartPolicy.NoRestart,
            MaximumRisk = PlanRisk.Elevated,
            AllowDestructive = false
          }
        ]
      }
    };
  }

  private static ExecutionRun PromoteDeferredRun(
      ExecutionRun run,
      ResourcePlan executablePlan)
  {
    var deferred = Assert.Single(run.Plan!.Resources);
    return run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          deferred with
          {
            ResourcePlan = executablePlan,
            Status = PlannedResourceStatus.Ready,
            Risk = PlanRisk.Elevated,
            RequiresElevation = true,
            DeferredAuthorization = null
          }
        ]
      }
    };
  }

  private static ExecutionRun DeferredElevatedRunWithTwoResources()
  {
    var run = DeferredElevatedRun("first-deferred-secret");
    var first = Assert.Single(run.Plan!.Resources);
    var firstResolved = run.Graph!.Nodes[first.Definition.Id];
    var firstProof = Assert.Single(run.PlanApproval!.DeferredAuthorizations);
    var secondDefinition = first.Definition with
    {
      Id = "node",
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = "second-deferred-secret"
      }
    };
    var secondPlan = first.ResourcePlan with
    {
      ResourceId = secondDefinition.Id,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(secondDefinition)
    };
    var second = first with
    {
      Definition = secondDefinition,
      ResourcePlan = secondPlan
    };
    return run with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            [first.Definition.Id] = firstResolved,
            [secondDefinition.Id] = new(
                secondDefinition,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, [first.Definition.Id, secondDefinition.Id])]),
      Plan = run.Plan with
      {
        Layers = [new ResourceGraphLayer(0, [first.Definition.Id, secondDefinition.Id])],
        Resources = [first, second]
      },
      PlanApproval = run.PlanApproval with
      {
        DeferredAuthorizations =
        [
          firstProof,
          firstProof with
          {
            ResourceId = secondDefinition.Id,
            DefinitionFingerprint = secondPlan.DesiredStateFingerprint
          }
        ]
      },
      ResourceResults = new Dictionary<string, ResourceResult>(
          run.ResourceResults,
          StringComparer.OrdinalIgnoreCase)
      {
        [secondDefinition.Id] = run.ResourceResults[first.Definition.Id] with
        {
          ResourceId = secondDefinition.Id
        }
      }
    };
  }

  private static ResourcePlan ExecutableDeferredPlan(PlannedResource resource) =>
      resource.ResourcePlan with
      {
        IsExecutable = true,
        Steps =
        [
          new PlanStep
          {
            Id = $"install-{resource.Definition.Id}",
            Description = $"Install deferred resource '{resource.Definition.Id}'",
            Action = PlanAction.Install,
            PrivilegeRequirement = PrivilegeRequirement.Administrator,
            RestartPolicy = RestartPolicy.NoRestart
          }
        ]
      };

  private static ExecutionRun PromoteDeferredResources(
      ExecutionRun run,
      params (string ResourceId, ResourcePlan Plan)[] promotions) => run with
      {
        Plan = run.Plan! with
        {
          Resources = run.Plan.Resources.Select(resource =>
          {
            var promotion = promotions.SingleOrDefault(candidate => string.Equals(
                candidate.ResourceId,
                resource.Definition.Id,
                StringComparison.OrdinalIgnoreCase));
            return promotion.Plan is null
                ? resource
                : resource with
                {
                  ResourcePlan = promotion.Plan,
                  Status = PlannedResourceStatus.Ready,
                  Risk = PlanRisk.Elevated,
                  RequiresElevation = true,
                  DeferredAuthorization = null
                };
          }).ToArray()
        }
      };

  private static ResourceResult SampleResourceResult() => new()
  {
    ResourceId = "git",
    State = ExecutionState.Running,
    FinalCompliance = ComplianceStatus.VersionMismatch,
    DetectedBefore = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = "2.52.0",
      InstalledVersions = [new SemanticVersion(2, 52, 0)],
      Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["provider"] = "winget"
      }
    },
    Progress = 0.5,
    Message = "Installing",
    StartedAtUtc = DateTimeOffset.UtcNow,
    RestartRequirement = RestartPolicy.NoRestart,
    StepResults = [SampleStepResult()]
  };

  private static DetectedState MissingEvidence(string detail) => new()
  {
    ResourceId = "git",
    Outcome = DetectionOutcome.Succeeded,
    Exists = false,
    Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["detail"] = detail
    }
  };

  private static StepResult SampleStepResult() => new()
  {
    StepId = "git:install",
    Name = "Install Git",
    State = ExecutionState.Running,
    Progress = 0.5,
    FirstLogSequence = 1,
    LastLogSequence = 1,
    ProcessExitCode = 3010,
    ProcessSucceeded = true
  };

  private static ResourceGraph SampleGraph()
  {
    var definition = SampleDefinition();
    return new ResourceGraph(
        new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = new(
              definition,
              ResourceOrigin.Required,
              new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        },
        [new ResourceGraphLayer(0, ["git"])]);
  }

  private static ExecutionPlan SamplePlan()
  {
    var definition = SampleDefinition();
    var resourcePlan = new ResourcePlan
    {
      ResourceId = "git",
      ResourceType = "package",
      ProviderName = "winget",
      DesiredStateFingerprint = "fingerprint",
      Compliance = ComplianceStatus.VersionMismatch,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "git:install",
          Description = "Install Git",
          Action = PlanAction.Upgrade,
          PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    return new ExecutionPlan
    {
      PlanId = Guid.NewGuid(),
      Fingerprint = "plan-fingerprint",
      ProfileId = "developer",
      ProfileVersion = "1.0.0",
      Layers = [new ResourceGraphLayer(0, ["git"])],
      Resources =
      [
        new PlannedResource
        {
          Definition = definition,
          Origin = ResourceOrigin.Required,
          Dependencies = [],
          ResourcePlan = resourcePlan,
          Status = PlannedResourceStatus.Ready,
          Risk = PlanRisk.Standard,
          RequiresElevation = false,
          IsDestructive = false,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ],
      IsExecutable = true
    };
  }

  private static ResourceDefinition SampleDefinition() => new()
  {
    Id = "git",
    Type = "package",
    Provider = "winget",
    VersionConstraint = ">=2.52.1",
    Dependencies = [],
    Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
      ["scope"] = "user"
    },
    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
    RestartPolicy = RestartPolicy.NoRestart
  };

  private static RunLogEntry SampleLog(int sequence) => new(
      sequence,
      DateTimeOffset.UtcNow.AddSeconds(sequence),
      ProviderLogLevel.Info,
      "git",
      "git:install",
      $"message-{sequence}");

  private static async Task<Exception?> AttemptSaveAsync(
      IExecutionRunStore store,
      ExecutionRun run,
      Task start)
  {
    await start;
    try
    {
      await store.SaveAsync(run, CancellationToken.None);
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private static void AssertReadOnly<T>(IReadOnlyList<T> values)
  {
    var collection = Assert.IsAssignableFrom<ICollection<T>>(values);
    Assert.True(collection.IsReadOnly);
    Assert.Throws<NotSupportedException>(() => collection.Add(values[0]));
  }

  private sealed class StubMachineInformationSource : IWindowsMachineInformationSource
  {
    public string OperatingSystem => "Windows test version";
    public string Architecture => "Arm64";
    public string ComputerName => "TESTBOX";
    public string UserName => "test-user";
  }

  private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }

  private sealed class DeterministicApprovedResourceProtector : IApprovedResourceProtector
  {
    private readonly byte[] _key;

    public DeterministicApprovedResourceProtector(string identity = "test-user")
    {
      _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
    }

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
      var nonce = SHA256.HashData(entropy)[..12];
      var ciphertext = new byte[plaintext.Length];
      var tag = new byte[16];
      using var aes = new AesGcm(_key, tag.Length);
      aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
      return [.. nonce, .. tag, .. ciphertext];
    }

    public byte[] Unprotect(byte[] protectedData, byte[] entropy)
    {
      var plaintext = new byte[protectedData.Length - 28];
      using var aes = new AesGcm(_key, 16);
      aes.Decrypt(
          protectedData.AsSpan(0, 12),
          protectedData.AsSpan(28),
          protectedData.AsSpan(12, 16),
          plaintext,
          entropy);
      return plaintext;
    }
  }

  private sealed class PassThroughApprovedResourceProtector : IApprovedResourceProtector
  {
    public byte[] Protect(byte[] plaintext, byte[] entropy) => plaintext;

    public byte[] Unprotect(byte[] protectedData, byte[] entropy) => protectedData;
  }

  private sealed class OriginalValueRecordingProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "winget";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }
    public ResourceDefinition? AppliedResource { get; private set; }

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
        Outcome = ApplyOutcome.Succeeded
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
}
