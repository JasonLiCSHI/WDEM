using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Desktop.ViewModels;
using Xunit;
using static Wdem.Desktop.Tests.ViewModels.ViewModelTestFixture;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class ExecutionMonitorViewModelTests
{
  [Fact]
  public async Task RecoverTracksOnlyFreshReplacementRunEvents()
  {
    var priorRunId = Guid.NewGuid();
    var unrelatedRunId = Guid.NewGuid();
    var replacementRunId = Guid.NewGuid();
    using var events = new RunEventHub();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoverResult = CompletedRun(("git", ExecutionOutcome.Succeeded, "install")) with
      {
        RunId = replacementRunId
      },
      RecoveryEvents =
      [
        Event(priorRunId, 1, 0.1, "stale prior event"),
        Event(unrelatedRunId, 1, 0.2, "unrelated event"),
        Event(replacementRunId, 1, 0.5, "fresh recovery event")
      ]
    };
    var monitor = new ExecutionMonitorViewModel(
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());

    await monitor.RecoverAsync(priorRunId);

    LogEntryViewModel log = Assert.Single(monitor.Logs);
    Assert.Equal("fresh recovery event", log.Message);
    Assert.Equal(replacementRunId, monitor.RunId);
    Assert.Equal(priorRunId, service.RecoveredRunId);
    Assert.Equal(0, service.ApplyCalls);
  }

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
}
