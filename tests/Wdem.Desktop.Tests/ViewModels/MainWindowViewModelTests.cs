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

public sealed class MainWindowViewModelTests
{
  [Fact]
  public void PlanResourcePresentationUsesRedactedDescriptionWithoutProviderFallback()
  {
    const string secret = "plan-description-secret";
    PlannedResource planned = Assert.Single(InspectRun(executable: true).Plan!.Resources);
    planned = planned with
    {
      Definition = planned.Definition with
      {
        DisplayName = $"Git {secret}",
        Description = $"Source control {secret}"
      }
    };

    var viewModel = new PlanResourceViewModel(planned, new LogRedactor([secret]));

    Assert.Equal("Git ***", viewModel.DisplayName);
    Assert.Equal("Source control ***", viewModel.Description);
    Assert.DoesNotContain(viewModel.Provider, viewModel.Description, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InitializeAsync_RecoveryCandidatesPreemptProfileSelectionWithUsefulDetails()
  {
    var candidate = RecoveryCandidate();
    var newerCandidate = candidate with
    {
      RunId = Guid.NewGuid(),
      StartedAtUtc = candidate.StartedAtUtc.AddHours(1)
    };
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [newerCandidate, candidate]
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());

    await main.InitializeAsync();

    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);
    Assert.Equal(2, recovery.Candidates.Count);
    var item = recovery.Candidates[0];
    Assert.Equal(candidate.RunId, item.RunId);
    Assert.Contains("csharp-developer", item.Profile, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("2", item.Status, StringComparison.Ordinal);
    Assert.Contains("git", item.PendingResources, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("2026", item.StartedAtDisplay, StringComparison.Ordinal);
    Assert.Equal(1, service.FindRecoveryCandidatesCalls);
    Assert.NotEmpty(main.ProfileSelection.Profiles);
  }

  [Fact]
  public async Task InitializeAsync_RedactsRecoveryProfileAndPendingResourceIds()
  {
    const string secret = "candidate-secret";
    var candidate = RecoveryCandidate() with
    {
      ProfileSourcePath = Path.Combine("profiles", $"{secret}.yaml"),
      PendingResourceIds = new HashSet<string>(
          [$"git-{secret}"],
          StringComparer.OrdinalIgnoreCase)
    };
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [candidate]
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor([secret]),
        new RecordingDispatcher());

    await main.InitializeAsync();

    RecoveryCandidateViewModel item = Assert.Single(
        Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage).Candidates);
    Assert.DoesNotContain(secret, item.Profile, StringComparison.Ordinal);
    Assert.DoesNotContain(secret, item.PendingResources, StringComparison.Ordinal);
    Assert.NotEmpty(item.Profile);
    Assert.NotEmpty(item.PendingResources);
  }

  [Fact]
  public async Task RecoverCandidate_UsesServiceAndExistingMonitorCompletionFlowExactlyOnce()
  {
    var candidate = RecoveryCandidate();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [candidate],
      HoldRecover = true,
      RecoverResult = CompletedRun(("git", ExecutionOutcome.Succeeded, "install"))
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);
    recovery.SelectedCandidate = Assert.Single(recovery.Candidates);

    Task recovering = recovery.RecoverCommand.ExecuteAsync(null);
    await service.RecoverStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await recovery.RecoverCommand.ExecuteAsync(null);

    Assert.Equal(1, service.RecoverCalls);
    Assert.Equal(candidate.RunId, service.RecoveredRunId);
    Assert.Equal(0, service.ApplyCalls);
    Assert.True(Assert.IsType<ExecutionMonitorViewModel>(main.CurrentPage).IsRunning);

    service.ReleaseRecover.TrySetResult();
    await recovering;

