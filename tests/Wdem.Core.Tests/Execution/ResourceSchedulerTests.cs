using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Core.Tests.Execution;

public sealed class ResourceSchedulerTests
{
  private readonly IResourceScheduler _scheduler = new ResourceScheduler();

  [Fact]
  public void CancellationDrainDeadline_EnforcesPlatformTimerUpperBound()
  {
    var maximumSupportedBudget = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    using var deadline = new CancellationDrainDeadline(
        maximumSupportedBudget,
        CancellationToken.None);
    var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        new CancellationDrainDeadline(
            maximumSupportedBudget + TimeSpan.FromTicks(1),
            CancellationToken.None));

    Assert.Equal(maximumSupportedBudget, deadline.Remaining);
    Assert.Equal("budget", error.ParamName);
  }

  [Fact]
  public void CancellationDrainDeadline_UsesMaximumReservationInsteadOfSum()
  {
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        CancellationToken.None);

    using var first = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(200));
    using var second = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(300));

    Assert.Equal(TimeSpan.FromMilliseconds(400), deadline.Remaining);
  }

  [Fact]
  public void CancellationDrainDeadline_DuplicateReservationsAreIdempotent()
  {
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        CancellationToken.None);

    using var first = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(250));
    using var second = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(250));

    Assert.Equal(TimeSpan.FromMilliseconds(350), deadline.Remaining);
  }

  [Fact]
  public void CancellationDrainDeadline_ReleasingLargestReservationRecomputesMaximum()
  {
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        CancellationToken.None);
    using var smaller = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(200));
    var larger = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(300));

    larger.Dispose();

    Assert.Equal(TimeSpan.FromMilliseconds(300), deadline.Remaining);
  }

  [Fact]
  public void CancellationDrainDeadline_ConcurrentReservationsUseLargestTimeout()
  {
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        CancellationToken.None);
    var timeouts = new[]
    {
      TimeSpan.FromMilliseconds(125),
      TimeSpan.FromMilliseconds(300),
      TimeSpan.FromMilliseconds(225),
      TimeSpan.FromMilliseconds(300)
    };

    var reservations = new IDisposable?[timeouts.Length];
    try
    {
      Parallel.For(0, timeouts.Length, index =>
      {
        reservations[index] = deadline.RegisterPotentialFinalization(timeouts[index]);
      });

      Assert.Equal(TimeSpan.FromMilliseconds(400), deadline.Remaining);
    }
    finally
    {
      foreach (var reservation in reservations)
      {
        reservation?.Dispose();
      }
    }
  }

  [Fact]
  public void CancellationDrainDeadline_AcceptsReservationAfterCancellationStarts()
  {
    using var cancellation = new CancellationTokenSource();
    using var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        cancellation.Token);

    cancellation.Cancel();

    using var reservation = deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(300));
    Assert.True(deadline.Remaining > TimeSpan.FromMilliseconds(200));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  public void CancellationDrainDeadline_RejectsReservationsAfterDisposal(
      int durationMilliseconds)
  {
    var deadline = new CancellationDrainDeadline(
        TimeSpan.FromMilliseconds(100),
        CancellationToken.None);
    deadline.Dispose();

    Assert.Throws<ObjectDisposedException>(() => deadline.RegisterPotentialFinalization(
        TimeSpan.FromMilliseconds(durationMilliseconds)));
  }

  [Fact]
  public void CancellationDrainDeadline_EnforcesTimerBoundAcrossBaseAndReservation()
  {
    var maximumSupportedBudget = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    var baseBudget = TimeSpan.FromMilliseconds(100);
    using var deadline = new CancellationDrainDeadline(
        baseBudget,
        CancellationToken.None);
    using var accepted = deadline.RegisterPotentialFinalization(
        maximumSupportedBudget - baseBudget);

    var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        deadline.RegisterPotentialFinalization(maximumSupportedBudget));

    Assert.Equal(maximumSupportedBudget, deadline.Remaining);
    Assert.Equal("duration", error.ParamName);
  }

  [Fact]
  public async Task ExecuteAsync_ReportsReadyRunningCompletedAndBlockedBeforeAdvancing()
  {
    var transitions = new List<(string Id, ExecutionState State, ExecutionOutcome? Outcome)>();

    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) => Task.FromResult(Result(
            resource.Definition.Id,
            ExecutionOutcome.Failed)),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None,
        transition =>
        {
          transitions.Add((transition.ResourceId, transition.State, transition.Outcome));
          return Task.CompletedTask;
        });

    Assert.Equal(ExecutionOutcome.Failed, result.Results["a"].Outcome);
    Assert.Equal(
        [
          ("a", ExecutionState.Ready, null),
          ("a", ExecutionState.Running, null),
          ("a", ExecutionState.Completed, ExecutionOutcome.Failed),
          ("b", ExecutionState.Blocked, ExecutionOutcome.Skipped)
        ],
        transitions);
  }

  [Fact]
  public async Task ExecuteAsync_ObserverFailureCancelsAndAwaitsRunningWorkBeforeThrowing()
  {
    using var cancellation = new CancellationTokenSource();
    var firstStarted = NewGate();
    var firstFinished = NewGate();
    var invoked = new List<string>();

    var execution = _scheduler.ExecuteAsync(
        IndependentPlan("a", "b", "c"),
        async (resource, token) =>
        {
          invoked.Add(resource.Definition.Id);
          firstStarted.TrySetResult();
          try
          {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
          }
          finally
          {
            firstFinished.TrySetResult();
          }

          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 3 },
        maximumConcurrency: 3,
        cancellation.Token,
        transition => transition.ResourceId == "b" &&
            transition.State == ExecutionState.Ready
            ? Task.FromException(new InvalidOperationException("snapshot failed"))
            : Task.CompletedTask);

    try
    {
      var error = await Assert.ThrowsAsync<InvalidOperationException>(
          () => execution.WaitAsync(TimeSpan.FromSeconds(5)));

      Assert.Equal("snapshot failed", error.Message);
      Assert.True(firstStarted.Task.IsCompleted);
      Assert.True(firstFinished.Task.IsCompleted);
      Assert.Equal(["a"], invoked);
    }
    finally
    {
      await cancellation.CancelAsync();
      await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [Fact]
  public async Task ExecuteAsync_ObserverFailurePreservesCauseWhenRunningWorkIgnoresCancellation()
  {
    var scheduler = new ResourceScheduler(TimeSpan.FromMilliseconds(50));
    var runningStarted = NewGate();
    var releaseRunning = new TaskCompletionSource<ResourceResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var execution = scheduler.ExecuteAsync(
        IndependentPlan("a", "b"),
        async (resource, _) =>
        {
          runningStarted.TrySetResult();
          return await releaseRunning.Task;
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 2 },
        maximumConcurrency: 2,
        CancellationToken.None,
        transition => transition.ResourceId == "b" &&
            transition.State == ExecutionState.Ready
            ? Task.FromException(new InvalidOperationException("snapshot failed promptly"))
            : Task.CompletedTask);

    try
    {
      await runningStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
      var error = await Assert.ThrowsAsync<InvalidOperationException>(
          () => execution.WaitAsync(TimeSpan.FromSeconds(1)));

      Assert.Equal("snapshot failed promptly", error.Message);
    }
    finally
    {
      releaseRunning.TrySetResult(Result("a", ExecutionOutcome.Succeeded));
    }
  }

  [Fact]
  public async Task ExecuteAsync_AlreadyCancelledReportsTerminalTransitionsBeforeReturning()
  {
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    var transitions = new List<ResourceResult>();

    var result = await _scheduler.ExecuteAsync(
        IndependentPlan("a", "b"),
        (_, _) => throw new InvalidOperationException("No work should start."),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        cancellation.Token,
        transition =>
        {
          transitions.Add(transition);
          return Task.CompletedTask;
        });

    Assert.Equal(["a", "b"], transitions.Select(transition => transition.ResourceId));
    Assert.All(transitions, transition =>
    {
      Assert.Equal(ExecutionState.Completed, transition.State);
      Assert.Equal(ExecutionOutcome.Cancelled, transition.Outcome);
    });
    Assert.All(result.Results.Values, resourceResult =>
        Assert.Equal(ExecutionOutcome.Cancelled, resourceResult.Outcome));
  }

  [Fact]
  public async Task ExecuteAsync_CancelledDuringRunningTransitionDoesNotInvokeDelegate()
  {
    using var cancellation = new CancellationTokenSource();
    var runningObserved = NewGate();
    var releaseRunningObserver = NewGate();
    var transitions = new List<ResourceResult>();
    var invoked = 0;

    var execution = _scheduler.ExecuteAsync(
        IndependentPlan("a"),
        (resource, _) =>
        {
          Interlocked.Increment(ref invoked);
          return Task.FromResult(Result(
              resource.Definition.Id,
              ExecutionOutcome.Succeeded));
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        cancellation.Token,
        async transition =>
        {
          transitions.Add(transition);
          if (transition.State == ExecutionState.Running)
          {
            runningObserved.TrySetResult();
            await releaseRunningObserver.Task;
          }
        });

    await runningObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancellation.CancelAsync();
    releaseRunningObserver.TrySetResult();
    var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Equal(0, invoked);
    Assert.Equal(ExecutionOutcome.Cancelled, result.Results["a"].Outcome);
    Assert.Contains(transitions, transition =>
        transition.ResourceId == "a" &&
        transition.State == ExecutionState.Completed &&
        transition.Outcome == ExecutionOutcome.Cancelled);
  }

  [Fact]
  public async Task ExecuteAsync_CallerCancellationReturnsWhenRunningDelegateIgnoresToken()
  {
    var scheduler = new ResourceScheduler(TimeSpan.FromMilliseconds(50));
    using var cancellation = new CancellationTokenSource();
    var started = NewGate();
    var release = new TaskCompletionSource<ResourceResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var transitions = new List<ResourceResult>();
    var execution = scheduler.ExecuteAsync(
        IndependentPlan("a"),
        async (_, _) =>
        {
          started.TrySetResult();
          return await release.Task;
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        cancellation.Token,
        transition =>
        {
          transitions.Add(transition);
          return Task.CompletedTask;
        });
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    try
    {
      await cancellation.CancelAsync();
      var result = await execution.WaitAsync(TimeSpan.FromSeconds(1));

      Assert.Equal(ExecutionState.Completed, result.Results["a"].State);
      Assert.Equal(ExecutionOutcome.Cancelled, result.Results["a"].Outcome);
      Assert.Contains(transitions, transition =>
          transition.ResourceId == "a" &&
          transition.State == ExecutionState.Completed &&
          transition.Outcome == ExecutionOutcome.Cancelled);
    }
    finally
    {
      release.TrySetException(new InvalidOperationException("late provider failure"));
    }
  }

  [Fact]
  public async Task ExecuteAsync_RunningObserverFailureIsNotReportedAsProviderFailure()
  {
    var invoked = 0;

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _scheduler.ExecuteAsync(
            IndependentPlan("a"),
            (resource, _) =>
            {
              Interlocked.Increment(ref invoked);
              return Task.FromResult(Result(
                  resource.Definition.Id,
                  ExecutionOutcome.Succeeded));
            },
            _ => new ProviderCapabilities(),
            maximumConcurrency: 1,
            CancellationToken.None,
            transition => transition.State == ExecutionState.Running
                ? Task.FromException(new InvalidOperationException("running snapshot failed"))
                : Task.CompletedTask));

    Assert.Equal("running snapshot failed", error.Message);
    Assert.Equal(0, invoked);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(33)]
  public async Task ExecuteAsync_MaximumConcurrencyOutsideSupportedRange_Throws(int maximumConcurrency)
  {
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _scheduler.ExecuteAsync(
        IndependentPlan("a"),
        (resource, _) => Task.FromResult(Result(resource.Definition.Id, ExecutionOutcome.Succeeded)),
        _ => new ProviderCapabilities(),
        maximumConcurrency,
        CancellationToken.None));
  }

  [Fact]
  public async Task ExecuteAsync_EmptyPlan_ReturnsEmptyCaseInsensitiveResults()
  {
    var result = await _scheduler.ExecuteAsync(
        Plan(Array.Empty<PlannedResource>()),
        (_, _) => throw new InvalidOperationException("No resource should execute."),
        _ => throw new InvalidOperationException("No capabilities should be requested."),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Empty(result.Results);
    Assert.False(result.Results.ContainsKey("missing"));
  }

  [Theory]
  [InlineData(ExecutionOutcome.Succeeded)]
  [InlineData(ExecutionOutcome.NotRequired)]
  public async Task ExecuteAsync_SuccessfulDependency_AllowsDependentToRun(
      ExecutionOutcome dependencyOutcome)
  {
    var executionOrder = new List<string>();
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) =>
        {
          executionOrder.Add(resource.Definition.Id);
          var outcome = resource.Definition.Id == "a"
              ? dependencyOutcome
              : ExecutionOutcome.Succeeded;
          return Task.FromResult(Result(resource.Definition.Id, outcome));
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 2,
        CancellationToken.None);

    Assert.Equal(["a", "b"], executionOrder);
    Assert.Equal(ExecutionOutcome.Succeeded, result.Results["B"].Outcome);
  }

  [Fact]
  public async Task ExecuteAsync_FailedDependency_BlocksAllDownstreamResources()
  {
    var invoked = new List<string>();
    var result = await _scheduler.ExecuteAsync(
        DiamondPlan(),
        (resource, _) =>
        {
          invoked.Add(resource.Definition.Id);
          return Task.FromResult(Result("a", ExecutionOutcome.Failed));
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 2 },
        maximumConcurrency: 4,
        CancellationToken.None);

    Assert.Equal(["a"], invoked);
    Assert.All(new[] { "b", "c", "d" }, id =>
    {
      var blocked = result.Results[id];
      Assert.Equal(ExecutionState.Blocked, blocked.State);
      Assert.Equal(ExecutionOutcome.Skipped, blocked.Outcome);
      Assert.NotNull(blocked.Error);
      Assert.Equal(WdemErrorCode.DependencyError, blocked.Error.Code);
      Assert.False(blocked.Error.IsRetryable);
      Assert.Equal(
          $"Resource '{id}' was not started because dependency 'a' did not succeed.",
          blocked.Error.Detail);
    });
  }

  [Fact]
  public async Task ExecuteAsync_RespectsGlobalConcurrency()
  {
    var release = NewGate();
    var twoStarted = NewGate();
    var observed = 0;
    var peak = 0;
    var started = 0;

    var execution = _scheduler.ExecuteAsync(
        IndependentPlan("a", "b", "c"),
        async (resource, token) =>
        {
          var active = Interlocked.Increment(ref observed);
          SetMaximum(ref peak, active);
          if (Interlocked.Increment(ref started) == 2)
          {
            twoStarted.TrySetResult();
          }

          await release.Task.WaitAsync(token);
          Interlocked.Decrement(ref observed);
          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        resource => new ProviderCapabilities
        {
          MaxConcurrentOperations = 3,
          ConcurrencyGroup = resource.Definition.Id
        },
        maximumConcurrency: 2,
        CancellationToken.None);

    await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(2, Volatile.Read(ref peak));
    release.TrySetResult();
    await execution;
    Assert.Equal(2, peak);
  }

  [Fact]
  public async Task ExecuteAsync_RespectsSharedProviderConcurrencyGroup()
  {
    var release = NewGate();
    var firstStarted = NewGate();
    var observed = 0;
    var peak = 0;

    var execution = _scheduler.ExecuteAsync(
        IndependentPlan("a", "b", "c"),
        async (resource, token) =>
        {
          var active = Interlocked.Increment(ref observed);
          SetMaximum(ref peak, active);
          firstStarted.TrySetResult();
          await release.Task.WaitAsync(token);
          Interlocked.Decrement(ref observed);
          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities
        {
          MaxConcurrentOperations = 1,
          ConcurrencyGroup = "vs-installer"
        },
        maximumConcurrency: 3,
        CancellationToken.None);

    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    release.TrySetResult();
    await execution;

    Assert.Equal(1, peak);
  }

  [Fact]
  public async Task ExecuteAsync_DifferentDefaultProviderGroups_RunConcurrently()
  {
    var bothStarted = NewGate();
    var started = 0;
    var plan = Plan(
        Resource("a", provider: "one"),
        Resource("b", provider: "two"));

    var execution = _scheduler.ExecuteAsync(
        plan,
        async (resource, token) =>
        {
          if (Interlocked.Increment(ref started) == 2)
          {
            bothStarted.TrySetResult();
          }

          await bothStarted.Task.WaitAsync(token);
          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 1 },
        maximumConcurrency: 2,
        CancellationToken.None);

    await execution.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(2, started);
  }

  [Fact]
  public async Task ExecuteAsync_CompletedDependencyLaunchesDependentWhileUnrelatedWorkRuns()
  {
    var unrelatedStarted = NewGate();
    var releaseUnrelated = NewGate();
    var dependentStarted = NewGate();
    var plan = Plan(
        [Resource("a"), Resource("x")],
        [Resource("b", ["a"])]);

    var execution = _scheduler.ExecuteAsync(
        plan,
        async (resource, token) =>
        {
          switch (resource.Definition.Id)
          {
            case "a":
              await unrelatedStarted.Task.WaitAsync(token);
              break;
            case "x":
              unrelatedStarted.TrySetResult();
              await releaseUnrelated.Task.WaitAsync(token);
              break;
            case "b":
              dependentStarted.TrySetResult();
              break;
          }

          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 3 },
        maximumConcurrency: 3,
        CancellationToken.None);

    try
    {
      await dependentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.False(releaseUnrelated.Task.IsCompleted);
    }
    finally
    {
      releaseUnrelated.TrySetResult();
      await execution.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [Fact]
  public async Task ExecuteAsync_CancellationStopsNewDelegatesAndCompletesUnstartedResources()
  {
    using var cancellation = new CancellationTokenSource();
    var firstStarted = NewGate();
    var invoked = 0;
    var transitions = new List<ResourceResult>();

    var execution = _scheduler.ExecuteAsync(
        IndependentPlan("a", "b", "c"),
        async (resource, token) =>
        {
          Interlocked.Increment(ref invoked);
          firstStarted.TrySetResult();
          await Task.Delay(Timeout.InfiniteTimeSpan, token);
          return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 3 },
        maximumConcurrency: 1,
        cancellation.Token,
        transition =>
        {
          transitions.Add(transition);
          return Task.CompletedTask;
        });

    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancellation.CancelAsync();
    var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Equal(1, invoked);
    Assert.All(result.Results.Values, resourceResult =>
    {
      Assert.Equal(ExecutionState.Completed, resourceResult.State);
      Assert.Equal(ExecutionOutcome.Cancelled, resourceResult.Outcome);
    });
    Assert.NotNull(result.Results["a"].StartedAtUtc);
    Assert.Null(result.Results["b"].StartedAtUtc);
    Assert.Null(result.Results["c"].StartedAtUtc);
    Assert.Contains(transitions, transition =>
        transition.ResourceId == "b" &&
        transition.State == ExecutionState.Completed &&
        transition.Outcome == ExecutionOutcome.Cancelled);
    Assert.Contains(transitions, transition =>
        transition.ResourceId == "c" &&
        transition.State == ExecutionState.Completed &&
        transition.Outcome == ExecutionOutcome.Cancelled);
  }

  [Fact]
  public async Task ExecuteAsync_DelegateExceptionFailsResourceAndBlocksDependent()
  {
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) => resource.Definition.Id == "a"
            ? Task.FromException<ResourceResult>(new InvalidOperationException("provider failed"))
            : Task.FromResult(Result(resource.Definition.Id, ExecutionOutcome.Succeeded)),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, result.Results["a"].State);
    Assert.Equal(ExecutionOutcome.Failed, result.Results["a"].Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, result.Results["a"].Error?.Code);
    Assert.NotNull(result.Results["a"].StartedAtUtc);
    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
  }

  [Fact]
  public async Task ExecuteAsync_UnsolicitedDelegateCancellationFailsResourceAndBlocksDependent()
  {
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) => resource.Definition.Id == "a"
            ? Task.FromCanceled<ResourceResult>(new CancellationToken(canceled: true))
            : Task.FromResult(Result(resource.Definition.Id, ExecutionOutcome.Succeeded)),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, result.Results["a"].State);
    Assert.Equal(ExecutionOutcome.Failed, result.Results["a"].Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, result.Results["a"].Error?.Code);
    Assert.NotNull(result.Results["a"].StartedAtUtc);
    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
    Assert.Equal(ExecutionOutcome.Skipped, result.Results["b"].Outcome);
  }

  [Theory]
  [InlineData(null)]
  [InlineData(999)]
  public async Task ExecuteAsync_MalformedDelegateOutcome_FailsResourceAndBlocksDependent(
      int? rawOutcome)
  {
    var returnedStartedAtUtc = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);
    var returnedEndedAtUtc = returnedStartedAtUtc.AddSeconds(1);
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) => Task.FromResult(new ResourceResult
        {
          ResourceId = "wrong-id",
          State = ExecutionState.Running,
          Outcome = rawOutcome.HasValue ? (ExecutionOutcome)rawOutcome.Value : null,
          StartedAtUtc = returnedStartedAtUtc,
          EndedAtUtc = returnedEndedAtUtc
        }),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    var failed = result.Results["a"];
    Assert.Equal("a", failed.ResourceId);
    Assert.Equal(ExecutionState.Completed, failed.State);
    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.NotNull(failed.StartedAtUtc);
    Assert.NotNull(failed.EndedAtUtc);
    Assert.True(failed.EndedAtUtc >= failed.StartedAtUtc);
    Assert.Equal(WdemErrorCode.ProviderError, failed.Error?.Code);
    Assert.False(failed.Error?.IsRetryable);

    var blocked = result.Results["b"];
    Assert.Equal(ExecutionState.Blocked, blocked.State);
    Assert.Equal(ExecutionOutcome.Skipped, blocked.Outcome);
    Assert.Equal(
        "Resource 'b' was not started because dependency 'a' did not succeed.",
        blocked.Error?.Detail);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Succeeded)]
  [InlineData(ExecutionOutcome.NotRequired)]
  public async Task ExecuteAsync_DependencySatisfyingResultWithCancelledStep_FailsAndBlocksDependent(
      ExecutionOutcome resourceOutcome)
  {
    var invoked = new List<string>();
    var stepStartedAtUtc = new DateTimeOffset(2026, 8, 28, 2, 3, 4, TimeSpan.Zero);
    var stepEndedAtUtc = stepStartedAtUtc.AddSeconds(1);
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) =>
        {
          invoked.Add(resource.Definition.Id);
          return Task.FromResult(Result(resource.Definition.Id, resourceOutcome) with
          {
            StepResults =
            [
              new StepResult
              {
                StepId = "install",
                Name = "Install",
                State = ExecutionState.Completed,
                Outcome = ExecutionOutcome.Cancelled,
                StartedAtUtc = stepStartedAtUtc,
                EndedAtUtc = stepEndedAtUtc
              }
            ]
          });
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(["a"], invoked);
    var failed = result.Results["a"];
    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, failed.Error?.Code);
    Assert.False(failed.Error?.IsRetryable);
    var step = Assert.Single(failed.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, step.Outcome);
    Assert.Equal(stepStartedAtUtc, step.StartedAtUtc);
    Assert.Equal(stepEndedAtUtc, step.EndedAtUtc);
    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
    Assert.Equal(ExecutionOutcome.Skipped, result.Results["b"].Outcome);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Succeeded)]
  [InlineData(ExecutionOutcome.NotRequired)]
  public async Task ExecuteAsync_DependencySatisfyingResultWithNonZeroExitCode_FailsAndBlocksDependent(
      ExecutionOutcome resourceOutcome)
  {
    var invoked = new List<string>();
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) =>
        {
          invoked.Add(resource.Definition.Id);
          return Task.FromResult(Result(resource.Definition.Id, resourceOutcome) with
          {
            StepResults =
            [
              new StepResult
              {
                StepId = "install",
                Name = "Install",
                State = ExecutionState.Completed,
                Outcome = ExecutionOutcome.Succeeded,
                ProcessExitCode = 1603
              }
            ]
          });
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(["a"], invoked);
    var failed = result.Results["a"];
    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, failed.Error?.Code);
    Assert.False(failed.Error?.IsRetryable);
    Assert.Equal(1603, Assert.Single(failed.StepResults).ProcessExitCode);
    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
    Assert.Equal(ExecutionOutcome.Skipped, result.Results["b"].Outcome);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Succeeded, ExecutionState.Completed, null)]
  [InlineData(ExecutionOutcome.NotRequired, ExecutionState.Completed, 999)]
  [InlineData(ExecutionOutcome.Succeeded, ExecutionState.Running, (int)ExecutionOutcome.Succeeded)]
  [InlineData(ExecutionOutcome.NotRequired, ExecutionState.Ready, (int)ExecutionOutcome.NotRequired)]
  public async Task ExecuteAsync_DependencySatisfyingResultWithMalformedStep_FailsAndBlocksDependent(
      ExecutionOutcome resourceOutcome,
      ExecutionState stepState,
      int? rawStepOutcome)
  {
    var invoked = new List<string>();
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) =>
        {
          invoked.Add(resource.Definition.Id);
          return Task.FromResult(Result(resource.Definition.Id, resourceOutcome) with
          {
            StepResults =
            [
              new StepResult
              {
                StepId = "install",
                Name = "Install",
                State = stepState,
                Outcome = rawStepOutcome.HasValue
                    ? (ExecutionOutcome)rawStepOutcome.Value
                    : null
              }
            ]
          });
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(["a"], invoked);
    var failed = result.Results["a"];
    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, failed.Error?.Code);
    Assert.False(failed.Error?.IsRetryable);
    Assert.Single(failed.StepResults);
    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
    Assert.Equal(ExecutionOutcome.Skipped, result.Results["b"].Outcome);
  }

  [Fact]
  public async Task ExecuteAsync_NotRequiredResultWithCompletedNotRequiredStep_AllowsDependent()
  {
    var invoked = new List<string>();
    var result = await _scheduler.ExecuteAsync(
        ChainPlan("a", "b"),
        (resource, _) =>
        {
          invoked.Add(resource.Definition.Id);
          return Task.FromResult(Result(resource.Definition.Id, ExecutionOutcome.NotRequired) with
          {
            StepResults = resource.Definition.Id == "a"
                ?
                [
                  new StepResult
                  {
                    StepId = "install",
                    Name = "Install",
                    State = ExecutionState.Completed,
                    Outcome = ExecutionOutcome.NotRequired,
                    ProcessExitCode = 0
                  }
                ]
                : []
          });
        },
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);

    Assert.Equal(["a", "b"], invoked);
    Assert.Equal(ExecutionOutcome.NotRequired, result.Results["a"].Outcome);
    Assert.Equal(ExecutionOutcome.NotRequired, result.Results["b"].Outcome);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  public async Task ExecuteAsync_ProviderControlledResourceTimestamps_AreReplacedWithSchedulerInterval(
      int scenario)
  {
    var beforeExecution = DateTimeOffset.UtcNow;
    var (providerStartedAtUtc, providerEndedAtUtc) = scenario switch
    {
      0 => (beforeExecution.AddHours(-1), beforeExecution.AddHours(-2)),
      1 => (beforeExecution.AddDays(1), beforeExecution.AddDays(2)),
      _ => (beforeExecution.AddDays(-2), beforeExecution.AddDays(-1))
    };

    var result = await _scheduler.ExecuteAsync(
        IndependentPlan("a"),
        (resource, _) => Task.FromResult(Result(
            resource.Definition.Id,
            ExecutionOutcome.Succeeded) with
        {
          StartedAtUtc = providerStartedAtUtc,
          EndedAtUtc = providerEndedAtUtc
        }),
        _ => new ProviderCapabilities(),
        maximumConcurrency: 1,
        CancellationToken.None);
    var afterExecution = DateTimeOffset.UtcNow;

    var completed = result.Results["a"];
    Assert.NotNull(completed.StartedAtUtc);
    Assert.NotNull(completed.EndedAtUtc);
    Assert.InRange(completed.StartedAtUtc.Value, beforeExecution, afterExecution);
    Assert.InRange(completed.EndedAtUtc.Value, completed.StartedAtUtc.Value, afterExecution);
    Assert.NotEqual(providerStartedAtUtc, completed.StartedAtUtc);
    Assert.NotEqual(providerEndedAtUtc, completed.EndedAtUtc);
  }

  private static TaskCompletionSource NewGate() =>
      new(TaskCreationOptions.RunContinuationsAsynchronously);

  private static void SetMaximum(ref int maximum, int candidate)
  {
    int observed;
    do
    {
      observed = Volatile.Read(ref maximum);
      if (candidate <= observed)
      {
        return;
      }
    }
    while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
  }

  private static ExecutionPlan IndependentPlan(params string[] ids) =>
      Plan(ids.Select(id => Resource(id)).ToArray());

  private static ExecutionPlan ChainPlan(string first, string second) => Plan(
      [Resource(first)],
      [Resource(second, [first])]);

  private static ExecutionPlan DiamondPlan() => Plan(
      [Resource("a")],
      [Resource("b", ["a"]), Resource("c", ["a"])],
      [Resource("d", ["b", "c"])]);

  private static ExecutionPlan Plan(params PlannedResource[] resources) =>
      Plan([resources]);

  private static ExecutionPlan Plan(params PlannedResource[][] layers)
  {
    var resources = layers.SelectMany(layer => layer).ToArray();
    return new ExecutionPlan
    {
      PlanId = Guid.NewGuid(),
      Fingerprint = "test-plan",
      ProfileId = "test-profile",
      ProfileVersion = "1.0.0",
      Layers = layers.Select((layer, index) =>
          new ResourceGraphLayer(index, layer.Select(resource => resource.Definition.Id).ToArray()))
          .ToArray(),
      Resources = resources,
      IsExecutable = true
    };
  }

  private static PlannedResource Resource(
      string id,
      IReadOnlyList<string>? dependencies = null,
      string provider = "test")
  {
    var definition = new ResourceDefinition
    {
      Id = id,
      Type = "package",
      Provider = provider,
      Dependencies = dependencies ?? []
    };
    return new PlannedResource
    {
      Definition = definition,
      Origin = ResourceOrigin.Required,
      Dependencies = dependencies ?? [],
      ResourcePlan = new ResourcePlan
      {
        ResourceId = id,
        ResourceType = definition.Type,
        ProviderName = provider,
        DesiredStateFingerprint = $"fingerprint-{id}",
        Compliance = ComplianceStatus.Missing,
        IsExecutable = true
      },
      Status = PlannedResourceStatus.Ready,
      Risk = PlanRisk.Standard,
      RequiresElevation = false,
      IsDestructive = false,
      RestartPolicy = RestartPolicy.NoRestart
    };
  }

  private static ResourceResult Result(string id, ExecutionOutcome outcome) => new()
  {
    ResourceId = id,
    State = ExecutionState.Completed,
    Outcome = outcome,
    Progress = outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired ? 1 : 0,
    EndedAtUtc = DateTimeOffset.UtcNow
  };
}
