using System.Text.Json;
using Wdem.Cli;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Windows.Tests.Cli;

public sealed class WdemCliBuilderTests
{
  [Fact]
  public async Task Inspect_BindsProfileSelectionsAndJson()
  {
    var handler = new CapturingHandler();
    var command = WdemCliBuilder.Build(handler);

    var exitCode = await command.Parse(
    [
      "inspect",
      "--profile", @"profiles\csharp-developer.yaml",
      "--select", "resharper",
      "--json"
    ]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("inspect", handler.Command);
    Assert.Equal(
        Path.GetFullPath(@"profiles\csharp-developer.yaml"),
        handler.Request!.ProfilePath);
    Assert.Contains("resharper", handler.Request!.SelectedOptionalResourceIds);
    Assert.True(handler.Json);
  }

  [Fact]
  public async Task Apply_BindsProfileSelectionsConcurrencyAndJson()
  {
    var handler = new CapturingHandler();
    var command = WdemCliBuilder.Build(handler);

    var exitCode = await command.Parse(
    [
      "apply",
      "--profile", "developer.yaml",
      "--select", "git",
      "--select", "dotnet-sdk",
      "--max-concurrency", "32",
      "--json"
    ]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("apply", handler.Command);
    Assert.Equal(Path.GetFullPath("developer.yaml"), handler.Request!.ProfilePath);
    Assert.Equal(32, handler.Request.MaximumConcurrency);
    Assert.Equal(2, handler.Request.SelectedOptionalResourceIds.Count);
    Assert.Contains("git", handler.Request.SelectedOptionalResourceIds);
    Assert.Contains("dotnet-sdk", handler.Request.SelectedOptionalResourceIds);
    Assert.True(handler.Json);
  }

  [Fact]
  public async Task Retry_BindsRunMultipleResourcesAndJson()
  {
    var runId = Guid.Parse("7530dd5c-70bd-47a6-a353-e612ceb6c32c");
    var handler = new CapturingHandler();
    var command = WdemCliBuilder.Build(handler);

    var exitCode = await command.Parse(
    [
      "retry",
      "--run", runId.ToString("D"),
      "--resource", "git",
      "--resource", "dotnet-sdk",
      "--json"
    ]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("retry", handler.Command);
    Assert.Equal(runId, handler.RunId);
    Assert.Equal(2, handler.ResourceIds!.Count);
    Assert.Contains("git", handler.ResourceIds);
    Assert.Contains("dotnet-sdk", handler.ResourceIds);
    Assert.True(handler.Json);
  }

  [Fact]
  public async Task Resume_BindsRunAndJson()
  {
    var runId = Guid.Parse("e3c67e49-54ca-4b46-831c-68c667303d36");
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
        ["resume", "--run", runId.ToString("D"), "--json"]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("resume", handler.Command);
    Assert.Equal(runId, handler.RunId);
    Assert.True(handler.Json);
  }

  [Fact]
  public async Task RunsList_BindsJson()
  {
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
        ["runs", "list", "--json"]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("runs list", handler.Command);
    Assert.True(handler.Json);
  }

  [Theory]
  [InlineData("inspect", "--profile", "developer.yaml", "--select")]
  [InlineData("apply", "--profile", "developer.yaml", "--select")]
  public void SelectWithoutResource_IsRejected(params string[] arguments)
  {
    var result = WdemCliBuilder.Build(new CapturingHandler()).Parse(arguments);

    Assert.NotEmpty(result.Errors);
  }

  [Theory]
  [InlineData("inspect")]
  [InlineData("apply")]
  [InlineData("retry", "--run", "7530dd5c-70bd-47a6-a353-e612ceb6c32c")]
  [InlineData("retry", "--resource", "git")]
  [InlineData("resume")]
  public void RequiredOptions_AreRejectedWhenMissing(params string[] arguments)
  {
    var result = WdemCliBuilder.Build(new CapturingHandler()).Parse(arguments);

    Assert.NotEmpty(result.Errors);
  }

  [Theory]
  [InlineData("retry", "--run", "not-a-guid", "--resource", "git")]
  [InlineData("resume", "--run", "not-a-guid")]
  public void RunOptions_RejectInvalidGuids(params string[] arguments)
  {
    var result = WdemCliBuilder.Build(new CapturingHandler()).Parse(arguments);

    Assert.NotEmpty(result.Errors);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(33)]
  public void Apply_RejectsConcurrencyOutsideSupportedRange(int value)
  {
    var result = WdemCliBuilder.Build(new CapturingHandler()).Parse(
        ["apply", "--profile", "developer.yaml", "--max-concurrency", value.ToString()]);

    Assert.NotEmpty(result.Errors);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(32)]
  public async Task Apply_AcceptsConcurrencyRangeBoundaries(int value)
  {
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
        ["apply", "--profile", "developer.yaml", "--max-concurrency", value.ToString()])
        .InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal(value, handler.Request!.MaximumConcurrency);
  }

  [Fact]
  public async Task Apply_UsesDocumentedOptionalDefaults()
  {
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
        ["apply", "--profile", "developer.yaml"]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Empty(handler.Request!.SelectedOptionalResourceIds);
    Assert.Equal(4, handler.Request.MaximumConcurrency);
    Assert.False(handler.Json);
  }

  [Theory]
  [InlineData("run")]
  [InlineData("generate")]
  [InlineData("state")]
  [InlineData("completion")]
  [InlineData("config")]
  public void RetiredRootCommands_AreNotExposed(string command)
  {
    var result = WdemCliBuilder.Build(new CapturingHandler()).Parse([command]);

    Assert.NotEmpty(result.Errors);
  }

  [Fact]
  public async Task CommandHandler_InspectCallsServiceAndWritesJsonLineEvents()
  {
    var run = CompletedRun(
        ExecutionOutcome.Succeeded,
        new ResourceResult
        {
          ResourceId = "git",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          Progress = 1,
          Message = "Git is ready."
        });
    var service = new StubEnvironmentRunService { Result = run };
    var output = new StringWriter();
    var request = new RunRequest(
        Path.GetFullPath("developer.yaml"),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        output,
        new StringWriter());

    var exitCode = await handler.InspectAsync(request, json: true, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.Same(request, service.InspectRequest);
    var events = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<RunEvent>(line))
        .ToArray();
    Assert.Collection(
        events,
        runEvent =>
        {
          Assert.NotNull(runEvent);
          Assert.Equal(RunEventKind.ResourceStateChanged, runEvent.Kind);
          Assert.Equal("git", runEvent.ResourceId);
        },
        runEvent =>
        {
          Assert.NotNull(runEvent);
          Assert.Equal(RunEventKind.Completed, runEvent.Kind);
        });
  }

  [Fact]
  public async Task CommandHandler_CommandsCallMatchingRunOperations()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var service = new StubEnvironmentRunService { Result = run };
    var store = new StubExecutionRunStore { Runs = [run] };
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter());
    var request = new RunRequest(
        Path.GetFullPath("developer.yaml"),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var priorRunId = Guid.Parse("b0bf9dd4-7977-47a4-bdf6-4c5d6162e3df");
    IReadOnlySet<string> resources = new HashSet<string>(["git"]);

    Assert.Equal(0, await handler.ApplyAsync(request, false, CancellationToken.None));
    Assert.Equal(0, await handler.RetryAsync(priorRunId, resources, false, CancellationToken.None));
    Assert.Equal(0, await handler.ResumeAsync(priorRunId, false, CancellationToken.None));
    Assert.Equal(0, await handler.ListRunsAsync(true, CancellationToken.None));

    Assert.Same(request, service.ApplyRequest);
    Assert.Equal(priorRunId, service.RetryRunId);
    Assert.Same(resources, service.RetryResourceIds);
    Assert.Equal(priorRunId, service.RecoverRunId);
    Assert.True(store.ListCalled);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommandHandler_ProfileOrPlanValidationFailureReturnsTwo(bool json)
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "Profile validation failed: password=validation-summary-secret.",
        "The profile is invalid.")
    {
      ResourceId = "token=validation-resource-secret"
    };
    var run = CompletedRun(ExecutionOutcome.Failed) with
    {
      Plan = new ExecutionPlan
      {
        PlanId = Guid.Parse("fb3f042e-9519-43cc-af97-f677555fc8d2"),
        Fingerprint = "invalid-plan",
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        Layers = [],
        Resources = [],
        IsExecutable = false,
        Errors = [error]
      }
    };
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService { Result = run },
        new StubExecutionRunStore(),
        output,
        new StringWriter());

