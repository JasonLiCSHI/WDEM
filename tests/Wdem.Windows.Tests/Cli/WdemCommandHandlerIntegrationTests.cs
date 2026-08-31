using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Cli;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Cli;

public sealed class WdemCommandHandlerIntegrationTests : IDisposable
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-cli-integration-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_RealDetectionFailureReturnsExecutionExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    Assert.Equal(3, exitCode);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task RetryAsync_RedactedDescriptionPreservesCanonicalApprovalAfterStoreReload()
  {
    const string secret = "description-approval-secret";
    var provider = new RetryFailureProvider();
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with
        {
          Provider = provider.ProviderName,
          DisplayName = "Git",
          Description = $"Source control configured for {secret}"
        }
      }
    };
    var redactor = new LogRedactor([secret]);
    var firstStore = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var firstService = CreateService(firstStore, redactor);

    var failed = await firstService.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.True(failed.Revision > 0);
    Assert.Equal(failed.Plan!.Fingerprint, failed.PlanApproval!.InitialPlanFingerprint);
    Assert.DoesNotContain(
        secret,
        await File.ReadAllTextAsync(firstStore.SnapshotPath(failed.RunId)),
        StringComparison.Ordinal);

    var restartRedactor = new LogRedactor();
    var reloadedStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        restartRedactor);
    var retryService = CreateService(reloadedStore, restartRedactor);
    var retried = await retryService.RetryAsync(
        failed.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(failed.RunId, retried.RetriedFromRunId);
    Assert.Equal(2, provider.ApplyCalls);

    EnvironmentRunService CreateService(
        JsonExecutionRunStore store,
        LogRedactor serviceRedactor)
    {
      var registry = new ResourceProviderRegistry([provider]);
      var compliance = new ComplianceEvaluator();
      return new EnvironmentRunService(
          new FixedProfileCatalog(profile),
          new ResourceGraphBuilder(),
          registry,
          compliance,
          new ExecutionPlanner(registry, compliance),
          new ResourceScheduler(),
          store,
          new DirectResourceApplyDispatcher(),
          timeProvider: null,
          new RunEventHub(),
          serviceRedactor);
    }
  }

  [Fact]
  public async Task InspectAsync_RealDetectionFailureReturnsExecutionExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.InspectAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    var log = await store.ReadLogPageAsync(run.RunId, 0, 1000, CancellationToken.None);
    var completedEntry = Assert.Single(log, entry => entry.Kind == RunEventKind.Completed);
    var completedEvent = Assert.Single(
        output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<RunEvent>(line, JsonOptions)!),
        runEvent => runEvent.Kind == RunEventKind.Completed);
    Assert.Equal(3, exitCode);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(ExecutionOutcome.Skipped, run.ResourceResults["git"].Outcome);
    Assert.Equal(
        WdemErrorCode.DetectionError,
        Assert.Single(run.Plan!.Errors).Code);
    Assert.Equal("Failed", completedEntry.Message);
    Assert.Equal(ProviderLogLevel.Error, completedEntry.Level);
    Assert.Equal("Failed", completedEvent.Message);
    Assert.Equal(1, completedEvent.Progress);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task InspectAsync_RealDependencyValidationFailureReturnsProfileExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Dependencies = ["missing"] }
      }
    };
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.InspectAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(2, exitCode);
    Assert.Equal(WdemErrorCode.DependencyError, Assert.Single(run.Plan!.Errors).Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_InvalidMaterializedProfileRedactsDiagnosticsBeforePersistenceAndOutput()
  {
    const string secret = "invalid-profile-hunter2";
    var profile = Profile() with
    {
      Resources = Profile().Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Value with
          {
            Parameters = new Dictionary<string, string?>
            {
              ["access_token"] = secret
            }
          },
          StringComparer.OrdinalIgnoreCase)
    };
    var diagnostic = new StructuredError(
        WdemErrorCode.ProviderError,
        $"Invalid provider value {secret}",
        $"Provider detail {secret}")
    {
      UnderlyingException = new InvalidOperationException(secret)
    };
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var registry = new ResourceProviderRegistry([new DetectionFailureProvider()]);
    var compliance = new ComplianceEvaluator();
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(new ProfileLoadResult
        {
          Profile = profile,
          SourcePath = Path.GetFullPath("developer.yaml"),
          Errors = [diagnostic]
        }),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(3, exitCode);
    Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.LogPath(run.RunId)),
        StringComparison.Ordinal);
    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.SnapshotPath(run.RunId)),
        StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ResumeAsync_ExistingSuccessfulReplacementReplaysRedactedRunEvents(bool json)
  {
    const string secret = "resume-replay-secret";
    var provider = new SuccessfulProvider(secret);
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with
        {
          Provider = provider.ProviderName,
          Parameters = new Dictionary<string, string?> { ["access_token"] = secret }
        }
      }
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var prior = InterruptedRun();
    var replacement = await service.ApplyAsync(
        new RunRequest(
            prior.ProfileSourcePath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);
    prior = WithApproval(prior, replacement);
    await store.CreateAsync(prior, CancellationToken.None);
    replacement = await store.SaveAsync(
        replacement with
        {
          RetriedFromRunId = prior.RunId,
          RecoveredFromRunId = prior.RunId
        },
        CancellationToken.None);
    var historyBefore = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ResumeAsync(prior.RunId, json, CancellationToken.None);

    var secondOutput = new StringWriter();
    var secondHandler = new WdemCommandHandler(
        service,
        store,
        secondOutput,
        new StringWriter(),
        redactor,
        sink);
    var secondExitCode = await secondHandler.ResumeAsync(
        prior.RunId,
        json,
        CancellationToken.None);

    var historyAfter = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var lines = output.ToString().Split(
        Environment.NewLine,
        StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(0, exitCode);
    Assert.Equal(0, secondExitCode);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(historyBefore, historyAfter);
    Assert.Equal(historyBefore.Count, lines.Length);
    Assert.Equal(output.ToString(), secondOutput.ToString());
    Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    if (json)
    {
      var events = lines.Select(line =>
          JsonSerializer.Deserialize<RunEvent>(line, JsonOptions)!).ToArray();
      Assert.All(events, runEvent => Assert.Equal(replacement.RunId, runEvent.RunId));
      Assert.Equal(historyBefore.Select(entry => entry.Sequence),
          events.Select(runEvent => runEvent.Sequence));
      Assert.Equal(RunEventKind.RunStateChanged, events[0].Kind);
      Assert.Contains(events, runEvent =>
          runEvent.Kind == RunEventKind.StepProgress && runEvent.Progress == 0.5);
      Assert.Equal(RunEventKind.Completed, events[^1].Kind);
    }
    else
    {
      Assert.All(lines, line => Assert.Contains(
          replacement.RunId.ToString("D"),
          line,
          StringComparison.Ordinal));
      Assert.Contains(nameof(RunEventKind.RunStateChanged), lines[0], StringComparison.Ordinal);
      Assert.Contains(lines, line =>
          line.Contains(nameof(RunEventKind.StepProgress), StringComparison.Ordinal));
      Assert.Contains(nameof(RunEventKind.Completed), lines[^1], StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task ResumeAsync_DoesNotWriteUnrelatedEventFromSameOperationScope()
  {
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var run = InterruptedRun();
    await store.CreateAsync(run, CancellationToken.None);
    await store.AppendLogAsync(
        run.RunId,
        new RunLogEntry(
            1,
            DateTimeOffset.UtcNow,
            ProviderLogLevel.Info,
            "git",
            null,
            "persisted resume event",
            Kind: RunEventKind.RunStateChanged),
        CancellationToken.None);
    var unrelatedRunId = Guid.NewGuid();
    var service = new UnrelatedPublishingRecoveryService(run, unrelatedRunId, sink);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    await handler.ResumeAsync(run.RunId, json: true, CancellationToken.None);

    var events = output.ToString()
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<RunEvent>(line, JsonOptions)!)
        .ToArray();
    Assert.DoesNotContain(events, runEvent => runEvent.RunId == unrelatedRunId);
    Assert.Contains(events, runEvent =>
        runEvent.RunId == run.RunId && runEvent.Message == "persisted resume event");
  }

  [Fact]
  public async Task ResumeAsync_OutputFailureAfterSuccessfulRecoveryCanBeRetriedWithoutApplyingAgain()
  {
    const string secret = "resume-output-failure-secret";
    var provider = new SuccessfulProvider(secret);
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with
        {
          Provider = provider.ProviderName
        }
      }
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor([secret]);
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var approvedTemplate = await service.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);
    provider.ResetApplyCalls();
    var prior = WithApproval(InterruptedRun(), approvedTemplate);
    await store.CreateAsync(prior, CancellationToken.None);
    var failingHandler = new WdemCommandHandler(
        service,
        store,
        new ThrowOnCompletedEventTextWriter(),
        new StringWriter(),
        redactor,
        sink);

    var failedExitCode = await failingHandler.ResumeAsync(
        prior.RunId,
        json: true,
        CancellationToken.None);

    var replacement = Assert.Single(
        await store.ListAsync(CancellationToken.None),
        run => run.RetriedFromRunId == prior.RunId);
    var historyBeforeRetry = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var retryOutput = new StringWriter();
    var retryHandler = new WdemCommandHandler(
        service,
        store,
        retryOutput,
        new StringWriter(),
        redactor,
        sink);

    var retryExitCode = await retryHandler.ResumeAsync(
        prior.RunId,
        json: true,
        CancellationToken.None);

    var historyAfterRetry = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var retriedPrior = await store.GetAsync(prior.RunId, CancellationToken.None);
    Assert.Equal(1, failedExitCode);
    Assert.Equal(0, retryExitCode);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, replacement.State);
    Assert.Equal(ExecutionOutcome.Succeeded, replacement.Outcome);
    Assert.Equal(ExecutionState.Completed, retriedPrior!.State);
    Assert.Equal(historyBeforeRetry, historyAfterRetry);
    Assert.DoesNotContain(secret, retryOutput.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task ApplyAsync_InternalProgressDrainTimeoutIsHostFailureWithDurableEvidence()
  {
    var provider = new SuccessfulProvider("progress-timeout-secret");
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Provider = provider.ProviderName }
      }
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    using var blockedProgress = sink.SubscribeRequired((runEvent, cancellationToken) =>
        runEvent.Kind == RunEventKind.StepProgress
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.CompletedTask);
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor,
        TimeSpan.FromMilliseconds(500));
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(1, exitCode);
    Assert.Equal(ExecutionState.Running, run.State);
    Assert.Null(run.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.NotEqual(WdemErrorCode.CancellationError, run.ResourceResults["git"].Error?.Code);
    Assert.Equal(
        "Applied resource evidence could not be fully published.",
        run.ResourceResults["git"].Error?.Summary);
  }

  [Theory]
  [InlineData("progress", RunEventKind.StepProgress)]
  [InlineData("diagnostic", RunEventKind.Log)]
  [InlineData("step", RunEventKind.StepProgress)]
  public async Task ApplyAsync_RequiredOutputFailureDuringProviderPublicationIsHostFailure(
      string publication,
      RunEventKind failingKind)
  {
    var provider = new PublicationProvider(publication);
    var profile = Profile() with
    {
      RequiredResources =
      [
        new ProfileResourceReference { Id = "git" },
        new ProfileResourceReference { Id = "dependent" }
      ],
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Provider = provider.ProviderName },
        ["dependent"] = Profile().Resources["git"] with
        {
          Id = "dependent",
          Provider = provider.ProviderName,
          Dependencies = ["git"]
        }
      }
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new ThrowOnRunEventKindTextWriter(failingKind),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(1, exitCode);
    Assert.Equal(ExecutionState.Running, run.State);
    Assert.Null(run.Outcome);
    var evidence = run.ResourceResults["git"];
    Assert.Equal(ExecutionState.Completed, evidence.State);
    Assert.Equal(ExecutionOutcome.Failed, evidence.Outcome);
    Assert.Equal(
        "Applied resource evidence could not be fully published.",
        evidence.Error?.Summary);
    Assert.Equal(ExecutionState.Pending, run.ResourceResults["dependent"].State);
  }

  [Fact]
  public async Task ApplyAsync_CancellationPreventsLateProviderProgressAfterCliExit()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new LateProgressAfterCancellationProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(TimeSpan.FromMilliseconds(50)),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);
    var execution = handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        cancellation.Token);
    await provider.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancellation.CancelAsync();

    try
    {
      Assert.Equal(130, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
      var run = Assert.Single(await store.ListAsync(CancellationToken.None));
      var before = await store.ReadLogPageAsync(
          run.RunId,
          0,
          1000,
          CancellationToken.None);
      var outputBefore = output.ToString();
      Assert.Equal(ExecutionState.Completed, run.State);
      Assert.Equal(RunEventKind.Completed, before[^1].Kind);

      provider.ReleaseApply.SetResult();
      await provider.LateProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await Task.Delay(250);

      var after = await store.ReadLogPageAsync(
          run.RunId,
          0,
          1000,
          CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(RunEventKind.Completed, after[^1].Kind);
      Assert.Equal(outputBefore, output.ToString());
    }
    finally
    {
      provider.ReleaseApply.TrySetResult();
    }
  }

  [Fact]
  public async Task ApplyAsync_CancellationDropsAcceptedBacklogBeforeCliExit()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new BackloggedProgressProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var output = new SlowBacklogTextWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);
    var execution = handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        cancellation.Token);
    await provider.BacklogReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await output.BacklogWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancellation.CancelAsync();

    try
    {
      Assert.Equal(130, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
      var run = Assert.Single(await store.ListAsync(CancellationToken.None));
      var before = await store.ReadLogPageAsync(
          run.RunId,
          0,
          1000,
          CancellationToken.None);
      var outputBefore = output.ToString();
      Assert.Equal(ExecutionState.Completed, run.State);
      Assert.Equal(RunEventKind.Completed, before[^1].Kind);

      await Task.Delay(250);

      var after = await store.ReadLogPageAsync(
          run.RunId,
          0,
          1000,
          CancellationToken.None);
      Assert.Equal(before, after);
      Assert.Equal(RunEventKind.Completed, after[^1].Kind);
      Assert.Equal(outputBefore, output.ToString());
    }
    finally
    {
      provider.ReleaseApply.TrySetResult();
    }
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
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
        Provider = "failing"
      }
    }
  };

  private static ExecutionRun InterruptedRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = Path.GetFullPath("developer.yaml"),
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
    State = ExecutionState.Running,
    Machine = new MachineInformation("Windows", "X64", "machine", "user"),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        ResourceId = "git",
        State = ExecutionState.Running,
        DetectedBefore = new DetectedState
        {
          ResourceId = "git",
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        }
      }
    }
  };

  private static ExecutionRun WithApproval(ExecutionRun interrupted, ExecutionRun approved) =>
      interrupted with
      {
        Plan = approved.Plan,
        PlanApproval = approved.PlanApproval
      };

  private sealed class FixedProfileCatalog : IProfileCatalog
  {
    private readonly ProfileLoadResult _result;

    public FixedProfileCatalog(DeveloperProfile profile)
        : this(new ProfileLoadResult
        {
          Profile = profile,
          SourcePath = Path.GetFullPath("developer.yaml")
        })
    {
    }

    public FixedProfileCatalog(ProfileLoadResult result)
    {
      _result = result;
    }

    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(id, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(_result with { SourcePath = Path.GetFullPath(path) });
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([]);
  }

  private sealed class DetectionFailureProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "failing";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Failed,
          Exists = false,
          StructuredError = new StructuredError(
              WdemErrorCode.DetectionError,
              "Detection failed.",
              "The provider could not inspect the resource.")
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
        {
          ResourceId = resource.Id,
          ResourceType = resource.Type,
          ProviderName = resource.Provider,
          DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
          Compliance = ComplianceStatus.DetectionFailed,
          IsExecutable = false
        });

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      throw new InvalidOperationException("A non-executable plan must not be applied.");
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A non-executable plan must not be verified.");
  }

  private sealed class SuccessfulProvider(string secret) : IResourceProvider
  {
    private int _applyCalls;

    public string ResourceType => "package";
    public string ProviderName => "successful";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls => Volatile.Read(ref _applyCalls);

    public void ResetApplyCalls() => Interlocked.Exchange(ref _applyCalls, 0);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
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

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _applyCalls);
      progress?.Report(new ProviderProgress(
          "install",
          0.5,
          $"installing {secret}",
          "install"));
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            Message = $"installed {secret}"
          }
        ]
      });
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }

  private sealed class RetryFailureProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "retry-failure";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
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

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Failed,
        Error = new StructuredError(
            WdemErrorCode.ProviderError,
            "Apply failed.",
            "The retryable test provider failed the apply operation.")
      });
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Missing,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = false
          }
        });
  }

  private sealed class PublicationProvider(string publication) : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "publication";
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
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
              Description = "Install resource",
              Action = PlanAction.Install,
              PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
              RestartPolicy = RestartPolicy.NoRestart
            }
          ]
        });

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      if (publication == "progress")
      {
        progress?.Report(new ProviderProgress("install", 0.5, "installing", "install"));
      }

      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        Diagnostics = publication == "diagnostic"
            ? [new StructuredError(WdemErrorCode.ProviderError, "diagnostic", "detail")]
            : [],
        StepResults = publication == "step"
            ?
            [
              new ProviderStepResult
              {
                StepId = "install",
                Action = PlanAction.Install,
                Progress = 1,
                Message = "installed"
              }
            ]
            : []
      });
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }

  private sealed class LateProgressAfterCancellationProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "failing";
    public ProviderCapabilities Capabilities { get; } = new();
    public TaskCompletionSource ApplyEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseApply { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource LateProgressReported { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
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
              Description = "Install resource",
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
      ApplyEntered.SetResult();
      await ReleaseApply.Task;
      progress?.Report(new ProviderProgress(
          "install",
          0.5,
          "late provider progress",
          "install"));
      LateProgressReported.SetResult();
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded
      };
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }

  private sealed class BackloggedProgressProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "failing";
    public ProviderCapabilities Capabilities { get; } = new();
    public TaskCompletionSource BacklogReported { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseApply { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
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
              Description = "Install resource",
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
      for (var index = 0; index < 100; index++)
      {
        progress?.Report(new ProviderProgress(
            "install",
            index / 200d,
            $"backlog-{index}",
            "install"));
      }

      BacklogReported.SetResult();
      await ReleaseApply.Task;
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded
      };
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }

  private sealed class SlowBacklogTextWriter : StringWriter
  {
    public TaskCompletionSource BacklogWriteEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default)
    {
      if (buffer.Span.Contains("\"message\":\"backlog-", StringComparison.Ordinal))
      {
        BacklogWriteEntered.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
      }

      await base.WriteLineAsync(buffer, cancellationToken);
    }
  }

  private sealed class ThrowOnRunEventKindTextWriter(RunEventKind kind) : StringWriter
  {
    private readonly string _kind = $"\"kind\":\"{JsonNamingPolicy.CamelCase.ConvertName(kind.ToString())}\"";

    public override Task WriteLineAsync(string? value) =>
        value?.Contains(_kind, StringComparison.Ordinal) == true
            ? Task.FromException(new IOException($"{kind} output failed"))
            : base.WriteLineAsync(value);

    public override Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default) =>
        buffer.Span.Contains(_kind, StringComparison.Ordinal)
            ? Task.FromException(new IOException($"{kind} output failed"))
            : base.WriteLineAsync(buffer, cancellationToken);
  }

  private sealed class ThrowOnCompletedEventTextWriter : StringWriter
  {
    public override Task WriteLineAsync(string? value) =>
        value?.Contains("\"kind\":\"completed\"", StringComparison.Ordinal) == true
            ? Task.FromException(new IOException("completed event output failed"))
            : base.WriteLineAsync(value);

    public override Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default) =>
        buffer.Span.Contains("\"kind\":\"completed\"", StringComparison.Ordinal)
            ? Task.FromException(new IOException("completed event output failed"))
            : base.WriteLineAsync(buffer, cancellationToken);
  }

  private sealed class UnrelatedPublishingRecoveryService(
      ExecutionRun run,
      Guid unrelatedRunId,
      IRunEventSink sink) : ICommandLineEnvironmentRunService
  {
    public Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExecutionRun> ApplyFromCommandLineAsync(
        RunRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExecutionRun> ApplyReviewedPlanAsync(
        RunRequest request,
        string reviewedPlanFingerprint,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ExecutionRun> RetryAsync(
        Guid priorRunId,
        IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public async Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken)
    {
      await sink.PublishAsync(
          new RunEvent(
              unrelatedRunId,
              1,
              DateTimeOffset.UtcNow,
              RunEventKind.Log,
              null,
              null,
              null,
              "unrelated event",
              null),
          cancellationToken);
      return run;
    }

    public Task AbandonAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
