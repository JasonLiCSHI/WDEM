using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Desktop.ViewModels;

namespace Wdem.Desktop.Tests.ViewModels;

internal static class ViewModelTestFixture
{
  internal static RunRequest Request() => new(
      "profiles/csharp-developer.yaml",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  internal static DeveloperProfile Profile() => new()
  {
    Id = "csharp-developer",
    Version = "1.0.0",
    DisplayName = "C# Developer",
    Description = "Test profile",
    RequiredResources = [new ProfileResourceReference { Id = "git" }],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new ResourceDefinition
      {
        Id = "git",
        Type = "package",
        Provider = "fake"
      }
    }
  };

  internal static RunEvent Event(Guid runId, long sequence, double progress, string message) =>
      new(
          runId,
          sequence,
          DateTimeOffset.UnixEpoch.AddSeconds(sequence),
          RunEventKind.StepProgress,
          "git",
          "install",
          progress,
          message,
          null);

  internal static ExecutionRun CompletedRun(
      params (string Id, ExecutionOutcome Outcome, string StepId)[] resources)
  {
    var completedAt = DateTimeOffset.UtcNow;
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Apply,
      ProfileSourcePath = Path.GetFullPath("profiles/csharp-developer.yaml"),
      ProfileId = "csharp-developer",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(),
      StartedAtUtc = completedAt.AddMinutes(-1),
      EndedAtUtc = completedAt,
      State = ExecutionState.Completed,
      Outcome = resources.Any(resource => resource.Outcome == ExecutionOutcome.Failed)
          ? ExecutionOutcome.Failed
          : ExecutionOutcome.Succeeded,
      Machine = new MachineInformation("Windows", "x64", "test", "test"),
      ResourceResults = resources.ToDictionary(
          resource => resource.Id,
          resource => new ResourceResult
          {
            ResourceId = resource.Id,
            State = ExecutionState.Completed,
            Outcome = resource.Outcome,
            Progress = 1,
            StepResults =
            [
              new StepResult
              {
                StepId = resource.StepId,
                Name = resource.StepId,
                State = ExecutionState.Completed,
                Outcome = resource.Outcome,
                Progress = 1
              }
            ]
          },
          StringComparer.OrdinalIgnoreCase)
    };
  }

  internal static ExecutionRun InspectRun(bool executable)
  {
    var definition = new ResourceDefinition
    {
      Id = "git",
      Type = "package",
      Provider = "fake",
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      RestartPolicy = RestartPolicy.RestartRecommended,
      Dependencies = ["runtime"]
    };
    var resourcePlan = new ResourcePlan
    {
      ResourceId = "git",
      ResourceType = "package",
      ProviderName = "fake",
      DesiredStateFingerprint = new string('A', 64),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = executable,
      Steps =
      [
        new PlanStep
        {
          Id = "install",
          Description = "Install Git",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.RestartRecommended
        }
      ]
    };
    var diagnostic = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider unavailable.",
        "The fake provider cannot execute this plan.")
    {
      ResourceId = "git"
    };
    var planned = new PlannedResource
    {
      Definition = definition,
      Origin = ResourceOrigin.Required,
      Dependencies = ["runtime"],
      ResourcePlan = resourcePlan,
      Status = executable ? PlannedResourceStatus.Ready : PlannedResourceStatus.Blocked,
      Risk = PlanRisk.Elevated,
      RequiresElevation = true,
      IsDestructive = false,
      RestartPolicy = RestartPolicy.RestartRecommended,
      Diagnostics = executable ? [] : [diagnostic]
    };
    var plan = new ExecutionPlan
    {
      PlanId = Guid.NewGuid(),
      Fingerprint = new string('B', 64),
      ProfileId = "csharp-developer",
      ProfileVersion = "1.0.0",
      Layers = [new ResourceGraphLayer(0, ["git"])],
      Resources = [planned],
      IsExecutable = executable,
      Errors = executable ? [] : [diagnostic]
    };
    return CompletedRun(("git", ExecutionOutcome.Failed, "install")) with
    {
      Mode = RunMode.Inspect,
      Plan = plan
    };
  }

  internal static RecoveryCandidate RecoveryCandidate(Guid? runId = null) => new()
  {
    RunId = runId ?? Guid.Parse("b1723fe5-e7e1-4e87-98bd-e057be256c19"),
    ProfileSourcePath = Path.GetFullPath("profiles/csharp-developer.yaml"),
    StartedAtUtc = DateTimeOffset.Parse("2026-08-30T08:30:00Z"),
    PendingResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "git",
      "dotnet-sdk"
    }
  };

  internal sealed class RecordingDispatcher : IUiDispatcher
  {
    public int EnqueueCalls { get; private set; }

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      EnqueueCalls++;
      action();
      return Task.CompletedTask;
    }
  }

  internal sealed class RejectingDispatcher : IUiDispatcher
  {
    public int EnqueueCalls { get; private set; }

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
      EnqueueCalls++;
      return Task.FromException(new UiDispatcherUnavailableException());
    }
  }

  internal sealed class ControlledDispatcher : IUiDispatcher
  {
    private readonly object _gate = new();
    private readonly Queue<(Action Action, TaskCompletionSource Completion)> _pending = [];
    private readonly List<int> _enqueueThreadIds = [];
    private int _enqueueCalls;
    private bool _runInline;

    public IReadOnlyList<int> EnqueueThreadIds
    {
      get
      {
        lock (_gate)
        {
          return _enqueueThreadIds.ToArray();
        }
      }
    }

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
      ArgumentNullException.ThrowIfNull(action);
      cancellationToken.ThrowIfCancellationRequested();
      TaskCompletionSource? completion = null;
      lock (_gate)
      {
        _enqueueThreadIds.Add(Environment.CurrentManagedThreadId);
        _enqueueCalls++;
        if (!_runInline)
        {
          completion = NewCompletion();
          _pending.Enqueue((action, completion));
        }

        Monitor.PulseAll(_gate);
      }

      if (completion is not null)
      {
        return completion.Task;
      }

      action();
      return Task.CompletedTask;
    }

    public bool WaitForEnqueueCount(int expected, TimeSpan timeout)
    {
      var deadline = DateTimeOffset.UtcNow + timeout;
      lock (_gate)
      {
        while (_enqueueCalls < expected)
        {
          TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
          if (remaining <= TimeSpan.Zero || !Monitor.Wait(_gate, remaining))
          {
            return false;
          }
        }

        return true;
      }
    }

    public void DrainNext()
    {
      (Action Action, TaskCompletionSource Completion) work;
      lock (_gate)
      {
        work = _pending.Dequeue();
      }

      try
      {
        work.Action();
        work.Completion.TrySetResult();
      }
      catch (Exception exception)
      {
        work.Completion.TrySetException(exception);
        throw;
      }
    }

    public void RunQueuedAndInline()
    {
      lock (_gate)
      {
        _runInline = true;
      }

      while (true)
      {
        lock (_gate)
        {
          if (_pending.Count == 0)
          {
            return;
          }
        }

        DrainNext();
      }
    }

    private static TaskCompletionSource NewCompletion() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
  }

  internal sealed class FixedProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    private readonly ProfileLoadResult _result = new()
    {
      Profile = profile,
      SourcePath = Path.GetFullPath("profiles/csharp-developer.yaml")
    };

    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) => Task.FromResult(_result);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.FromResult(_result);

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([_result]);
  }

  internal sealed class SlowLoadProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    private readonly ProfileLoadResult _result = new()
    {
      Profile = profile,
      SourcePath = Path.GetFullPath("profiles/csharp-developer.yaml")
    };

    public TaskCompletionSource LoadStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseLoad { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
      LoadStarted.TrySetResult();
      using CancellationTokenRegistration registration = cancellationToken.Register(
          () => CancellationObserved.TrySetResult());
      await ReleaseLoad.Task;
      return _result;
    }

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.FromResult(_result);

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([_result]);
  }

  internal sealed class FakeEnvironmentRunService(
      IRunEventSink events,
      Guid runId) : IReviewedPlanEnvironmentRunService
  {
    public IReadOnlyList<RunEvent> Events { get; set; } = [];
    public IReadOnlyList<RunEvent> RecoveryEvents { get; set; } = [];
    public ExecutionRun ApplyResult { get; set; } = CompletedRun();
    public ExecutionRun InspectResult { get; set; } = InspectRun(executable: true);
    public Exception? InspectException { get; set; }
    public IReadOnlyList<RecoveryCandidate> RecoveryCandidates { get; set; } = [];
    public Exception? RecoveryDiscoveryException { get; set; }
    public Exception? RecoverException { get; set; }
    public Exception? AbandonException { get; set; }
    public ExecutionRun RecoverResult { get; set; } = CompletedRun();
    public int InspectCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int FindRecoveryCandidatesCalls { get; private set; }
    public int RecoverCalls { get; private set; }
    public int AbandonCalls { get; private set; }
    public RunRequest? AppliedRequest { get; private set; }
    public string? ReviewedPlanFingerprint { get; private set; }
    public Guid? RetriedRunId { get; private set; }
    public Guid? RecoveredRunId { get; private set; }
    public Guid? AbandonedRunId { get; private set; }
    public IReadOnlySet<string>? RetriedResourceIds { get; private set; }
    public CancellationToken RetryCancellationToken { get; private set; }
    public CancellationToken ApplyCancellationToken { get; private set; }
    public bool HoldAfterCancellation { get; init; }
    public bool HoldRetryAfterCancellation { get; init; }
    public bool HoldRetryUntilReleased { get; init; }
    public bool HoldAfterEvents { get; set; }
    public bool HoldInspection { get; init; }
    public int? HoldInspectionOnCall { get; init; }
    public bool HoldInspectionAfterCancellation { get; init; }
    public bool IgnoreInspectionCancellation { get; init; }
    public bool ThrowOnInspectionCancellation { get; init; }
    public bool HoldRecoveryDiscovery { get; init; }
    public bool HoldRecover { get; init; }
    public bool HoldAbandon { get; init; }
    public TaskCompletionSource InspectStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource HeldInspectionStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseInspection { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InspectionCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InspectionCleanupCompleted { get; private set; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseInspectionAfterCancellation { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ApplyStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseTerminalCompletion { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RetryStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RetryCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRetryTerminalCompletion { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRetry { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EventsPublished { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseEvents { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoveryDiscoveryStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoveryDiscoveryCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRecoveryDiscovery { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoverStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoverCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRecover { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AbandonStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AbandonCancellationObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAbandon { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      InspectCalls++;
      var cleanupCompleted = new TaskCompletionSource(
          TaskCreationOptions.RunContinuationsAsynchronously);
      InspectionCleanupCompleted = cleanupCompleted;
      try
      {
        InspectStarted.TrySetResult();
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => InspectionCancellationObserved.TrySetResult());
        using CancellationTokenRegistration throwingRegistration = ThrowOnInspectionCancellation
            ? cancellationToken.Register(() =>
            {
              InspectionCancellationObserved.TrySetResult();
              throw new InvalidOperationException("inspection cancellation callback failed");
            })
            : default;
        if (HoldInspection &&
            (HoldInspectionOnCall is null || InspectCalls >= HoldInspectionOnCall))
        {
          HeldInspectionStarted.TrySetResult();
          if (IgnoreInspectionCancellation)
          {
            await ReleaseInspection.Task;
          }
          else
          {
            try
            {
              await ReleaseInspection.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
              InspectionCancellationObserved.TrySetResult();
              if (HoldInspectionAfterCancellation)
              {
                await ReleaseInspectionAfterCancellation.Task;
              }

              throw;
            }
          }
        }

        if (InspectException is not null)
        {
          throw InspectException;
        }

        return InspectResult;
      }
      finally
      {
        cleanupCompleted.TrySetResult();
      }
    }

    public async Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      ApplyCancellationToken = cancellationToken;
      AppliedRequest = request;
      events.BindCurrentScopeToRun(runId);
      ApplyStarted.TrySetResult();
      foreach (RunEvent runEvent in Events)
      {
        await events.PublishAsync(runEvent, cancellationToken);
      }

      EventsPublished.TrySetResult();
      if (HoldAfterEvents)
      {
        await ReleaseEvents.Task;
      }

      if (HoldAfterCancellation)
      {
        try
        {
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
          CancellationObserved.TrySetResult();
        }

        await ReleaseTerminalCompletion.Task;
        return CompletedRun() with
        {
          RunId = runId,
          Outcome = ExecutionOutcome.Cancelled
        };
      }

      return ApplyResult with { RunId = runId };
    }

    public Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        string reviewedPlanFingerprint,
        CancellationToken cancellationToken)
    {
      ReviewedPlanFingerprint = reviewedPlanFingerprint;
      return ApplyAsync(request, cancellationToken);
    }

    public async Task<ExecutionRun> RetryAsync(
        Guid priorRunId,
        IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken)
    {
      RetriedRunId = priorRunId;
      RetriedResourceIds = new HashSet<string>(resourceIds, StringComparer.OrdinalIgnoreCase);
      RetryCancellationToken = cancellationToken;
      RetryStarted.TrySetResult();
      if (HoldRetryUntilReleased)
      {
        await ReleaseRetry.Task;
      }

      if (HoldRetryAfterCancellation)
      {
        try
        {
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
          RetryCancellationObserved.TrySetResult();
        }

        await ReleaseRetryTerminalCompletion.Task;
      }

      return CompletedRun(("git", ExecutionOutcome.Succeeded, "install"));
    }

    public async Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken)
    {
      FindRecoveryCandidatesCalls++;
      RecoveryDiscoveryStarted.TrySetResult();
      using CancellationTokenRegistration registration = cancellationToken.Register(
          () => RecoveryDiscoveryCancellationObserved.TrySetResult());
      if (HoldRecoveryDiscovery)
      {
        await ReleaseRecoveryDiscovery.Task.WaitAsync(cancellationToken);
      }

      if (RecoveryDiscoveryException is not null)
      {
        throw RecoveryDiscoveryException;
      }

      return RecoveryCandidates;
    }

    public async Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken)
    {
      RecoverCalls++;
      RecoveredRunId = priorRunId;
      RecoverStarted.TrySetResult();
      if (HoldRecover)
      {
        try
        {
          await ReleaseRecover.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
          RecoverCancellationObserved.TrySetResult();
          throw;
        }
      }

      if (RecoverException is not null)
      {
        throw RecoverException;
      }

      events.BindCurrentScopeToRun(RecoverResult.RunId);
      foreach (RunEvent runEvent in RecoveryEvents)
      {
        await events.PublishAsync(runEvent, cancellationToken);
      }

      return RecoverResult;
    }

    public async Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken)
    {
      AbandonCalls++;
      AbandonedRunId = priorRunId;
      AbandonStarted.TrySetResult();
      if (HoldAbandon)
      {
        try
        {
          await ReleaseAbandon.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
          AbandonCancellationObserved.TrySetResult();
          throw;
        }
      }

      if (AbandonException is not null)
      {
        throw AbandonException;
      }
    }
  }

  internal sealed class CapturingReportExporter : IRunReportExporter
  {
    public ExecutionRun? Run { get; private set; }
    public string? FilePath { get; private set; }

    public string ExportJson(ExecutionRun run) => throw new NotSupportedException();

    public string ExportMarkdown(ExecutionRun run) => throw new NotSupportedException();

    public Task ExportAsync(
        ExecutionRun run,
        string filePath,
        CancellationToken cancellationToken = default)
    {
      Run = run;
      FilePath = filePath;
      return Task.CompletedTask;
    }
  }

  internal sealed class TestRunEventSink : IRunEventSink
  {
    private readonly object _gate = new();
    private readonly List<Func<RunEvent, CancellationToken, Task>> _observers = [];

    public int ObserverCount
    {
      get
      {
        lock (_gate)
        {
          return _observers.Count;
        }
      }
    }

    public int SubscriptionDisposals { get; private set; }

    public IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer) =>
        Add(observer);

    public IDisposable SubscribeRequired(Func<RunEvent, CancellationToken, Task> observer) =>
        Add(observer);

    public IDisposable SubscribeScoped(Func<RunEvent, CancellationToken, Task> observer) =>
        Add(observer);

    public IDisposable SubscribeRequiredScoped(
        Func<RunEvent, CancellationToken, Task> observer) => Add(observer);

    public void BindCurrentScopeToRun(Guid targetRunId)
    {
    }

    public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
      Func<RunEvent, CancellationToken, Task>[] observers;
      lock (_gate)
      {
        observers = _observers.ToArray();
      }

      foreach (var observer in observers)
      {
        await observer(runEvent, cancellationToken);
      }
    }

    private IDisposable Add(Func<RunEvent, CancellationToken, Task> observer)
    {
      lock (_gate)
      {
        _observers.Add(observer);
      }

      return new Subscription(this, observer);
    }

    private sealed class Subscription(
        TestRunEventSink owner,
        Func<RunEvent, CancellationToken, Task> observer) : IDisposable
    {
      private TestRunEventSink? _owner = owner;

      public void Dispose()
      {
        var current = Interlocked.Exchange(ref _owner, null);
        if (current is null)
        {
          return;
        }

        lock (current._gate)
        {
          current._observers.Remove(observer);
          current.SubscriptionDisposals++;
        }
      }
    }
  }
}
