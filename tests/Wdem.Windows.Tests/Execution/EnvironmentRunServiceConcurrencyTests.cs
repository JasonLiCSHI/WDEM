using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Wdem.Windows.Configuration;
using Wdem.Windows.Persistence;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Execution;

public sealed class EnvironmentRunServiceConcurrencyTests : IDisposable
{
  public static TheoryData<DateTimeOffset, DateTimeOffset> FutureClaimReadTimes => new()
  {
    {
      new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
      new DateTimeOffset(2029, 12, 31, 23, 59, 59, TimeSpan.Zero)
    },
    {
      DateTimeOffset.MaxValue,
      new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)
    }
  };

  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-recovery-concurrency-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_ReSharperFinalVerificationPersistsPostCommitMismatch()
  {
    var profiles = Path.Combine(_directory, "profiles");
    var destinationRoot = Path.Combine(_directory, "JetBrains");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("verified ReSharper settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var settings = new ResourceDefinition
    {
      Id = "resharper-settings",
      Type = "resharper-settings",
      Provider = "file",
      Dependencies = ["resharper"],
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["sourcePath"] = "team.DotSettings",
        ["expectedSha256"] = Convert.ToHexString(SHA256.HashData(source)),
        ["destinationPath"] = Path.Combine(
            "Shared",
            "vAny",
            "GlobalSettingsStorage.DotSettings")
      }
    };
    var profile = new DeveloperProfile
    {
      Id = "developer",
      Version = "1.0.0",
      DisplayName = "Developer",
      Description = "Developer workstation",
      RequiredResources = [new ProfileResourceReference { Id = settings.Id }],
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["resharper"] = new ResourceDefinition
        {
          Id = "resharper",
          Type = "package",
          Provider = "fake"
        },
        [settings.Id] = settings
      }
    };
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_directory, profiles),
        new ConfigurationImporter(
            afterDestinationMove: null,
            afterDestinationDirectoryLeased: null,
            beforeDestinationPreconditionCheck: null,
            afterCommitVerified: path => File.WriteAllText(path, "rewritten after commit")),
        new ComplianceEvaluator(),
        destinationRoot);
    var dependencyProvider = new GatedProvider { WaitForRelease = false };
    var store = CreateStore();
    var service = CreateService(
        provider,
        store,
        profile: profile,
        additionalProviders: [dependencyProvider]);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var result = run.ResourceResults[settings.Id];
    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.Equal(ComplianceStatus.ConfigurationMismatch, result.FinalCompliance);
    Assert.False(result.DetectedBefore!.Exists);
    Assert.True(result.DetectedAfter!.Exists);
    Assert.False(string.Equals(
        settings.Parameters["expectedSha256"],
        result.DetectedAfter.ConfigurationHash,
        StringComparison.OrdinalIgnoreCase));
    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    var persistedResult = persisted!.ResourceResults[settings.Id];
    Assert.Equal(ComplianceStatus.ConfigurationMismatch, persistedResult.FinalCompliance);
    Assert.Equal(result.DetectedAfter.ConfigurationHash, persistedResult.DetectedAfter!.ConfigurationHash);
    Assert.Equal(WdemErrorCode.ConfigurationError, persistedResult.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_PersistsEveryRedactedEventBeforePublishingIt()
  {
    var redactor = new LogRedactor();
    var store = CreateStore(redactor: redactor);
    var events = new List<RunEvent>();
    var sink = new RunEventHub();
    var provider = new GatedProvider
    {
      WaitForRelease = false,
      ProgressEvents =
      [
        new ProviderProgress("install", 0.5, "provider-log hunter2", "install")
      ],
      StepResults =
      [
        new ProviderStepResult
        {
          StepId = "install",
          Action = PlanAction.Install,
          Progress = 1,
          Message = "step-complete hunter2"
        }
      ],
      Diagnostics =
      [
        new StructuredError(
            WdemErrorCode.ProviderError,
            "provider diagnostic hunter2",
            "provider detail hunter2")
        {
          UnderlyingException = new InvalidOperationException("hunter2")
        }
      ]
    };
    var profile = Profile() with
    {
      Resources = Profile().Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Value with
          {
            Parameters = new Dictionary<string, string?>
            {
              ["access_token"] = "hunter2"
            }
          },
          StringComparer.OrdinalIgnoreCase)
    };
    var service = CreateService(
        provider,
        store,
        eventSink: sink,
        redactor: redactor,
        profile: profile);
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      var persisted = await store.ReadLogPageAsync(
          runEvent.RunId,
          runEvent.Sequence - 1,
          1,
          cancellationToken);
      var entry = Assert.Single(persisted);
      Assert.Equal(runEvent.Sequence, entry.Sequence);
      Assert.Equal(runEvent.Message, entry.Message);
      events.Add(runEvent);
    });

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var log = await store.ReadLogPageAsync(run.RunId, 0, 1000, CancellationToken.None);
    Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
        events.Select(runEvent => runEvent.Sequence));
    Assert.Equal(events.Select(runEvent => runEvent.Sequence), log.Select(entry => entry.Sequence));
    Assert.Equal(events.Select(runEvent => runEvent.Message), log.Select(entry => entry.Message));
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.StepProgress);
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.Log);
    Assert.Equal(RunEventKind.Completed, events[^1].Kind);
    Assert.DoesNotContain(events, runEvent => ContainsSecret(runEvent.Message, runEvent.Error));
    Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(store.LogPath(run.RunId)),
        StringComparison.Ordinal);
    Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(store.SnapshotPath(run.RunId)),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task ApplyAsync_DrainsReportedProgressAfterCallerCancellation()
  {
    using var cancellation = new CancellationTokenSource();
    var redactor = new LogRedactor();
    var store = CreateStore(redactor: redactor);
    var sink = new RunEventHub();
    var events = new List<RunEvent>();
    using var subscription = sink.Subscribe((runEvent, _) =>
    {
      events.Add(runEvent);
      return Task.CompletedTask;
    });
    var provider = new GatedProvider
    {
      WaitForRelease = false,
      Outcome = ApplyOutcome.Cancelled,
      ProgressEvents =
      [
        new ProviderProgress(
            "install",
            0.5,
            "reported-before-cancel",
            "install")
      ],
      AfterProgress = cancellation.Cancel
    };
    var service = CreateService(
        provider,
        store,
        eventSink: sink,
        redactor: redactor);

    var run = await service.ApplyAsync(Request(), cancellation.Token);

    var log = await store.ReadLogPageAsync(run.RunId, 0, 1000, CancellationToken.None);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.StepProgress &&
        runEvent.Message == "reported-before-cancel");
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.Log &&
        runEvent.Message == "reported-before-cancel");
    Assert.Contains(log, entry => entry.Message == "reported-before-cancel");
  }

  [Fact]
  public async Task ApplyAsync_CoalescesBurstProgressAndPreservesTerminalUpdate()
  {
    var persistenceEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releasePersistence = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var allProgressReported = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var progress = Enumerable.Range(0, 100)
        .Select(index => new ProviderProgress(
            "install",
            index / 200d,
            $"flood-{index}",
            "install"))
        .Append(new ProviderProgress("install", 1, "flood-terminal", "install"))
        .Append(new ProviderProgress("install", 0.5, "flood-regression", "install"))
        .ToArray();
    var provider = new GatedProvider
    {
      WaitForRelease = false,
      ProgressEvents = progress,
      AfterProgress = allProgressReported.SetResult
    };
    var store = CreateStore();
    var sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      if (runEvent.Kind == RunEventKind.StepProgress &&
          runEvent.Message.StartsWith("flood-", StringComparison.Ordinal) &&
          !persistenceEntered.Task.IsCompleted)
      {
        persistenceEntered.SetResult();
        await releasePersistence.Task.WaitAsync(cancellationToken);
      }
    });
    var service = CreateService(provider, store, eventSink: sink);
    var execution = service.ApplyAsync(Request(), CancellationToken.None);

    await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await allProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
    releasePersistence.SetResult();
    var run = await execution.WaitAsync(TimeSpan.FromSeconds(15));

    var log = await store.ReadLogPageAsync(run.RunId, 0, 1000, CancellationToken.None);
    var persistedProgress = log
        .Where(entry => entry.Kind == RunEventKind.StepProgress &&
            entry.Message.StartsWith("flood-", StringComparison.Ordinal))
        .ToArray();
    Assert.InRange(persistedProgress.Length, 2, 34);
    Assert.Equal("flood-terminal", persistedProgress[^1].Message);
    Assert.Equal(1, persistedProgress[^1].Progress);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
  }

  [Fact]
  public async Task RecoverAsync_ConcurrentJsonStoresOnlyOneExecutesReplacement()
  {
    var provider = new GatedProvider();
    var firstStore = CreateStore();
    var secondStore = CreateStore();
    var firstService = CreateService(provider, firstStore);
    var secondService = CreateService(provider, secondStore);
    var prior = await ApprovedInterruptedRunAsync();
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var attempts = new[]
    {
      AttemptRecoveryAsync(firstService, prior.RunId, start.Task),
      AttemptRecoveryAsync(secondService, prior.RunId, start.Task)
    };
    start.SetResult();
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAny(
        provider.SecondApplyEntered.Task,
        Task.WhenAny(attempts)).WaitAsync(TimeSpan.FromSeconds(5));
    provider.ReleaseApply.TrySetResult();
    var completed = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));

    var runs = await CreateStore().ListAsync(CancellationToken.None);
    var persistedPrior = Assert.Single(runs, run => run.RunId == prior.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Single(completed, attempt => attempt.Run is not null);
    Assert.Single(completed, attempt => attempt.Error is InvalidOperationException);
    Assert.Single(runs, run => run.RetriedFromRunId == prior.RunId);
    Assert.Equal(ExecutionState.Completed, persistedPrior.State);
  }

  [Fact]
  public async Task ApplyAsync_HoldsRecoveryOperationUntilTerminalState()
  {
    var provider = new GatedProvider();
    var ownerService = CreateService(provider, CreateStore());
    var competitor = CreateService(provider, CreateStore());
    var ownerTask = ownerService.ApplyAsync(Request(), CancellationToken.None);
    try
    {
      await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      var visible = Assert.Single(
          await CreateStore().ListIncompleteAsync(CancellationToken.None));
      var candidates = await competitor.FindRecoveryCandidatesAsync(CancellationToken.None);
      var recoveryTask = AttemptRecoveryAsync(
          competitor,
          visible.RunId,
          Task.CompletedTask);
      await Task.WhenAny(recoveryTask, provider.SecondApplyEntered.Task)
          .WaitAsync(TimeSpan.FromSeconds(5));
      var abandonError = await AttemptAbandonAsync(competitor, visible.RunId);

      provider.ReleaseApply.TrySetResult();
      ExecutionRun? ownerRun = null;
      Exception? ownerError = null;
      try
      {
        ownerRun = await ownerTask.WaitAsync(TimeSpan.FromSeconds(5));
      }
      catch (Exception exception)
      {
        ownerError = exception;
      }

      var recovery = await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
      var afterCandidates = await competitor.FindRecoveryCandidatesAsync(
          CancellationToken.None);
      var afterRecovery = await AttemptRecoveryAsync(
          competitor,
          visible.RunId,
          Task.CompletedTask);

      Assert.Empty(candidates);
      Assert.IsType<InvalidOperationException>(recovery.Error);
      Assert.IsType<InvalidOperationException>(abandonError);
      Assert.Null(ownerError);
      Assert.Equal(ExecutionOutcome.Succeeded, ownerRun!.Outcome);
      Assert.Equal(1, provider.ApplyCalls);
      Assert.DoesNotContain(
          afterCandidates,
          candidate => candidate.RunId == visible.RunId);
      Assert.IsType<InvalidOperationException>(afterRecovery.Error);
    }
    finally
    {
      provider.ReleaseApply.TrySetResult();
      try
      {
        await ownerTask.WaitAsync(TimeSpan.FromSeconds(5));
      }
      catch
      {
        // The assertions above report the owner failure after all provider calls are released.
      }
    }
  }

  [Fact]
  public async Task AbandonAsync_CannotTerminatePriorClaimedByRecovery()
  {
    var provider = new GatedProvider();
    var firstStore = CreateStore();
    var secondStore = CreateStore();
    var recoveryService = CreateService(provider, firstStore);
    var abandonService = CreateService(provider, secondStore);
    var prior = await ApprovedInterruptedRunAsync();
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var recovery = recoveryService.RecoverAsync(prior.RunId, CancellationToken.None);
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Exception? abandonError = null;
    try
    {
      await abandonService.AbandonAsync(prior.RunId, CancellationToken.None);
    }
    catch (Exception exception)
    {
      abandonError = exception;
    }

    var whileApplying = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    provider.ReleaseApply.TrySetResult();
    await recovery.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<InvalidOperationException>(abandonError);
    Assert.Equal(ExecutionState.Running, whileApplying!.State);
    Assert.NotNull(whileApplying.RecoveryClaimId);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task RecoverAsync_DoesNotApplyAfterConcurrentAbandonWins()
  {
    var provider = new GatedProvider();
    var recoveryStore = new GatedRecoveryOperationStore(CreateStore());
    var recoveryService = CreateService(provider, recoveryStore);
    var abandonService = CreateService(provider, CreateStore());
    var prior = await ApprovedInterruptedRunAsync();
    await CreateStore().CreateAsync(prior, CancellationToken.None);
    var recovery = AttemptRecoveryAsync(
        recoveryService,
        prior.RunId,
        Task.CompletedTask);
    await recoveryStore.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await abandonService.AbandonAsync(prior.RunId, CancellationToken.None);
    recoveryStore.ReleaseAcquire.TrySetResult();
    var attempt = await recovery.WaitAsync(TimeSpan.FromSeconds(5));

    var persisted = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    Assert.IsType<InvalidOperationException>(attempt.Error);
    Assert.Null(attempt.Run);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed, ExecutionOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled, ExecutionOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulReplacementReleasesClaimAcrossJsonStores(
      ApplyOutcome applyOutcome,
      ExecutionOutcome executionOutcome)
  {
    var provider = new GatedProvider
    {
      Outcome = applyOutcome,
      WaitForRelease = false
    };
    var firstStore = CreateStore();
    var firstService = CreateService(provider, firstStore);
    var prior = await ApprovedInterruptedRunAsync();
    await firstStore.CreateAsync(prior, CancellationToken.None);

    var recovered = await firstService.RecoverAsync(prior.RunId, CancellationToken.None);

    var secondStore = CreateStore();
    var secondService = CreateService(provider, secondStore);
    var persisted = await secondStore.GetAsync(prior.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(executionOutcome, recovered.Outcome);
    Assert.Equal(ExecutionState.Running, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
    Assert.Contains(candidates, candidate => candidate.RunId == prior.RunId);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_HidesActiveRecoveryOperation()
  {
    var store = CreateStore();
    var service = CreateService(new GatedProvider(), store);
    var claimed = InterruptedRun() with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    await using var operation = await store.TryAcquireRecoveryOperationAsync(
        claimed.RunId,
        CancellationToken.None);
    Assert.NotNull(operation);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    Assert.DoesNotContain(candidates, candidate => candidate.RunId == claimed.RunId);
  }

  [Theory]
  [InlineData(CompetingOperation.Recover)]
  [InlineData(CompetingOperation.Abandon)]
  public async Task FindRecoveryCandidatesAsync_RechecksAfterConcurrentCompletion(
      CompetingOperation competingOperation)
  {
    var provider = new GatedProvider { WaitForRelease = false };
    var backingStore = CreateStore();
    var gatedStore = new GatedRecoveryOperationStore(CreateStore());
    var finder = CreateService(provider, gatedStore);
    var competitor = CreateService(provider, CreateStore());
    var prior = await ApprovedInterruptedRunAsync();
    await backingStore.CreateAsync(prior, CancellationToken.None);
    var finding = finder.FindRecoveryCandidatesAsync(CancellationToken.None);
    await gatedStore.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    if (competingOperation == CompetingOperation.Recover)
    {
      await competitor.RecoverAsync(prior.RunId, CancellationToken.None);
    }
    else
    {
      await competitor.AbandonAsync(prior.RunId, CancellationToken.None);
    }

    gatedStore.ReleaseAcquire.TrySetResult();
    var candidates = await finding.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.DoesNotContain(candidates, candidate => candidate.RunId == prior.RunId);
  }

  [Fact]
  public async Task RecoverAsync_ReclaimsOrphanedClaimAcrossJsonStores()
  {
    var provider = new GatedProvider { WaitForRelease = false };
    var firstStore = CreateStore();
    var claimed = (await ApprovedInterruptedRunAsync()) with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await firstStore.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(provider, CreateStore());
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    var persisted = await CreateStore().GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Contains(candidates, candidate => candidate.RunId == claimed.RunId);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.True(persisted.Revision > claimed.Revision);
  }

  [Theory]
  [MemberData(nameof(FutureClaimReadTimes))]
  public async Task RecoverAsync_ReclaimsPersistedFutureClaimAfterClockRollback(
      DateTimeOffset claimedAt,
      DateTimeOffset readAt)
  {
    var writeClock = new MutableTimeProvider(claimedAt);
    var readClock = new MutableTimeProvider(readAt);
    var provider = new GatedProvider { WaitForRelease = false };
    var store = CreateStore(writeClock);
    var claimed = (await ApprovedInterruptedRunAsync(writeClock)) with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = claimedAt
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var reader = CreateStore(readClock);
    var service = CreateService(provider, reader, readClock);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    var persisted = await reader.GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Contains(candidates, candidate => candidate.RunId == claimed.RunId);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
    Assert.True(File.Exists(reader.SnapshotPath(claimed.RunId)));
    Assert.Empty(Directory.GetFiles(
        Path.GetDirectoryName(reader.SnapshotPath(claimed.RunId))!,
        $"{claimed.RunId:D}.json.corrupted.*"));
  }

  [Fact]
  public async Task AbandonAsync_ClearsOrphanedRecoveryClaim()
  {
    var store = CreateStore();
    var claimed = InterruptedRun() with
    {
      Revision = 4,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(new GatedProvider(), CreateStore());

    await service.AbandonAsync(claimed.RunId, CancellationToken.None);

    var persisted = await CreateStore().GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
  }

  [Fact]
  public async Task RecoveryClaim_BecomesImmediatelyRecoverableWhenOperationLeaseIsDisposed()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider { WaitForRelease = false };
    var store = CreateStore(clock);
    var claimed = (await ApprovedInterruptedRunAsync(clock)) with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = clock.GetUtcNow()
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(provider, CreateStore(clock), clock);
    var operation = await store.TryAcquireRecoveryOperationAsync(
        claimed.RunId,
        CancellationToken.None);
    Assert.NotNull(operation);

    Assert.Empty(await service.FindRecoveryCandidatesAsync(CancellationToken.None));
    await operation!.DisposeAsync();
    Assert.Contains(
        await service.FindRecoveryCandidatesAsync(CancellationToken.None),
        candidate => candidate.RunId == claimed.RunId);
    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ActiveRecoveryOperationCannotBeStolenAfterClaimTimestampAges()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider();
    var firstStore = CreateStore(clock);
    var prior = await ApprovedInterruptedRunAsync(clock);
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var firstService = CreateService(provider, firstStore, clock);
    var secondService = CreateService(provider, CreateStore(clock), clock);
    var firstRecovery = AttemptRecoveryAsync(
        firstService,
        prior.RunId,
        Task.CompletedTask);
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    clock.Advance(TimeSpan.FromMinutes(6));
    try
    {
      Assert.DoesNotContain(
          await secondService.FindRecoveryCandidatesAsync(CancellationToken.None),
          candidate => candidate.RunId == prior.RunId);
      var secondRecovery = AttemptRecoveryAsync(
          secondService,
          prior.RunId,
          Task.CompletedTask);
      var abandon = AttemptAbandonAsync(secondService, prior.RunId);
      await Task.WhenAll((Task)secondRecovery, abandon)
          .WaitAsync(TimeSpan.FromSeconds(2));
      Assert.IsType<InvalidOperationException>((await secondRecovery).Error);
      Assert.IsType<InvalidOperationException>(await abandon);
      Assert.Equal(1, provider.ApplyCalls);
      var persisted = await CreateStore(clock).GetAsync(prior.RunId, CancellationToken.None);
      Assert.Equal(ExecutionState.Running, persisted!.State);
    }
    finally
    {
      provider.ReleaseApply.TrySetResult();
    }

    var owner = await firstRecovery.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Null(owner.Error);
    Assert.Equal(ExecutionOutcome.Succeeded, owner.Run!.Outcome);
  }

  [Fact]
  public async Task RecoverAsync_ImmediatelyReconcilesAfterClaimFinalizationFailure()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider { WaitForRelease = false };
    var backingStore = CreateStore(clock);
    var prior = await ApprovedInterruptedRunAsync(clock);
    await backingStore.CreateAsync(prior, CancellationToken.None);
    var faultingStore = new ThrowOnTrySaveCallStore(backingStore, throwOnCall: 2);
    var firstService = CreateService(provider, faultingStore, clock);

    await Assert.ThrowsAsync<IOException>(() =>
        firstService.RecoverAsync(prior.RunId, CancellationToken.None));

    var runsAfterFailure = await CreateStore(clock).ListAsync(CancellationToken.None);
    var replacement = Assert.Single(
        runsAfterFailure,
        run => run.RetriedFromRunId == prior.RunId &&
            run.Outcome == ExecutionOutcome.Succeeded);
    var claimedPrior = Assert.Single(runsAfterFailure, run => run.RunId == prior.RunId);
    Assert.NotNull(claimedPrior.RecoveryClaimId);
    var secondService = CreateService(provider, CreateStore(clock), clock);
    Assert.Contains(
        await secondService.FindRecoveryCandidatesAsync(CancellationToken.None),
        candidate => candidate.RunId == prior.RunId);
    var recovered = await secondService.RecoverAsync(prior.RunId, CancellationToken.None);

    var persistedPrior = await CreateStore(clock).GetAsync(prior.RunId, CancellationToken.None);
    Assert.Equal(replacement.RunId, recovered.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
    Assert.Null(persistedPrior.RecoveryClaimId);
  }

  [Fact]
  public async Task RecoverAsync_PreservesRecoveryFailureWhenClaimReleaseAlsoFails()
  {
    var provider = new GatedProvider { WaitForRelease = false };
    var backingStore = CreateStore();
    var prior = await ApprovedInterruptedRunAsync();
    await backingStore.CreateAsync(prior, CancellationToken.None);
    var faultingStore = new ThrowOnTrySaveCallStore(
        backingStore,
        throwOnCall: 2,
        listException: new InvalidDataException("Injected recovery lookup failure."));
    var service = CreateService(provider, faultingStore);

    var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
        service.RecoverAsync(prior.RunId, CancellationToken.None));

    Assert.IsType<IOException>(error.Data["RecoveryClaimReleaseException"]);
    Assert.Equal(0, provider.ApplyCalls);
    var persistedPrior = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    Assert.NotNull(persistedPrior!.RecoveryClaimId);
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private JsonExecutionRunStore CreateStore(
      TimeProvider? timeProvider = null,
      LogRedactor? redactor = null) => new(
      new WdemDataPaths(_directory),
      redactor ?? new LogRedactor(),
      timeProvider);

  private static EnvironmentRunService CreateService(
      IResourceProvider provider,
      IExecutionRunStore store,
      TimeProvider? timeProvider = null,
      IRunEventSink? eventSink = null,
      LogRedactor? redactor = null,
      DeveloperProfile? profile = null,
      IReadOnlyList<IResourceProvider>? additionalProviders = null)
  {
    var registry = new ResourceProviderRegistry(
        [provider, .. additionalProviders ?? []]);
    var compliance = new ComplianceEvaluator();
    eventSink ??= new RunEventHub();
    redactor ??= new LogRedactor();
    return new EnvironmentRunService(
        new FixedProfileCatalog(profile ?? Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider ?? TimeProvider.System,
        eventSink,
        redactor);
  }

  private static bool ContainsSecret(string message, StructuredError? error) =>
      message.Contains("hunter2", StringComparison.Ordinal)
      || error?.Summary.Contains("hunter2", StringComparison.Ordinal) == true
      || error?.Detail.Contains("hunter2", StringComparison.Ordinal) == true
      || error?.UnderlyingExceptionMessage?.Contains("hunter2", StringComparison.Ordinal) == true;

  private static async Task<RecoveryAttempt> AttemptRecoveryAsync(
      IEnvironmentRunService service,
      Guid runId,
      Task start)
  {
    await start;
    try
    {
      return new RecoveryAttempt(
          await service.RecoverAsync(runId, CancellationToken.None),
          null);
    }
    catch (Exception exception)
    {
      return new RecoveryAttempt(null, exception);
    }
  }

  private static async Task<Exception?> AttemptAbandonAsync(
      IEnvironmentRunService service,
      Guid runId)
  {
    try
    {
      await service.AbandonAsync(runId, CancellationToken.None);
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private static DeveloperProfile Profile() => new()
  {
    Id = "developer",
    Version = "1.0.0",
    DisplayName = "Developer",
    Description = "Developer workstation",
    RequiredResources = [new ProfileResourceReference { Id = "git" }],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        Id = "git",
        Type = "package",
        Provider = "fake",
        VersionConstraint = ">=2.50.0"
      }
    }
  };

  private static RunRequest Request() => new(
      "input/profile.yaml",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private async Task<ExecutionRun> ApprovedInterruptedRunAsync(
      TimeProvider? timeProvider = null)
  {
    var seedProvider = new GatedProvider { WaitForRelease = false };
    var seedDirectory = Path.Combine(_directory, $"approval-seed-{Guid.NewGuid():N}");
    var seedStore = new JsonExecutionRunStore(
        new WdemDataPaths(seedDirectory),
        new LogRedactor(),
        timeProvider);
    var approved = await CreateService(
        seedProvider,
        seedStore,
        timeProvider).ApplyAsync(Request(), CancellationToken.None);
    var plan = Assert.IsType<ExecutionPlan>(approved.Plan);
    var approval = Assert.IsType<PlanApproval>(approved.PlanApproval);
    Assert.Equal(plan.Fingerprint, approval.InitialPlanFingerprint);
    return InterruptedRun() with
    {
      Plan = plan,
      PlanApproval = approval
    };
  }

  private static ExecutionRun InterruptedRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = Path.GetFullPath("input/profile.yaml"),
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
    State = ExecutionState.Running,
    Machine = new MachineInformation("test", "x64", "test", "test"),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        ResourceId = "git",
        State = ExecutionState.Running,
        DetectedBefore = Missing()
      }
    }
  };

  private static DetectedState Missing() => new()
  {
    ResourceId = "git",
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private sealed record RecoveryAttempt(ExecutionRun? Run, Exception? Error);

  private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
  }

  public enum CompetingOperation
  {
    Recover,
    Abandon
  }

  private sealed class GatedRecoveryOperationStore(IExecutionRunStore inner) :
      ForwardingRunStore(inner)
  {
    public TaskCompletionSource AcquireEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAcquire { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
      AcquireEntered.TrySetResult();
      await ReleaseAcquire.Task.WaitAsync(cancellationToken);
      return await Inner.TryAcquireRecoveryOperationAsync(runId, cancellationToken);
    }
  }

  private sealed class ThrowOnTrySaveCallStore(
      IExecutionRunStore inner,
      int throwOnCall,
      Exception? listException = null) : ForwardingRunStore(inner)
  {
    private int _trySaveCalls;
    private Exception? _listException = listException;

    public override Task<IReadOnlyList<ExecutionRun>> ListAsync(
        CancellationToken cancellationToken)
    {
      var exception = Interlocked.Exchange(ref _listException, null);
      if (exception is not null)
      {
        throw exception;
      }

      return Inner.ListAsync(cancellationToken);
    }

    public override Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken)
    {
      if (Interlocked.Increment(ref _trySaveCalls) == throwOnCall)
      {
        throw new IOException("Injected recovery claim persistence failure.");
      }

      return Inner.TrySaveAsync(
          run,
          expectedRevision,
          expectedRecoveryClaimId,
          cancellationToken);
    }
  }

  private abstract class ForwardingRunStore(IExecutionRunStore inner) : IExecutionRunStore
  {
    protected IExecutionRunStore Inner { get; } = inner;
    public IReadOnlyList<StructuredError> Diagnostics => Inner.Diagnostics;

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken) =>
        Inner.CreateAsync(run, cancellationToken);

    public Task CreateAsync(
        ExecutionRun run,
        IReadOnlyList<ApprovedResourceSeal> approvedResources,
        CancellationToken cancellationToken) =>
        Inner.CreateAsync(run, approvedResources, cancellationToken);

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Inner.GetAsync(runId, cancellationToken);

    public virtual Task<IReadOnlyList<ExecutionRun>> ListAsync(
        CancellationToken cancellationToken) =>
        Inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken) =>
        Inner.ListIncompleteAsync(cancellationToken);

    public virtual Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        Inner.TryAcquireRecoveryOperationAsync(runId, cancellationToken);

    public Task<ExecutionRun> SaveAsync(
        ExecutionRun run,
        CancellationToken cancellationToken) =>
        Inner.SaveAsync(run, cancellationToken);

    public virtual Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken) =>
        Inner.TrySaveAsync(
            run,
            expectedRevision,
            expectedRecoveryClaimId,
            cancellationToken);

    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) =>
        Inner.AppendLogAsync(runId, entry, cancellationToken);

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Inner.ReadLogPageAsync(runId, afterSequence, take, cancellationToken);
  }

  private sealed class FixedProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(id, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(new ProfileLoadResult
      {
        Profile = profile,
        SourcePath = Path.GetFullPath(path)
      });
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([]);
  }

  private sealed class GatedProvider : IResourceProvider
  {
    private int _applyCalls;

    public string ResourceType => "package";
    public string ProviderName => "fake";
    public ProviderCapabilities Capabilities { get; } = new()
    {
      MaxConcurrentOperations = 2
    };
    public int ApplyCalls => Volatile.Read(ref _applyCalls);
    public ApplyOutcome Outcome { get; init; } = ApplyOutcome.Succeeded;
    public bool WaitForRelease { get; init; } = true;
    public IReadOnlyList<ProviderProgress> ProgressEvents { get; init; } = [];
    public IReadOnlyList<ProviderStepResult> StepResults { get; init; } = [];
    public IReadOnlyList<StructuredError> Diagnostics { get; init; } = [];
    public Action? AfterProgress { get; init; }
    public TaskCompletionSource FirstApplyEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondApplyEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseApply { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Missing() with { ResourceId = resource.Id });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ResourcePlan
        {
          ResourceId = resource.Id,
          ResourceType = resource.Type,
          ProviderName = resource.Provider,
          DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
          Compliance = ComplianceStatus.Missing,
          IsExecutable = true,
          Steps =
          [
            new PlanStep
            {
              Id = "install",
              Description = "Install git",
              Action = PlanAction.Install,
              PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
              RestartPolicy = RestartPolicy.NoRestart
            }
          ]
        });

    public async ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      var call = Interlocked.Increment(ref _applyCalls);
      (call == 1 ? FirstApplyEntered : SecondApplyEntered).TrySetResult();
      if (WaitForRelease)
      {
        await ReleaseApply.Task.WaitAsync(cancellationToken);
      }

      foreach (var providerProgress in ProgressEvents)
      {
        progress?.Report(providerProgress);
      }
      AfterProgress?.Invoke();

      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = Outcome,
        StepResults = StepResults,
        Diagnostics = Diagnostics,
        Error = Outcome == ApplyOutcome.Failed
            ? new StructuredError(
                WdemErrorCode.ProviderError,
                "Provider failed.",
                "apply failed")
            {
              ResourceId = resource.Id,
              IsRetryable = true
            }
            : null
      };
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true,
            Version = "2.52.1",
            InstalledVersions = [new SemanticVersion(2, 52, 1)]
          }
        });
  }
}
