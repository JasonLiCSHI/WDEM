using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class ExecutionMonitorViewModelTests
{
  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData(" ")]
  public void Constructor_RejectsMissingReviewedPlanFingerprintWithoutApplying(
      string? reviewedPlanFingerprint)
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid());

    Assert.ThrowsAny<ArgumentException>(() => new ExecutionMonitorViewModel(
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher(),
        Request(),
        reviewedPlanFingerprint!));

    Assert.Equal(0, service.ApplyCalls);
  }

  [Fact]
  public async Task RunEventsAreAppliedOnlyAfterTheUiDispatcherDrains()
  {
    var runId = Guid.NewGuid();
    using var events = new RunEventHub();
    var service = new FakeEnvironmentRunService(events, runId);
    var dispatcher = new ControlledDispatcher();
    var viewModel = CreateMonitor(service, events, dispatcher);
    int foregroundThreadId = Environment.CurrentManagedThreadId;
    var error = new StructuredError(
        WdemErrorCode.InstallationError,
        "Install failed.",
        "The package returned an error.")
    {
      ResourceId = "git",
      StepId = "install",
      IsRetryable = true
    };
    service.Events =
    [
      new RunEvent(
          runId,
          6,
          new DateTimeOffset(2026, 8, 30, 9, 14, 59, TimeSpan.Zero),
          RunEventKind.ResourceStateChanged,
          "git",
          null,
          0.25,
          "Installing Git",
          null,
          ExecutionState.Running,
          null),
      new RunEvent(
          runId,
          7,
          new DateTimeOffset(2026, 8, 30, 9, 15, 0, TimeSpan.Zero),
          RunEventKind.StepProgress,
          "git",
          "install",
          0.65,
          "Downloading Git",
          error,
          ExecutionState.Completed,
          ExecutionOutcome.Failed)
    ];

    service.HoldAfterEvents = true;
    Task run = viewModel.StartAsync();
    Assert.True(dispatcher.WaitForEnqueueCount(1, TimeSpan.FromSeconds(5)));

    Assert.Empty(viewModel.Resources);
    Assert.Empty(viewModel.Logs);
    Assert.Equal(0, viewModel.TotalProgress);
    Assert.Null(viewModel.CurrentResource);
    Assert.NotEqual(foregroundThreadId, dispatcher.EnqueueThreadIds[0]);

    dispatcher.DrainNext();
    Assert.True(dispatcher.WaitForEnqueueCount(2, TimeSpan.FromSeconds(5)));
    dispatcher.DrainNext();
    await service.EventsPublished.Task;

    ResourceProgressViewModel resource = Assert.Single(viewModel.Resources);
    Assert.Equal(65, resource.Percent);
    Assert.Equal("Downloading Git", resource.Message);
    Assert.Equal(ExecutionState.Running, resource.State);
    Assert.Null(resource.Outcome);
    StepProgressViewModel step = Assert.Single(resource.Steps);
    Assert.Equal(ExecutionState.Completed, step.State);
    Assert.Equal(ExecutionOutcome.Failed, step.Outcome);
    LogEntryViewModel log = viewModel.Logs[^1];
    Assert.Equal(7, log.Sequence);
    Assert.Equal("git", log.ResourceId);
    Assert.Equal("install", log.StepId);
    Assert.Equal("Downloading Git", log.Message);
    Assert.Equal("The package returned an error.", log.ErrorDetail);
    Assert.Equal(65, viewModel.TotalProgress);
    Assert.Equal("git", viewModel.CurrentResource);
    Assert.Equal(2, viewModel.Logs.Count);
    dispatcher.RunQueuedAndInline();
    service.ReleaseEvents.TrySetResult();
    await run;
    Assert.Null(viewModel.CurrentResource);
  }

  [Fact]
  public async Task CurrentResourceTracksMostRecentlyActiveRunningResource()
  {
    var runId = Guid.NewGuid();
    using var events = new RunEventHub();
    var service = new FakeEnvironmentRunService(events, runId)
    {
      Events =
      [
        StateEvent(1, "alpha", ExecutionState.Pending),
        StateEvent(2, "alpha", ExecutionState.Ready),
        StateEvent(3, "beta", ExecutionState.Blocked),
        StateEvent(4, "alpha", ExecutionState.Running),
        StateEvent(5, "beta", ExecutionState.Running),
        new RunEvent(
            runId,
            6,
            DateTimeOffset.UnixEpoch.AddSeconds(6),
            RunEventKind.StepProgress,
            "alpha",
            "install",
            0.5,
            "Installing alpha",
            null,
            ExecutionState.Running,
            null),
        StateEvent(7, "alpha", ExecutionState.Completed, ExecutionOutcome.Succeeded),
        StateEvent(8, "beta", ExecutionState.Completed, ExecutionOutcome.Succeeded),
        StateEvent(9, "gamma", ExecutionState.Running),
        new RunEvent(
            runId,
            10,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            RunEventKind.RunStateChanged,
            null,
            null,
            1,
            "Run completed",
            null,
            ExecutionState.Completed,
            ExecutionOutcome.Succeeded),
        new RunEvent(
            runId,
            11,
            DateTimeOffset.UnixEpoch.AddSeconds(11),
            RunEventKind.Completed,
            null,
            null,
            1,
            "Run completed",
            null,
            ExecutionState.Completed,
            ExecutionOutcome.Succeeded)
      ],
      HoldAfterEvents = true
    };
    var dispatcher = new ControlledDispatcher();
    var viewModel = CreateMonitor(service, events, dispatcher);

    Task run = viewModel.StartAsync();
    string?[] expected =
    [
      null,
      null,
      null,
      "alpha",
      "beta",
      "alpha",
      "beta",
      null,
      "gamma",
      null,
      null
    ];
    for (int index = 0; index < expected.Length; index++)
    {
      Assert.True(dispatcher.WaitForEnqueueCount(index + 1, TimeSpan.FromSeconds(5)));
      dispatcher.DrainNext();
      Assert.Equal(expected[index], viewModel.CurrentResource);
    }

    await service.EventsPublished.Task;
    dispatcher.RunQueuedAndInline();
    service.ReleaseEvents.TrySetResult();
    await run;

    Assert.Null(viewModel.CurrentResource);

    RunEvent StateEvent(
        long sequence,
        string resourceId,
        ExecutionState state,
        ExecutionOutcome? outcome = null) => new(
            runId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            RunEventKind.ResourceStateChanged,
            resourceId,
            null,
            null,
            state.ToString(),
            null,
            state,
            outcome);
  }

  [Fact]
  public async Task RestartRequirementUpdatesLiveWithoutLosingParallelRequirements()
  {
    var runId = Guid.NewGuid();
    using var events = new RunEventHub();
    var service = new FakeEnvironmentRunService(events, runId)
    {
      Events =
      [
        ResourceEvent(1, "alpha", ExecutionState.Running, RestartPolicy.RestartRecommended),
        ResourceEvent(2, "beta", ExecutionState.Running, RestartPolicy.RestartRequired),
        ResourceEvent(3, "alpha", ExecutionState.Completed, RestartPolicy.NoRestart),
        ResourceEvent(4, "beta", ExecutionState.Completed, RestartPolicy.RestartRequired)
      ],
      ApplyResult = CompletedRun() with
      {
        RestartRequirements = [RestartPolicy.RestartRecommended]
      },
      HoldAfterEvents = true
    };
    var dispatcher = new ControlledDispatcher();
    var viewModel = CreateMonitor(service, events, dispatcher);
    Task run = viewModel.StartAsync();
    string[] expected =
    [
      "RestartRecommended",
      "RestartRequired",
      "RestartRequired",
      "RestartRequired"
    ];

    for (int index = 0; index < expected.Length; index++)
    {
      Assert.True(dispatcher.WaitForEnqueueCount(index + 1, TimeSpan.FromSeconds(5)));
      dispatcher.DrainNext();
      Assert.Equal(expected[index], viewModel.RestartRequirement);
    }

    await service.EventsPublished.Task;
    dispatcher.RunQueuedAndInline();
    service.ReleaseEvents.TrySetResult();
    await run;

    Assert.Equal("RestartRecommended", viewModel.RestartRequirement);

    RunEvent ResourceEvent(
        long sequence,
        string resourceId,
        ExecutionState state,
        RestartPolicy restartRequirement) => new(
            runId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            RunEventKind.ResourceStateChanged,
            resourceId,
            null,
            null,
            state.ToString(),
            null,
            state,
            state == ExecutionState.Completed ? ExecutionOutcome.Succeeded : null,
            restartRequirement);
  }

  [Fact]
  public async Task RetryFailedPassesFailedResourceIdsRatherThanStepIds()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      ApplyResult = CompletedRun(
          ("git", ExecutionOutcome.Failed, "install"),
          ("dotnet", ExecutionOutcome.Succeeded, "configure"))
    };
    var viewModel = CreateMonitor(service, events, new RecordingDispatcher());
    await viewModel.StartAsync();
    Guid? priorRunId = viewModel.RunId;
    using var cancellation = new CancellationTokenSource();

    await viewModel.RetryFailedAsync(cancellation.Token);

    Assert.Equal(priorRunId, service.RetriedRunId);
    Assert.Equal(["git"], service.RetriedResourceIds);
    Assert.DoesNotContain("install", service.RetriedResourceIds!);
    Assert.True(service.RetryCancellationToken.CanBeCanceled);
  }

  [Fact]
  public async Task StopAsyncDuringRetryWaitsForCancelledRetryCleanup()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      ApplyResult = CompletedRun(("git", ExecutionOutcome.Failed, "install")),
      HoldRetryAfterCancellation = true
    };
    var viewModel = CreateMonitor(service, events, new RecordingDispatcher());
    await viewModel.StartAsync();
    Task retry = viewModel.RetryFailedAsync(CancellationToken.None);
    await service.RetryStarted.Task;

    Task stopping = viewModel.StopAsync();
    await service.RetryCancellationObserved.Task;

    try
    {
      Assert.False(stopping.IsCompleted);
    }
    finally
    {
      service.ReleaseRetryTerminalCompletion.TrySetResult();
      await Task.WhenAll(stopping, retry);
    }

    Assert.False(viewModel.IsRunning);
  }

  [Fact]
  public async Task CancelOnlyCancelsCurrentRunAndRemainsEnabledUntilTerminalCompletion()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldAfterCancellation = true
    };
    var viewModel = CreateMonitor(service, events, new RecordingDispatcher());
    Task run = viewModel.StartAsync();
    await service.ApplyStarted.Task;
    Assert.True(viewModel.CancelCommand.CanExecute(null));

    await viewModel.CancelCommand.ExecuteAsync(null);

    await service.CancellationObserved.Task;
    Assert.True(viewModel.CancelCommand.CanExecute(null));
    Assert.False(service.RetryCancellationToken.IsCancellationRequested);

    service.ReleaseTerminalCompletion.TrySetResult();
    await run;
    Assert.False(viewModel.CancelCommand.CanExecute(null));
  }

  [Fact]
  public async Task LogsKeepLatestFiveThousandEntriesAndRedactVisibleText()
  {
    var runId = Guid.NewGuid();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, runId);
    service.Events = Enumerable.Range(1, 5_001)
        .Select(sequence => new RunEvent(
            runId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            RunEventKind.Log,
            "git",
            null,
            null,
            $"token=top-secret entry {sequence}",
            null))
        .ToArray();
    var viewModel = new ExecutionMonitorViewModel(
        service,
        events,
        new LogRedactor(["top-secret"]),
        new RecordingDispatcher(),
        Request(),
        new string('A', 64));

    await viewModel.StartAsync();

    Assert.Equal(5_000, viewModel.Logs.Count);
    Assert.Equal(2, viewModel.Logs[0].Sequence);
    Assert.Equal(5_001, viewModel.Logs[^1].Sequence);
    Assert.All(viewModel.Logs, entry =>
    {
      Assert.DoesNotContain("top-secret", entry.Message, StringComparison.Ordinal);
      Assert.Contains("***", entry.Message, StringComparison.Ordinal);
    });
  }

  [Fact]
  public async Task SubscriptionIsDisposedWhenRunEndsAndViewModelDisposalIsIdempotent()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid());
    var viewModel = CreateMonitor(service, events, new RecordingDispatcher());

    await viewModel.StartAsync();
    viewModel.Dispose();
    viewModel.Dispose();

    Assert.Equal(1, events.SubscriptionDisposals);
    Assert.Equal(0, events.ObserverCount);
  }

  [Fact]
  public async Task DispatcherShutdownCancelsRunAndDoesNotEscapeThroughPublisher()
  {
    var runId = Guid.NewGuid();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, runId)
    {
      Events = [Event(runId, 1, 0.1, "starting")]
    };
    var dispatcher = new RejectingDispatcher();
    var viewModel = CreateMonitor(service, events, dispatcher);

    await viewModel.StartAsync();

    Assert.True(service.ApplyCancellationToken.IsCancellationRequested);
    Assert.Equal(1, dispatcher.EnqueueCalls);
    Assert.Equal(0, events.ObserverCount);
  }

  [Fact]
  public async Task EventsIgnoreNonMonotonicSequencesAndOtherRuns()
  {
    var runId = Guid.NewGuid();
    var otherRunId = Guid.NewGuid();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, runId)
    {
      Events =
      [
        Event(runId, 2, 0.2, "accepted first"),
        Event(runId, 1, 0.9, "stale"),
        Event(otherRunId, 3, 0.8, "other run"),
        Event(runId, 3, 0.4, "accepted second")
      ]
    };
    var viewModel = CreateMonitor(service, events, new RecordingDispatcher());

    service.HoldAfterEvents = true;
    Task run = viewModel.StartAsync();
    await service.EventsPublished.Task;

    Assert.Equal([2L, 3L], viewModel.Logs.Select(log => log.Sequence));
    ResourceProgressViewModel resource = Assert.Single(viewModel.Resources);
    Assert.Equal(40, resource.Percent);
    Assert.Equal("accepted second", resource.Message);
    service.ReleaseEvents.TrySetResult();
    await run;
  }

  [Fact]
  public async Task PlanInspectionExposesLayersAndDisablesApplyForNonExecutableErrors()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      InspectResult = InspectRun(executable: false)
    };
    var viewModel = new PlanViewModel(
        service,
        new LogRedactor(),
        Request(),
        (_, _) => Task.CompletedTask);

    await viewModel.InitializeAsync();

    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
    Assert.False(viewModel.CanApply);
    Assert.False(viewModel.ApplyCommand.CanExecute(null));
    PlanResourceViewModel resource = Assert.Single(viewModel.Resources);
    Assert.Equal("git", resource.Id);
    Assert.Equal("fake", resource.Provider);
    Assert.Equal("Install", resource.Action);
    Assert.Equal("Administrator", resource.Privilege);
    Assert.Equal("RestartRecommended", resource.RestartPolicy);
    Assert.Equal(["runtime"], resource.Dependencies);
    Assert.Equal(["git"], Assert.Single(viewModel.Layers).ResourceIds);
    Assert.Contains("Provider unavailable.", Assert.Single(viewModel.Errors));
  }

  [Fact]
  public async Task StartConfigurationNavigationInspectsPlanWithoutApplyingIt()
  {
    DeveloperProfile profile = Profile();
    var catalog = new FixedProfileCatalog(profile);
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid());
    var main = new MainWindowViewModel(
        catalog,
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);

    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);

    Assert.IsType<PlanViewModel>(main.CurrentPage);
    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
  }

  [Fact]
  public async Task NonExecutablePlanApplyCannotBypassGateOrNavigate()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      InspectResult = InspectRun(executable: false)
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);

    await plan.ApplyCommand.ExecuteAsync(null);

    Assert.Same(plan, main.CurrentPage);
    Assert.False(plan.ApplyCommand.CanExecute(null));
    Assert.Equal(0, service.ApplyCalls);
  }

  [Fact]
  public async Task ExecutablePlanApplyNavigatesThroughMonitorToCompletionAndRunsExactlyOnce()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid());
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);
    Assert.True(plan.ApplyCommand.CanExecute(null));

    await plan.ApplyCommand.ExecuteAsync(null);

    CompletionViewModel completion = Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.NotEqual(Guid.Empty, completion.Run.RunId);
    Assert.Equal(service.ApplyResult.ProfileId, completion.Run.ProfileId);
    Assert.Equal(1, service.ApplyCalls);
    Assert.Equal(
        service.InspectResult.Plan!.Fingerprint,
        service.ReviewedPlanFingerprint);
  }

  [Fact]
  public async Task DeferredPlanCanBeApprovedOnlyWithItsReviewedFingerprint()
  {
    var events = new TestRunEventSink();
    var inspection = InspectRun(executable: true);
    var planned = Assert.Single(inspection.Plan!.Resources);
    var notice = "Plan after dependency re-detection.";
    inspection = inspection with
    {
      Plan = inspection.Plan with
      {
        Resources =
        [
          planned with
          {
            Status = PlannedResourceStatus.Deferred,
            ResourcePlan = planned.ResourcePlan with { IsExecutable = false },
            DeferredAuthorization = new DeferredPlanAuthorization
            {
              AllowedActions = [PlanAction.Install],
              MaximumPrivilege = PrivilegeRequirement.Administrator,
              MaximumRestartPolicy = RestartPolicy.RestartRecommended,
              MaximumRisk = PlanRisk.Elevated,
              AllowDestructive = false,
              DynamicPlanNotice = notice
            },
            Reason = notice
          }
        ]
      }
    };
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      InspectResult = inspection
    };
    RunRequest? approvedRequest = null;
    string? approvedFingerprint = null;
    var request = Request();
    var viewModel = new PlanViewModel(
        service,
        new LogRedactor(),
        request,
        (request, fingerprint) =>
        {
          approvedRequest = request;
          approvedFingerprint = fingerprint;
          return Task.CompletedTask;
        });

    await viewModel.InitializeAsync();
    await viewModel.ApplyCommand.ExecuteAsync(null);

    Assert.True(viewModel.CanApply);
    Assert.Same(request, approvedRequest);
    Assert.Equal(inspection.Plan.Fingerprint, approvedFingerprint);
    Assert.False(inspection.Plan.Resources.Single().ResourcePlan.IsExecutable);
  }

  [Fact]
  public async Task CompletionRetryNavigatesThroughFreshMonitorAndReturnsToCompletion()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      ApplyResult = CompletedRun(("git", ExecutionOutcome.Failed, "install")),
      HoldRetryUntilReleased = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);
    await plan.ApplyCommand.ExecuteAsync(null);
    var initialCompletion = Assert.IsType<CompletionViewModel>(main.CurrentPage);

    Task retrying = initialCompletion.RetryFailedCommand.ExecuteAsync(null);
    await service.RetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Equal(initialCompletion.Run.RunId, service.RetriedRunId);
    Assert.Equal(["git"], service.RetriedResourceIds);
    Assert.True(Assert.IsType<ExecutionMonitorViewModel>(main.CurrentPage).IsRunning);

    service.ReleaseRetry.TrySetResult();
    await retrying;

    var retriedCompletion = Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.NotEqual(initialCompletion.Run.RunId, retriedCompletion.Run.RunId);
    Assert.Empty(retriedCompletion.Failed);
  }

  [Fact]
  public async Task ChangedApprovedPlanReturnsToPlanForReview()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var approvalError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "The reviewed execution plan has changed.",
        "Review the refreshed plan before applying it.");
    ExecutionPlan rejectedPlan = InspectRun(executable: true).Plan! with
    {
      Fingerprint = new string('C', 64),
      IsExecutable = false,
      Errors = [approvalError]
    };
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      ApplyResult = CompletedRun() with
      {
        Outcome = ExecutionOutcome.Failed,
        Plan = rejectedPlan
      }
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);

    await plan.ApplyCommand.ExecuteAsync(null);

    Assert.Same(plan, main.CurrentPage);
    Assert.False(plan.CanApply);
    Assert.Contains(plan.Errors, error => error.Contains(
        "reviewed execution plan has changed",
        StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task ActiveExecutionCannotBeHiddenOrReplacedUntilItTerminates()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldAfterEvents = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);

    Task applying = plan.ApplyCommand.ExecuteAsync(null);
    await service.ApplyStarted.Task;
    var monitor = Assert.IsType<ExecutionMonitorViewModel>(main.CurrentPage);

    Assert.False(main.NavigateToProfilesCommand.CanExecute(null));
    Assert.False(main.NavigateToResourcesCommand.CanExecute(null));
    await main.NavigateToProfilesCommand.ExecuteAsync(null);
    await plan.ApplyCommand.ExecuteAsync(null);
    Assert.Same(monitor, main.CurrentPage);
    Assert.Equal(1, service.ApplyCalls);

    service.ReleaseEvents.TrySetResult();
    await applying;

    Assert.True(main.NavigateToProfilesCommand.CanExecute(null));
    await main.NavigateToProfilesCommand.ExecuteAsync(null);
    Assert.Same(main.ProfileSelection, main.CurrentPage);
  }

  [Fact]
  public async Task DisposeAsyncWaitsForActiveExecutionCleanupBeforeCompleting()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldAfterCancellation = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    await ((AsyncRelayCommand)main.ResourceSelection!.StartConfigurationCommand)
        .ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);
    Task applying = plan.ApplyCommand.ExecuteAsync(null);
    await service.ApplyStarted.Task;

    Task disposing = main.DisposeAsync().AsTask();
    await service.CancellationObserved.Task;

    Assert.False(disposing.IsCompleted);
    Assert.Equal(1, service.ApplyCalls);

    service.ReleaseTerminalCompletion.TrySetResult();
    await disposing;
    await applying;
    Assert.False(Assert.IsType<ExecutionMonitorViewModel>(main.CurrentPage).IsRunning);
  }

  private static ExecutionMonitorViewModel CreateMonitor(
      IReviewedPlanEnvironmentRunService service,
      IRunEventSink events,
      IUiDispatcher dispatcher) => new(
          service,
          events,
          new LogRedactor(),
          dispatcher,
          Request(),
          new string('A', 64));

  private static RunRequest Request() => new(
      "profiles/csharp-developer.yaml",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static DeveloperProfile Profile() => new()
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

  private static RunEvent Event(Guid runId, long sequence, double progress, string message) =>
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

  private static ExecutionRun CompletedRun(
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

  private static ExecutionRun InspectRun(bool executable)
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

  private sealed class RecordingDispatcher : IUiDispatcher
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

  private sealed class RejectingDispatcher : IUiDispatcher
  {
    public int EnqueueCalls { get; private set; }

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
      EnqueueCalls++;
      return Task.FromException(new UiDispatcherUnavailableException());
    }
  }

  private sealed class ControlledDispatcher : IUiDispatcher
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

  private sealed class FixedProfileCatalog(DeveloperProfile profile) : IProfileCatalog
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

  private sealed class FakeEnvironmentRunService(
      IRunEventSink events,
      Guid runId) : IReviewedPlanEnvironmentRunService
  {
    public IReadOnlyList<RunEvent> Events { get; set; } = [];
    public ExecutionRun ApplyResult { get; set; } = CompletedRun();
    public ExecutionRun InspectResult { get; set; } = InspectRun(executable: true);
    public int InspectCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public RunRequest? AppliedRequest { get; private set; }
    public string? ReviewedPlanFingerprint { get; private set; }
    public Guid? RetriedRunId { get; private set; }
    public IReadOnlySet<string>? RetriedResourceIds { get; private set; }
    public CancellationToken RetryCancellationToken { get; private set; }
    public CancellationToken ApplyCancellationToken { get; private set; }
    public bool HoldAfterCancellation { get; init; }
    public bool HoldRetryAfterCancellation { get; init; }
    public bool HoldRetryUntilReleased { get; init; }
    public bool HoldAfterEvents { get; set; }
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

    public Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      InspectCalls++;
      return Task.FromResult(InspectResult);
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

    public Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);

    public Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
  }

  private sealed class TestRunEventSink : IRunEventSink
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