    var completion = Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.Equal(service.RecoverResult.RunId, completion.Run.RunId);
  }

  [Fact]
  public async Task RecoverFailureReturnsToRecoveryPageRedactedAndRetryable()
  {
    const string secret = "recover-operation-secret";
    var candidate = RecoveryCandidate();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [candidate],
      RecoverException = new InvalidOperationException($"recovery failed: {secret}")
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor([secret]),
        new RecordingDispatcher());
    await main.InitializeAsync();
    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);

    await recovery.RecoverCommand.ExecuteAsync(null);

    Assert.Same(recovery, main.CurrentPage);
    Assert.NotNull(main.ErrorMessage);
    Assert.DoesNotContain(secret, main.ErrorMessage, StringComparison.Ordinal);
    Assert.True(recovery.RecoverCommand.CanExecute(null));
    Assert.True(recovery.AbandonCommand.CanExecute(null));
    Assert.Equal(1, service.RecoverCalls);
    Assert.Equal(0, service.ApplyCalls);

    service.RecoverException = null;
    await recovery.RecoverCommand.ExecuteAsync(null);

    Assert.Equal(2, service.RecoverCalls);
    Assert.IsType<CompletionViewModel>(main.CurrentPage);
  }

  [Fact]
  public async Task AbandonCandidate_RemovesItAndSuppressesCompetingClicks()
  {
    var candidate = RecoveryCandidate();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [candidate],
      HoldAbandon = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);
    recovery.SelectedCandidate = Assert.Single(recovery.Candidates);

    Task abandoning = recovery.AbandonCommand.ExecuteAsync(null);
    await service.AbandonStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await recovery.AbandonCommand.ExecuteAsync(null);
    await recovery.RecoverCommand.ExecuteAsync(null);

    Assert.Equal(1, service.AbandonCalls);
    Assert.Equal(0, service.RecoverCalls);
    Assert.Equal(0, service.ApplyCalls);
    Assert.False(recovery.RecoverCommand.CanExecute(null));

    service.ReleaseAbandon.TrySetResult();
    await abandoning;

    Assert.Empty(recovery.Candidates);
    Assert.Same(main.ProfileSelection, main.CurrentPage);
  }

  [Fact]
  public async Task RecoveryDiscoveryFailureIsRedactedAndLeavesProfilesUsable()
  {
    const string secret = "recovery-discovery-secret";
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryDiscoveryException = new InvalidOperationException($"discovery failed: {secret}")
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor([secret]),
        new RecordingDispatcher());

    await main.InitializeAsync();

    Assert.Same(main.ProfileSelection, main.CurrentPage);
    Assert.NotNull(main.ErrorMessage);
    Assert.DoesNotContain(secret, main.ErrorMessage, StringComparison.Ordinal);
    Assert.NotEmpty(main.ProfileSelection.Profiles);
  }

  [Fact]
  public async Task DisposeDuringRecoveryDiscoveryCancelsAndPreventsLateNavigation()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [RecoveryCandidate()],
      HoldRecoveryDiscovery = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());

    Task initialization = main.InitializeAsync();
    await service.RecoveryDiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task disposal = main.DisposeAsync().AsTask();
    await service.RecoveryDiscoveryCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await disposal;
    await initialization;

    Assert.Same(main.ProfileSelection, main.CurrentPage);
    Assert.Equal(1, service.FindRecoveryCandidatesCalls);
  }

  [Fact]
  public async Task DisposeDuringRecoverCancelsAndWaitsWithoutLateCompletionNavigation()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [RecoveryCandidate()],
      HoldRecover = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);

    Task recovering = recovery.RecoverCommand.ExecuteAsync(null);
    await service.RecoverStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task disposal = main.DisposeAsync().AsTask();
    await service.RecoverCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Task.WhenAll(disposal, recovering);

    Assert.IsNotType<CompletionViewModel>(main.CurrentPage);
    Assert.Null(main.ErrorMessage);
    Assert.Equal(1, service.RecoverCalls);
    Assert.Equal(0, service.ApplyCalls);
  }

  [Fact]
  public async Task DisposeDuringAbandonCancelsAndWaitsWithoutRemovingCandidate()
  {
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      RecoveryCandidates = [RecoveryCandidate()],
      HoldAbandon = true
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher());
    await main.InitializeAsync();
    var recovery = Assert.IsType<RecoveryCandidatesViewModel>(main.CurrentPage);

    Task abandoning = recovery.AbandonCommand.ExecuteAsync(null);
    await service.AbandonStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task disposal = main.DisposeAsync().AsTask();
    await service.AbandonCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Task.WhenAll(disposal, abandoning);

    Assert.Same(recovery, main.CurrentPage);
    Assert.Single(recovery.Candidates);
    Assert.Null(main.ErrorMessage);
    Assert.Equal(1, service.AbandonCalls);
    Assert.Equal(0, service.RecoverCalls);
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
  public async Task CheckEnvironmentNavigatesToExportableCompletionWithoutApplying()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid());
    var exporter = new CapturingReportExporter();
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(),
        new RecordingDispatcher(),
        exporter);
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);

    await ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);

    var completion = Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.Equal(RunMode.Inspect, completion.Run.Mode);
    Assert.Equal(service.InspectResult.RunId, completion.Run.RunId);
    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
    Assert.False(completion.RetryFailedCommand.CanExecute(null));

    await completion.ExportAsync("inspection.md");

    Assert.Equal(completion.Run.RunId, exporter.Run?.RunId);
    Assert.Equal("inspection.md", exporter.FilePath);

    await completion.ReturnToPreviousCommand.ExecuteAsync(null);
    Assert.Same(main.ResourceSelection, main.CurrentPage);
  }

  [Fact]
  public async Task NonExecutableCheckEnvironmentStillCompletesAsInspectionReport()
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

    await ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);

    var completion = Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.Equal(RunMode.Inspect, completion.Run.Mode);
    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
  }

  [Fact]
  public async Task CheckEnvironmentFailureIsRedactedAndLeavesResourcesRecoverable()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      InspectException = new InvalidOperationException("token=inspect-secret")
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(["inspect-secret"]),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    ResourceSelectionViewModel resources = main.ResourceSelection!;

    await ((AsyncRelayCommand)resources.CheckEnvironmentCommand).ExecuteAsync(null);

    Assert.Same(resources, main.CurrentPage);
    Assert.NotNull(main.ErrorMessage);
    Assert.DoesNotContain("inspect-secret", main.ErrorMessage, StringComparison.Ordinal);
    Assert.Equal(main.ErrorMessage, resources.ErrorMessage);
    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
    Assert.True(resources.CheckEnvironmentCommand.CanExecute(null));
    Assert.True(resources.StartConfigurationCommand.CanExecute(null));
  }

  [Fact]
  public async Task StartConfigurationInspectionFailureReturnsToResourcesWithRedactedError()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      InspectException = new InvalidOperationException("token=start-secret")
    };
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(_ => null),
        service,
        events,
        new LogRedactor(["start-secret"]),
        new RecordingDispatcher());
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    ResourceSelectionViewModel resources = main.ResourceSelection!;

    await ((AsyncRelayCommand)resources.StartConfigurationCommand).ExecuteAsync(null);

    Assert.Same(resources, main.CurrentPage);
    Assert.NotNull(main.ErrorMessage);
    Assert.DoesNotContain("start-secret", main.ErrorMessage, StringComparison.Ordinal);
    Assert.Equal(main.ErrorMessage, resources.ErrorMessage);
    Assert.Equal(1, service.InspectCalls);
    Assert.Equal(0, service.ApplyCalls);
    Assert.True(resources.CheckEnvironmentCommand.CanExecute(null));
    Assert.True(resources.StartConfigurationCommand.CanExecute(null));
  }

  [Fact]
  public async Task DisposeDuringProfileLoadWaitsAndPreventsLateInspectionOrNavigation()
  {
    DeveloperProfile profile = Profile();
    var catalog = new SlowLoadProfileCatalog(profile);
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
    ResourceSelectionViewModel resources = main.ResourceSelection!;

    Task checking = ((AsyncRelayCommand)resources.CheckEnvironmentCommand).ExecuteAsync(null);
    await catalog.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task disposing = main.DisposeAsync().AsTask();
    await catalog.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    try
    {
      Assert.False(disposing.IsCompleted);
      Assert.Same(resources, main.CurrentPage);
      Assert.Equal(0, service.InspectCalls);
    }
    finally
    {
      catalog.ReleaseLoad.TrySetResult();
    }

    await disposing;
    await checking;
    Assert.Same(resources, main.CurrentPage);
    Assert.Equal(0, service.InspectCalls);
  }

  [Fact]
  public async Task ConcurrentResourceActionsCreateOnlyOneInspectionRun()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true
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
    ResourceSelectionViewModel resources = main.ResourceSelection!;
    var checkCommand = (AsyncRelayCommand)resources.CheckEnvironmentCommand;
    var startCommand = (AsyncRelayCommand)resources.StartConfigurationCommand;

    Task checking = checkCommand.ExecuteAsync(null);
    await service.InspectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task configuring = Task.CompletedTask;

    try
    {
      Assert.False(checkCommand.CanExecute(null));
      Assert.False(startCommand.CanExecute(null));
      configuring = startCommand.ExecuteAsync(null);
      Assert.True(configuring.IsCompletedSuccessfully);
      Assert.False(main.NavigateToProfilesCommand.CanExecute(null));
      Assert.False(main.NavigateToResourcesCommand.CanExecute(null));
    }
    finally
    {
      service.ReleaseInspection.TrySetResult();
    }

    await Task.WhenAll(checking, configuring);

    Assert.Equal(1, service.InspectCalls);
    Assert.IsType<CompletionViewModel>(main.CurrentPage);
    Assert.True(checkCommand.CanExecute(null));
    Assert.True(startCommand.CanExecute(null));
  }

  [Fact]
  public async Task DisposeAsyncCancelsAndWaitsForActiveInspection()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionAfterCancellation = true
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

    Task checking = ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);
    await service.InspectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Task disposing = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposing.IsCompleted);
    service.ReleaseInspectionAfterCancellation.TrySetResult();
    await disposing;
    await checking;
    Assert.Equal(1, service.InspectCalls);
    Assert.IsNotType<CompletionViewModel>(main.CurrentPage);
    Assert.True(main.ResourceSelection.CheckEnvironmentCommand.CanExecute(null));
    Assert.True(main.ResourceSelection.StartConfigurationCommand.CanExecute(null));
  }

  [Fact]
  public async Task DisposeCompletionIsSignaledBeforeInspectionCleanupNotification()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionAfterCancellation = true
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

    Task checking = ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task? disposing = null;
    var disposeCompletedDuringCleanupNotification = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    main.NavigateToProfilesCommand.CanExecuteChanged += (_, _) =>
    {
      if (service.InspectionCleanupCompleted.Task.IsCompleted && disposing is not null)
      {
        disposeCompletedDuringCleanupNotification.TrySetResult(
            disposing.Wait(TimeSpan.FromSeconds(5)));
      }
    };

    disposing = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposing.IsCompleted);
    service.ReleaseInspectionAfterCancellation.TrySetResult();

    Assert.True(await disposeCompletedDuringCleanupNotification.Task.WaitAsync(
        TimeSpan.FromSeconds(10)));
    await Task.WhenAll(disposing, checking);
  }

  [Fact]
  public async Task ThrowingNavigationObserverCannotHideDisposeCompletionFromLaterObserver()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionAfterCancellation = true
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

    Task checking = ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task? disposing = null;
    var disposeCompletedDuringCleanupNotification = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    main.NavigateToProfilesCommand.CanExecuteChanged += (_, _) =>
    {
      if (service.InspectionCleanupCompleted.Task.IsCompleted)
      {
        throw new InvalidOperationException("observer failed");
      }
    };
    main.NavigateToProfilesCommand.CanExecuteChanged += (_, _) =>
    {
      if (service.InspectionCleanupCompleted.Task.IsCompleted && disposing is not null)
      {
        disposeCompletedDuringCleanupNotification.TrySetResult(
            disposing.Wait(TimeSpan.FromSeconds(5)));
      }
    };

    disposing = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposing.IsCompleted);
    service.ReleaseInspectionAfterCancellation.TrySetResult();

    Assert.True(await disposeCompletedDuringCleanupNotification.Task.WaitAsync(
        TimeSpan.FromSeconds(10)));
    await Task.WhenAll(disposing, checking);
  }

  [Fact]
  public async Task ConcurrentDisposeCallsWaitForTheSameSlowInspectionTeardown()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldAfterEvents = true,
      HoldInspection = true,
      HoldInspectionAfterCancellation = true
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
    Task checking = ((AsyncRelayCommand)main.ResourceSelection!.CheckEnvironmentCommand)
        .ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Task firstDispose = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task secondDispose = main.DisposeAsync().AsTask();

    Assert.False(firstDispose.IsCompleted);
    Assert.False(secondDispose.IsCompleted);
    service.ReleaseInspectionAfterCancellation.TrySetResult();
    await Task.WhenAll(firstDispose, secondDispose, checking);
  }

  [Fact]
  public async Task DisposeWaitsForInspectionAndMonitorWhenCancellationCallbackThrows()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionOnCall = 2,
      HoldInspectionAfterCancellation = true,
      ThrowOnInspectionCancellation = true
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
    await service.EventsPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var monitor = Assert.IsType<ExecutionMonitorViewModel>(main.CurrentPage);
    service.ReleaseEvents.TrySetResult();
    await applying;
    Assert.IsType<CompletionViewModel>(main.CurrentPage);
    await main.NavigateToResourcesCommand.ExecuteAsync(null);

    Task checking = ((AsyncRelayCommand)main.ResourceSelection.CheckEnvironmentCommand)
        .ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Task firstDispose = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task secondDispose = main.DisposeAsync().AsTask();

    Assert.False(firstDispose.IsCompleted);
    Assert.False(secondDispose.IsCompleted);

    service.ReleaseInspectionAfterCancellation.TrySetResult();
    await checking;
    await Assert.ThrowsAsync<AggregateException>(() => firstDispose);
    await Assert.ThrowsAsync<AggregateException>(() => secondDispose);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.StartAsync());
  }

  [Fact]
  public async Task PlanRefreshAndResourceActionShareOneWindowInspectionFlight()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionOnCall = 2
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
    ResourceSelectionViewModel resources = main.ResourceSelection!;
    await ((AsyncRelayCommand)resources.StartConfigurationCommand).ExecuteAsync(null);
    var plan = Assert.IsType<PlanViewModel>(main.CurrentPage);

    Task refreshing = plan.InspectCommand.ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task competing = ((AsyncRelayCommand)resources.CheckEnvironmentCommand).ExecuteAsync(null);

    Assert.Equal(2, service.InspectCalls);
    service.ReleaseInspection.TrySetResult();
    await Task.WhenAll(refreshing, competing);
    Assert.Same(plan, main.CurrentPage);
    Assert.Equal(2, service.InspectCalls);
  }

  [Fact]
  public async Task DisposeWaitsForPlanRefreshAndIgnoresItsLateResult()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionOnCall = 2,
      IgnoreInspectionCancellation = true
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
    string originalAction = Assert.Single(plan.Resources).Action;
    service.InspectResult = InspectRun(executable: false);

    Task refreshing = plan.InspectCommand.ExecuteAsync(null);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Task disposing = main.DisposeAsync().AsTask();
    await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposing.IsCompleted);
    service.ReleaseInspection.TrySetResult();
    await disposing;
    await refreshing;
    Assert.Same(plan, main.CurrentPage);
    Assert.Equal(originalAction, Assert.Single(plan.Resources).Action);
    Assert.Empty(plan.Errors);
    Assert.Equal(2, service.InspectCalls);
  }

  [Fact]
  public async Task PlanRefreshCallerCancellationReachesWindowInspectionAndPreventsPresentation()
  {
    DeveloperProfile profile = Profile();
    var events = new TestRunEventSink();
    var service = new FakeEnvironmentRunService(events, Guid.NewGuid())
    {
      HoldInspection = true,
      HoldInspectionOnCall = 2
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
    string originalAction = Assert.Single(plan.Resources).Action;
    service.InspectResult = InspectRun(executable: false);
    using var cancellation = new CancellationTokenSource();

    Task refreshing = plan.InitializeAsync(cancellation.Token);
    await service.HeldInspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    bool cancellationObserved;
    try
    {
      await service.InspectionCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
      cancellationObserved = true;
    }
    catch (TimeoutException)
    {
      cancellationObserved = false;
    }

    service.ReleaseInspection.TrySetResult();
    Exception? failure = await Record.ExceptionAsync(() => refreshing);

    Assert.True(cancellationObserved);
    Assert.IsAssignableFrom<OperationCanceledException>(failure);
    Assert.Equal(originalAction, Assert.Single(plan.Resources).Action);
    Assert.Same(plan, main.CurrentPage);
  }

  [Fact]
  public async Task FailedPlanRefreshKeepsExistingPlanAndShowsError()
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
    string originalAction = Assert.Single(plan.Resources).Action;
    service.InspectException = new InvalidOperationException("refresh failed");

    await plan.InspectCommand.ExecuteAsync(null);

    Assert.Same(plan, main.CurrentPage);
    Assert.Equal(originalAction, Assert.Single(plan.Resources).Action);
    Assert.Contains("操作未完成", plan.ErrorMessage, StringComparison.Ordinal);
    Assert.Equal(2, service.InspectCalls);
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
}