    var exitCode = await handler.ApplyAsync(
        new RunRequest(Path.GetFullPath("developer.yaml"), new HashSet<string>()),
        json,
        CancellationToken.None);

    Assert.Equal(2, exitCode);
    Assert.DoesNotContain("validation-summary-secret", output.ToString());
    Assert.DoesNotContain("validation-resource-secret", output.ToString());
    if (json)
    {
      var events = DeserializeEvents(output);
      Assert.Contains(events, runEvent => runEvent.Error?.Code == WdemErrorCode.ProfileError);
    }
  }

  [Fact]
  public async Task CommandHandler_ExecutionFailureOrBlockedResourceReturnsThree()
  {
    var failed = CompletedRun(ExecutionOutcome.Failed);
    var blocked = CompletedRun(
        ExecutionOutcome.Succeeded,
        new ResourceResult
        {
          ResourceId = "git",
          State = ExecutionState.Blocked
        });

    Assert.Equal(3, await InvokeApplyAsync(failed));
    Assert.Equal(3, await InvokeApplyAsync(blocked));
  }

  [Fact]
  public async Task CommandHandler_IncompleteRunReturnsThree()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded) with
    {
      State = ExecutionState.Running,
      Outcome = null,
      EndedAtUtc = null
    };

    Assert.Equal(3, await InvokeApplyAsync(run));
  }

  [Fact]
  public async Task CommandHandler_CancelledRunOrOperationReturns130()
  {
    var cancelled = CompletedRun(ExecutionOutcome.Cancelled);
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new OperationCanceledException()
    };
    var handler = Handler(service);

    Assert.Equal(130, await InvokeApplyAsync(cancelled));
    Assert.Equal(130, await handler.ApplyAsync(Request(), false, CancellationToken.None));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommandHandler_CancelledOperationWritesRedactedEvent(bool json)
  {
    var error = new StringWriter();
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new OperationCanceledException("password=cancel-secret")
    };
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        new StringWriter(),
        error);

    var exitCode = await handler.ApplyAsync(Request(), json, CancellationToken.None);

    Assert.Equal(130, exitCode);
    Assert.DoesNotContain("cancel-secret", error.ToString());
    Assert.NotEmpty(error.ToString());
    if (json)
    {
      var runEvent = Assert.Single(DeserializeEvents(error));
      Assert.Equal(WdemErrorCode.CancellationError, runEvent.Error?.Code);
    }
  }

  [Fact]
  public async Task CommandHandler_UnknownArgumentExceptionReturnsOne()
  {
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new ArgumentException("The requested retry resource is invalid.")
    };

    var exitCode = await Handler(service).ApplyAsync(Request(), false, CancellationToken.None);

    Assert.Equal(1, exitCode);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommandHandler_UnexpectedHostExceptionReturnsOneAndWritesRedactedEvent(
      bool json)
  {
    var error = new StringWriter();
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new InvalidOperationException("token=unexpected-host-secret")
    };
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        new StringWriter(),
        error);

    var exitCode = await handler.ApplyAsync(Request(), json, CancellationToken.None);

    Assert.Equal(1, exitCode);
    Assert.DoesNotContain("unexpected-host-secret", error.ToString());
    if (json)
    {
      var runEvent = Assert.Single(DeserializeEvents(error));
      Assert.Equal(WdemErrorCode.ProviderError, runEvent.Error?.Code);
    }
  }

  [Fact]
  public async Task CommandHandler_NonValidationPlanDiagnosticReturnsThree()
  {
    var run = CompletedRun(ExecutionOutcome.Failed) with
    {
      Plan = new ExecutionPlan
      {
        PlanId = Guid.Parse("b1694f20-cb2e-449e-b9d3-8c11fd700d56"),
        Fingerprint = "provider-failure-plan",
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        Layers = [],
        Resources = [],
        IsExecutable = true,
        Errors =
        [
          new StructuredError(
              WdemErrorCode.ProviderError,
              "Provider failure.",
              "The provider failed while preparing the run.")
        ]
      }
    };

    Assert.Equal(3, await InvokeApplyAsync(run));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommandHandler_RunEventOutputRedactsMessagesAndNestedErrors(bool json)
  {
    var output = new StringWriter();
    var run = CompletedRun(
        ExecutionOutcome.Succeeded,
        new ResourceResult
        {
          ResourceId = "token=resource-id-secret",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          Message = "password=resource-message-secret",
          Error = new StructuredError(
              WdemErrorCode.ProviderError,
              "Provider diagnostic.",
              "Provider detail.")
          {
            LogLocation = "token=nested-error-secret"
          }
        });
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService { Result = run },
        new StubExecutionRunStore(),
        output,
        new StringWriter());

    var exitCode = await handler.ApplyAsync(Request(), json, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.DoesNotContain("resource-id-secret", output.ToString());
    Assert.DoesNotContain("resource-message-secret", output.ToString());
    Assert.DoesNotContain("nested-error-secret", output.ToString());
    if (json)
    {
      var resourceEvent = Assert.Single(
          DeserializeEvents(output),
          runEvent => runEvent.Kind == RunEventKind.ResourceStateChanged);
      Assert.NotNull(resourceEvent.Error);
      Assert.DoesNotContain("nested-error-secret", resourceEvent.Error.LogLocation);
    }
  }

  [Fact]
  public async Task CommandHandler_RunsListWritesRedactedJsonRunEvents()
  {
    var output = new StringWriter();
    var run = CompletedRun(ExecutionOutcome.Succeeded) with
    {
      ProfileId = "password=list-profile-secret"
    };
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService { Result = run },
        new StubExecutionRunStore { Runs = [run] },
        output,
        new StringWriter());

    var exitCode = await handler.ListRunsAsync(true, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.DoesNotContain("list-profile-secret", output.ToString());
    var runEvent = Assert.Single(DeserializeEvents(output));
    Assert.Equal(RunEventKind.RunStateChanged, runEvent.Kind);
  }

  private static RunEvent[] DeserializeEvents(StringWriter writer) =>
      writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
          .Select(line => JsonSerializer.Deserialize<RunEvent>(line))
          .OfType<RunEvent>()
          .ToArray();

  private static RunRequest Request() => new(
      Path.GetFullPath("developer.yaml"),
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static WdemCommandHandler Handler(IEnvironmentRunService service) => new(
      service,
      new StubExecutionRunStore(),
      new StringWriter(),
      new StringWriter());

  private static Task<int> InvokeApplyAsync(ExecutionRun run) =>
      Handler(new StubEnvironmentRunService { Result = run }).ApplyAsync(
          Request(),
          false,
          CancellationToken.None);

  private static ExecutionRun CompletedRun(
      ExecutionOutcome outcome,
      params ResourceResult[] resources) => new()
      {
        RunId = Guid.Parse("77671851-00ba-4839-be2c-bd71acc05633"),
        Mode = RunMode.Apply,
        ProfileSourcePath = Path.GetFullPath("developer.yaml"),
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        StartedAtUtc = DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
        EndedAtUtc = DateTimeOffset.Parse("2026-08-29T00:01:00Z"),
        State = ExecutionState.Completed,
        Outcome = outcome,
        Machine = new MachineInformation("Windows", "X64", "machine", "user"),
        ResourceResults = resources.ToDictionary(
            resource => resource.ResourceId,
            StringComparer.OrdinalIgnoreCase)
      };

  private sealed class CapturingHandler : IWdemCommandHandler
  {
    public string? Command { get; private set; }
    public RunRequest? Request { get; private set; }
    public bool Json { get; private set; }
    public Guid? RunId { get; private set; }
    public IReadOnlySet<string>? ResourceIds { get; private set; }

    public Task<int> InspectAsync(
        RunRequest request,
        bool json,
        CancellationToken cancellationToken)
    {
      Command = "inspect";
      Request = request;
      Json = json;
      return Task.FromResult(0);
    }

    public Task<int> ApplyAsync(
        RunRequest request,
        bool json,
        CancellationToken cancellationToken)
    {
      Command = "apply";
      Request = request;
      Json = json;
      return Task.FromResult(0);
    }

    public Task<int> RetryAsync(
        Guid runId,
        IReadOnlySet<string> resourceIds,
        bool json,
        CancellationToken cancellationToken)
    {
      Command = "retry";
      RunId = runId;
      ResourceIds = resourceIds;
      Json = json;
      return Task.FromResult(0);
    }

    public Task<int> ResumeAsync(
        Guid runId,
        bool json,
        CancellationToken cancellationToken)
    {
      Command = "resume";
      RunId = runId;
      Json = json;
      return Task.FromResult(0);
    }

    public Task<int> ListRunsAsync(
        bool json,
        CancellationToken cancellationToken)
    {
      Command = "runs list";
      Json = json;
      return Task.FromResult(0);
    }
  }

  private sealed class StubEnvironmentRunService : IEnvironmentRunService
  {
    public required ExecutionRun Result { get; init; }
    public Exception? Failure { get; init; }
    public RunRequest? InspectRequest { get; private set; }
    public RunRequest? ApplyRequest { get; private set; }
    public Guid? RetryRunId { get; private set; }
    public IReadOnlySet<string>? RetryResourceIds { get; private set; }
    public Guid? RecoverRunId { get; private set; }

    public Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      InspectRequest = request;
      return GetResult();
    }

    public Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      ApplyRequest = request;
      return GetResult();
    }

    public Task<ExecutionRun> RetryAsync(
        Guid priorRunId,
        IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken)
    {
      RetryRunId = priorRunId;
      RetryResourceIds = resourceIds;
      return GetResult();
    }

    public Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);

    public Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken)
    {
      RecoverRunId = priorRunId;
      return GetResult();
    }

    public Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private Task<ExecutionRun> GetResult() => Failure is null
        ? Task.FromResult(Result)
        : Task.FromException<ExecutionRun>(Failure);
  }

  private sealed class StubExecutionRunStore : IExecutionRunStore
  {
    public IReadOnlyList<StructuredError> Diagnostics => [];
    public IReadOnlyList<ExecutionRun> Runs { get; init; } = [];
    public bool ListCalled { get; private set; }

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult<ExecutionRun?>(null);

    public Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken) =>
        ListAsyncCore();

    private Task<IReadOnlyList<ExecutionRun>> ListAsyncCore()
    {
      ListCalled = true;
      return Task.FromResult(Runs);
    }

    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExecutionRun>>([]);

    public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(null);

    public Task<ExecutionRun> SaveAsync(
        ExecutionRun run,
        CancellationToken cancellationToken) => Task.FromResult(run);

    public Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken) => Task.FromResult(true);

    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunLogEntry>>([]);
  }
}
