using System.ComponentModel;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Execution;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class NamedPipePrivilegeBrokerTests
{
  [Fact]
  public async Task ApplyAsync_TwoAdministratorResources_StartsOneElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    await broker.ApplyAsync(Request(runId, "visual-studio"), null, CancellationToken.None);
    await broker.ApplyAsync(Request(runId, "vsix"), null, CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(2, launcher.Session.Requests.Count);
    Assert.All(launcher.Session.Requests, request =>
    {
      Assert.Equal(runId, request.RunId);
    });
    Assert.Single(launcher.PipeNames);
  }

  [Fact]
  public async Task ApplyAsync_UacDeclined_ReturnsPermissionError()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);

    var result = await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_UacDeclinedTwiceInOneRun_StartsElevatedHostOnce()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var first = await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    var second = await broker.ApplyAsync(
        Request(runId, "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(ApplyOutcome.Cancelled, second.Outcome);
    Assert.Equal(first.Error!.Code, second.Error!.Code);
    Assert.Equal(first.Error.Summary, second.Error.Summary);
    Assert.Equal(first.Error.Detail, second.Error.Detail);
    Assert.Equal("vsix", second.ResourceId);
    Assert.Equal("vsix", second.Error.ResourceId);
  }

  [Fact]
  public async Task ApplyAsync_ConcurrentUacDeclinesInOneRun_StartElevatedHostOnce()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var results = await Task.WhenAll(
        broker.ApplyAsync(
            Request(runId, "visual-studio"),
            null,
            CancellationToken.None),
        broker.ApplyAsync(
            Request(runId, "vsix"),
            null,
            CancellationToken.None));

    Assert.Equal(1, launcher.StartCalls);
    Assert.All(results, result =>
    {
      Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
      Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
      Assert.Equal("Administrator approval was declined.", result.Error.Summary);
    });
  }

  [Fact]
  public async Task ApplyAsync_UacDeclinedForDifferentRuns_RetriesElevatedLaunch()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);

    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);
    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(2, launcher.StartCalls);
  }

  [Fact]
  public async Task ApplyAsync_ElevatedHostLaunchFails_ReturnsCachedPermissionError()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new InvalidOperationException("host unavailable")
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var first = await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    var second = await broker.ApplyAsync(
        Request(runId, "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(ApplyOutcome.Failed, first.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, first.Error!.Code);
    Assert.Equal("Elevated host could not be started.", first.Error.Summary);
    Assert.Equal(first.Error.Summary, second.Error!.Summary);
    Assert.Equal(first.Error.Detail, second.Error.Detail);
    Assert.Equal("vsix", second.Error.ResourceId);
  }

  [Fact]
  public void ElevatedResourceRequest_ContainsOnlyApprovedIdentifiers()
  {
    var properties = typeof(ElevatedResourceRequest)
        .GetProperties()
        .Select(property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    Assert.Equal(
        ["PlanFingerprint", "ResourceId", "RunId"],
        properties);
  }

  [Fact]
  public async Task ApplyAsync_CancellationTerminatesElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    using var cancellation = new CancellationTokenSource();

    var apply = broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        cancellation.Token);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task ApplyAsync_CancellationUsesSharedDeadlineForRunCleanup()
  {
    var drainBudget = TimeSpan.FromMilliseconds(150);
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    launcher.Session.WaitForTerminationRelease = true;
    launcher.Session.IgnoreTerminationCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    using var cancellation = new CancellationTokenSource();
    using var deadline = new CancellationDrainDeadline(drainBudget, cancellation.Token);

    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        cancellation.Token,
        deadline);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();

    try
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => apply.WaitAsync(TimeSpan.FromSeconds(1)));
      var late = await broker.ApplyAsync(
          Request(runId, "vsix"),
          null,
          CancellationToken.None);

      AssertClosedRunFailure(late, "vsix");
      Assert.Equal(1, launcher.StartCalls);
      Assert.Equal(1, launcher.Session.TerminateCalls);
      Assert.Equal(1, launcher.Session.DisposeCalls);
    }
    finally
    {
      launcher.Session.TerminationRelease.TrySetResult();
      await launcher.Session.TerminationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    await broker.CompleteRunAsync(runId, CancellationToken.None);
    await broker.DisposeAsync();

    Assert.Equal(1, launcher.Session.TerminateCalls);
    Assert.Equal(1, launcher.Session.DisposeCalls);
  }

  [Fact]
  public async Task ApplyAsync_DifferentRuns_StartDifferentElevatedHosts()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);

    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);
    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(2, launcher.StartCalls);
    Assert.Equal(2, launcher.PipeNames.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public async Task CompleteRunAsync_TerminatesElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);

    await broker.CompleteRunAsync(runId, CancellationToken.None);

    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task CompleteRunAsync_RacingAndLateApply_ReturnClosedFailureWithoutRestart()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForTerminationRelease = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);

    var completing = broker.CompleteRunAsync(runId, CancellationToken.None);
    await launcher.Session.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    ResourceApplyResult racing;
    try
    {
      racing = await broker.ApplyAsync(
          Request(runId, "vsix"),
          null,
          CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally
    {
      launcher.Session.TerminationRelease.TrySetResult();
      await completing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    var late = await broker.ApplyAsync(
        Request(runId, "windows-feature"),
        null,
        CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    AssertClosedRunFailure(racing, "vsix");
    AssertClosedRunFailure(late, "windows-feature");
  }

  [Fact]
  public async Task CompleteRunAsync_ActiveApplyDrainsBeforeHostTermination()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    using var cancellation = new CancellationTokenSource();
    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        cancellation.Token);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var completing = broker.CompleteRunAsync(runId, CancellationToken.None);
    var completedBeforeApplyDrained = completing.IsCompleted;
    var terminatedBeforeApplyDrained = launcher.Session.Terminated.Task.IsCompleted;
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
    await completing.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(completedBeforeApplyDrained);
    Assert.False(terminatedBeforeApplyDrained);
    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task CompleteRunAsync_CancellationBoundsActiveApplyDrainAndKeepsRunClosed()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForApplyRelease = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    using var cleanupCancellation = new CancellationTokenSource(
        TimeSpan.FromMilliseconds(100));

    var completing = broker.CompleteRunAsync(runId, cleanupCancellation.Token);
    try
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => completing.WaitAsync(TimeSpan.FromSeconds(1)));
      var late = await broker.ApplyAsync(
          Request(runId, "vsix"),
          null,
          CancellationToken.None);

      AssertClosedRunFailure(late, "vsix");
      Assert.Equal(1, launcher.StartCalls);
      Assert.Equal(0, launcher.Session.TerminateCalls);
    }
    finally
    {
      launcher.Session.ApplyRelease.TrySetResult();
      await apply.WaitAsync(TimeSpan.FromSeconds(5));
    }

    await broker.CompleteRunAsync(runId, CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task CompleteRunAsync_CancellationBoundsUncooperativeHostTermination()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForTerminationRelease = true;
    launcher.Session.IgnoreTerminationCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    using var cleanupCancellation = new CancellationTokenSource(
        TimeSpan.FromMilliseconds(100));

    var completing = broker.CompleteRunAsync(runId, cleanupCancellation.Token);
    try
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => completing.WaitAsync(TimeSpan.FromSeconds(1)));
      var late = await broker.ApplyAsync(
          Request(runId, "vsix"),
          null,
          CancellationToken.None);

      AssertClosedRunFailure(late, "vsix");
      Assert.Equal(1, launcher.StartCalls);
      Assert.Equal(1, launcher.Session.TerminateCalls);
      Assert.Equal(1, launcher.Session.DisposeCalls);
    }
    finally
    {
      launcher.Session.TerminationRelease.TrySetResult();
      await launcher.Session.TerminationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [Fact]
  public async Task CompleteRunAsync_CancellationBoundsUncooperativeSessionDisposal()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForDisposeRelease = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    using var cleanupCancellation = new CancellationTokenSource(
        TimeSpan.FromMilliseconds(100));

    var completing = broker.CompleteRunAsync(runId, cleanupCancellation.Token);
    await launcher.Session.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => completing.WaitAsync(TimeSpan.FromSeconds(1)));
      var late = await broker.ApplyAsync(
          Request(runId, "vsix"),
          null,
          CancellationToken.None);

      AssertClosedRunFailure(late, "vsix");
      Assert.Equal(1, launcher.StartCalls);
      Assert.Equal(1, launcher.Session.TerminateCalls);
      Assert.Equal(1, launcher.Session.DisposeCalls);
    }
    finally
    {
      launcher.Session.DisposeRelease.TrySetResult();
      await launcher.Session.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [Fact]
  public async Task CompleteRunAsync_RacingDispose_ObservesSharedCleanupFailure()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForApplyRelease = true;
    launcher.Session.WaitForTerminationRelease = true;
    launcher.Session.TerminateException = new IOException("termination failed");
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var completing = broker.CompleteRunAsync(runId, CancellationToken.None);
    var disposing = broker.DisposeAsync().AsTask();
    await launcher.Session.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    launcher.Session.ApplyRelease.TrySetResult();
    await apply.WaitAsync(TimeSpan.FromSeconds(5));
    launcher.Session.TerminationRelease.TrySetResult();

    var disposeError = await Assert.ThrowsAsync<IOException>(() => disposing);
    var completionError = await Assert.ThrowsAsync<IOException>(() => completing);
    Assert.Equal("termination failed", disposeError.Message);
    Assert.Equal(disposeError.Message, completionError.Message);
    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task DisposeAsync_ActiveApplyWaitsForCancellationWithoutDisposedSemaphoreRace()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    using var cancellation = new CancellationTokenSource();
    var runId = Guid.NewGuid();
    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        cancellation.Token);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var disposing = broker.DisposeAsync().AsTask();
    await launcher.Session.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var disposedBeforeApplyCompleted = disposing.IsCompleted;
    cancellation.Cancel();
    var exception = await Record.ExceptionAsync(() => apply);
    await disposing.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposedBeforeApplyCompleted);
    Assert.IsAssignableFrom<OperationCanceledException>(exception);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None));
  }

  [Fact]
  public async Task DisposeAsync_TerminateFailure_StillDisposesSession()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.TerminateException = new IOException("termination failed");
    var broker = new NamedPipePrivilegeBroker(launcher);
    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);

    var error = await Assert.ThrowsAsync<IOException>(() => broker.DisposeAsync().AsTask());

    Assert.Equal("termination failed", error.Message);
    Assert.Equal(1, launcher.Session.TerminateCalls);
    Assert.Equal(1, launcher.Session.DisposeCalls);
  }

  [Fact]
  public async Task ApplyAsync_CurrentUserPlan_BypassesBroker()
  {
    var provider = new RecordingProvider();
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var runId = Guid.NewGuid();
    var resource = Resource(PrivilegeRequirement.CurrentUser);
    var plan = Plan(resource, PrivilegeRequirement.CurrentUser);

    await dispatcher.ApplyAsync(
        runId,
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Empty(broker.Requests);
  }

  [Fact]
  public async Task ApplyAsync_AdministratorPlan_UsesRestrictedBrokerRequest()
  {
    var provider = new RecordingProvider();
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var runId = Guid.NewGuid();
    var resource = Resource(PrivilegeRequirement.Administrator);
    var plan = Plan(resource, PrivilegeRequirement.Administrator);

    await dispatcher.ApplyAsync(
        runId,
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    Assert.Equal(0, provider.ApplyCalls);
    var request = Assert.Single(broker.Requests);
    Assert.Equal(runId, request.RunId);
    Assert.Equal(resource.Id, request.ResourceId);
    Assert.Equal(
        ApprovedResourceFingerprint.Create(resource, plan),
        request.PlanFingerprint);
  }

  [Fact]
  public async Task ApplyAsync_AdministratorPlan_ForwardsCancellationDeadline()
  {
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var resource = Resource(PrivilegeRequirement.Administrator);
    var plan = Plan(resource, PrivilegeRequirement.Administrator);
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromSeconds(1),
        CancellationToken.None);

    await dispatcher.ApplyAsync(
        Guid.NewGuid(),
        new RecordingProvider(),
        resource,
        plan,
        null,
        CancellationToken.None,
        deadline);

    Assert.Same(deadline, broker.CancellationDeadline);
  }

  [Fact]
  public async Task ApplyAsync_MixedPrivilegePlan_PreservesOrderedIntegritySegments()
  {
    var calls = new List<string>();
    var provider = new RecordingProvider
    {
      Applied = plan => calls.Add($"direct:{string.Join(',', plan.Steps.Select(step => step.Id))}")
    };
    var broker = new RecordingPrivilegeBroker
    {
      Applied = request => calls.Add($"broker:{request.PlanFingerprint}")
    };
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var runId = Guid.NewGuid();
    var resource = Resource(PrivilegeRequirement.Administrator);
    var plan = Plan(resource, PrivilegeRequirement.CurrentUser) with
    {
      Steps =
      [
        Step("current-one", PrivilegeRequirement.CurrentUser),
        Step("administrator", PrivilegeRequirement.Administrator),
        Step("current-two", PrivilegeRequirement.CurrentUser)
      ]
    };
    var administratorPlan = plan with { Steps = [plan.Steps[1]] };
    broker.Result = SuccessfulResult(resource.Id, administratorPlan.Steps);

    var result = await dispatcher.ApplyAsync(
        runId,
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    var administratorFingerprint = ApprovedResourceFingerprint.Create(
        resource,
        administratorPlan);
    Assert.Equal(
        [
          "direct:current-one",
          $"broker:{administratorFingerprint}",
          "direct:current-two"
        ],
        calls);
    Assert.Equal(
        ["current-one", "administrator", "current-two"],
        result.StepResults.Select(step => step.StepId));
    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(2, provider.ApplyCalls);
    Assert.All(
        provider.AppliedPlans,
        applied => Assert.All(
            applied.Steps,
            step => Assert.Equal(PrivilegeRequirement.CurrentUser, step.PrivilegeRequirement)));
  }

  [Fact]
  public async Task ApplyAsync_MixedPrivilegeFailurePreservesStrongestRestartEvidence()
  {
    var provider = new RecordingProvider
    {
      RestartRequirement = RestartPolicy.RestartRecommended
    };
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var resource = Resource(PrivilegeRequirement.Administrator);
    var plan = Plan(resource, PrivilegeRequirement.CurrentUser) with
    {
      Steps =
      [
        Step("current", PrivilegeRequirement.CurrentUser),
        Step("administrator", PrivilegeRequirement.Administrator)
      ]
    };
    broker.Result = new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      RestartRequirement = RestartPolicy.RestartRequired
    };

    var result = await dispatcher.ApplyAsync(
        Guid.NewGuid(),
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, result.RestartRequirement);
  }

  private static ElevatedResourceRequest Request(Guid runId, string resourceId) => new(
      runId,
      resourceId,
      new string('A', 64));

  private static ResourceDefinition Resource(PrivilegeRequirement privilege) => new()
  {
    Id = "resource",
    Type = "test",
    Provider = "test",
    PrivilegeRequirement = privilege
  };

  private static ResourcePlan Plan(
      ResourceDefinition resource,
      PrivilegeRequirement privilege) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = new string('C', 64),
        Compliance = ComplianceStatus.Missing,
        IsExecutable = true,
        Steps =
        [
          new PlanStep
          {
            Id = "resource:apply",
            Description = "Apply resource.",
            Action = PlanAction.Configure,
            PrivilegeRequirement = privilege,
            RestartPolicy = RestartPolicy.NoRestart
          }
        ]
      };

  private static PlanStep Step(string id, PrivilegeRequirement privilege) => new()
  {
    Id = id,
    Description = $"Apply {id}.",
    Action = PlanAction.Configure,
    PrivilegeRequirement = privilege,
    RestartPolicy = RestartPolicy.NoRestart
  };

  private static ResourceApplyResult SuccessfulResult(
      string resourceId,
      IReadOnlyList<PlanStep> steps) => new()
      {
        ResourceId = resourceId,
        Outcome = ApplyOutcome.Succeeded,
        StepResults = steps.Select(step => new ProviderStepResult
        {
          StepId = step.Id,
          Action = step.Action,
          Progress = 1
        }).ToArray()
      };

  private static void AssertClosedRunFailure(ResourceApplyResult result, string resourceId)
  {
    Assert.Equal(resourceId, result.ResourceId);
    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal("Execution run is closed.", result.Error.Summary);
    Assert.Equal(resourceId, result.Error.ResourceId);
  }

  private sealed class RecordingElevatedHostLauncher : IElevatedHostLauncher
  {
    public int StartCalls { get; private set; }
    public Exception? StartException { get; init; }
    public List<string> PipeNames { get; } = [];
    public RecordingElevatedHostSession Session { get; } = new();

    public Task<IElevatedHostSession> StartAsync(
        Guid runId,
        string pipeName,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      StartCalls++;
      PipeNames.Add(pipeName);
      if (StartException is not null)
      {
        throw StartException;
      }

      return Task.FromResult<IElevatedHostSession>(Session);
    }
  }

  private sealed class RecordingElevatedHostSession : IElevatedHostSession
  {
    public List<ElevatedResourceRequest> Requests { get; } = [];
    public bool WaitForCancellation { get; set; }
    public bool WaitForApplyRelease { get; set; }
    public bool WaitForTerminationRelease { get; set; }
    public bool WaitForDisposeRelease { get; set; }
    public bool IgnoreTerminationCancellation { get; set; }
    public Exception? TerminateException { get; set; }
    public int TerminateCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public TaskCompletionSource RequestStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Terminated { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ApplyRelease { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TerminationRelease { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource TerminationCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeRelease { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      RequestStarted.TrySetResult();
      if (WaitForApplyRelease)
      {
        await ApplyRelease.Task.WaitAsync(cancellationToken);
      }
      else if (WaitForCancellation)
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }

      return new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Succeeded
      };
    }

    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
      TerminateCalls++;
      Terminated.TrySetResult();
      try
      {
        if (WaitForTerminationRelease)
        {
          if (IgnoreTerminationCancellation)
          {
            await TerminationRelease.Task;
          }
          else
          {
            await TerminationRelease.Task.WaitAsync(cancellationToken);
          }
        }

        if (TerminateException is not null)
        {
          throw TerminateException;
        }
      }
      finally
      {
        TerminationCompleted.TrySetResult();
      }
    }

    public async ValueTask DisposeAsync()
    {
      DisposeCalls++;
      DisposeStarted.TrySetResult();
      try
      {
        if (WaitForDisposeRelease)
        {
          await DisposeRelease.Task;
        }
      }
      finally
      {
        DisposeCompleted.TrySetResult();
      }
    }
  }

  private sealed class RecordingPrivilegeBroker : IPrivilegeBroker
  {
    public List<ElevatedResourceRequest> Requests { get; } = [];
    public Action<ElevatedResourceRequest>? Applied { get; init; }
    public ResourceApplyResult? Result { get; set; }
    public CancellationDrainDeadline? CancellationDeadline { get; private set; }

    public Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      Applied?.Invoke(request);
      return Task.FromResult(Result ?? new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Succeeded
      });
    }

    public Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken,
        CancellationDrainDeadline? cancellationDeadline)
    {
      CancellationDeadline = cancellationDeadline;
      return ApplyAsync(request, progress, cancellationToken);
    }
  }

  private sealed class RecordingProvider : IResourceProvider
  {
    public string ResourceType => "test";
    public string ProviderName => "test";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }
    public Action<ResourcePlan>? Applied { get; init; }
    public RestartPolicy? RestartRequirement { get; init; }
    public List<ResourcePlan> AppliedPlans { get; } = [];

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      AppliedPlans.Add(plan);
      Applied?.Invoke(plan);
      return ValueTask.FromResult(SuccessfulResult(resource.Id, plan.Steps) with
      {
        RestartRequirement = RestartRequirement
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
