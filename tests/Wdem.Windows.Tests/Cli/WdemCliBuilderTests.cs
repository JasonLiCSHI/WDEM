using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Cli;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Windows.Tests.Cli;

public sealed class WdemCliBuilderTests
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Theory]
  [InlineData("--json")]
  [InlineData("--json=true")]
  public async Task Host_ParseErrorWritesJsonEventWithoutCreatingComposition(string jsonOption)
  {
    var factoryCalled = false;
    var output = new StringWriter();
    var error = new StringWriter();

    var exitCode = await WdemCliHost.RunAsync(
        ["apply", "--profile", "developer.yaml", "--max-concurrency", "password=parse-secret", jsonOption],
        _ =>
        {
          factoryCalled = true;
          throw new InvalidOperationException("composition should not be created");
        },
        output,
        error,
        CancellationToken.None);

    Assert.Equal(2, exitCode);
    Assert.False(factoryCalled);
    Assert.Empty(output.ToString());
    Assert.DoesNotContain("parse-secret", error.ToString());
    var runEvent = Assert.Single(DeserializeEvents(error));
    Assert.Equal(WdemErrorCode.ProfileError, runEvent.Error?.Code);
  }

  [Fact]
  public async Task Host_HelpDoesNotCreateCompositionOrExposeInitializationFailure()
  {
    var factoryCalled = false;
    var output = new StringWriter();
    var error = new StringWriter();

    var exitCode = await WdemCliHost.RunAsync(
        ["--help"],
        _ =>
        {
          factoryCalled = true;
          throw new InvalidOperationException("initialization failure");
        },
        output,
        error);

    Assert.Equal(0, exitCode);
    Assert.False(factoryCalled);
    Assert.Contains("Usage", output.ToString(), StringComparison.OrdinalIgnoreCase);
    Assert.Empty(error.ToString());
  }

  [Fact]
  public async Task Host_EmptyProfileWritesJsonEventWithoutCreatingComposition()
  {
    var factoryCalled = false;
    var error = new StringWriter();

    var exitCode = await WdemCliHost.RunAsync(
        ["inspect", "--profile", "", "--json"],
        _ =>
        {
          factoryCalled = true;
          throw new InvalidOperationException("composition should not be created");
        },
        new StringWriter(),
        error);

    Assert.Equal(2, exitCode);
    Assert.False(factoryCalled);
    Assert.Single(DeserializeEvents(error));
  }

  [Fact]
  public async Task Host_InitializationFailureWritesRedactedJsonEventAfterSuccessfulParse()
  {
    var error = new StringWriter();

    var exitCode = await WdemCliHost.RunAsync(
        ["inspect", "--profile", "developer.yaml", "--json"],
        _ => throw new InvalidOperationException("token=initialization-secret"),
        new StringWriter(),
        error);

    Assert.Equal(1, exitCode);
    Assert.DoesNotContain("initialization-secret", error.ToString());
    var runEvent = Assert.Single(DeserializeEvents(error));
    Assert.Equal(WdemErrorCode.ProviderError, runEvent.Error?.Code);
  }

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

  [Theory]
  [InlineData("inspect")]
  [InlineData("apply")]
  [InlineData("retry")]
  [InlineData("resume")]
  public async Task RunCommands_BindReportFile(string commandName)
  {
    var handler = new CapturingHandler();
    string runId = "7530dd5c-70bd-47a6-a353-e612ceb6c32c";
    string[] arguments = commandName switch
    {
      "inspect" => ["inspect", "--profile", "developer.yaml", "--report", "result.json"],
      "apply" => ["apply", "--profile", "developer.yaml", "--report", "result.json"],
      "retry" => ["retry", "--run", runId, "--resource", "git", "--report", "result.json"],
      _ => ["resume", "--run", runId, "--report", "result.json"]
    };

    int exitCode = await WdemCliBuilder.Build(handler).Parse(arguments).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal(Path.GetFullPath("result.json"), handler.ReportFile);
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
  public async Task Apply_SelectAcceptsMultipleValuesPerTokenAndRepeatedOptions()
  {
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
    [
      "apply", "--profile", "developer.yaml",
      "--select", "git", "dotnet-sdk",
      "--select", "node"
    ]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.True(handler.Request!.SelectedOptionalResourceIds.SetEquals(
        ["git", "dotnet-sdk", "node"]));
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
  public async Task Retry_ResourceAcceptsMultipleValuesPerTokenAndRepeatedOptions()
  {
    var runId = Guid.Parse("7530dd5c-70bd-47a6-a353-e612ceb6c32c");
    var handler = new CapturingHandler();

    var exitCode = await WdemCliBuilder.Build(handler).Parse(
    [
      "retry", "--run", runId.ToString("D"),
      "--resource", "git", "dotnet-sdk",
      "--resource", "node"
    ]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.True(handler.ResourceIds!.SetEquals(["git", "dotnet-sdk", "node"]));
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

  [Fact]
  public void Root_ExposesOnlySupportedCommands()
  {
    var root = WdemCliBuilder.Build(new CapturingHandler());

    Assert.Equal(
        ["inspect", "apply", "retry", "resume", "runs"],
        root.Subcommands.Select(command => command.Name));
  }

  [Theory]
  [InlineData(typeof(WdemCommandHandler))]
  [InlineData(typeof(EnvironmentRunService))]
  public void DirectServiceConstructorsRequireSharedEventComponents(Type serviceType)
  {
    var constructor = Assert.Single(serviceType.GetConstructors());

    Assert.False(Assert.Single(
        constructor.GetParameters(),
        parameter => parameter.ParameterType == typeof(LogRedactor)).IsOptional);
    Assert.False(Assert.Single(
        constructor.GetParameters(),
        parameter => parameter.ParameterType == typeof(IRunEventSink)).IsOptional);
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
    var sink = new RunEventHub();
    RunEvent[] published =
    [
      new RunEvent(
          run.RunId,
          41,
          DateTimeOffset.Parse("2026-08-29T00:00:41Z"),
          RunEventKind.StepProgress,
          "git",
          "install",
          0.5,
          "Installing Git.",
          null),
      new RunEvent(
          run.RunId,
          42,
          DateTimeOffset.Parse("2026-08-29T00:00:42Z"),
          RunEventKind.Completed,
          null,
          null,
          1,
          "Succeeded",
          null)
    ];
    var service = new StubEnvironmentRunService
    {
      Result = run,
      EventSink = sink,
      Events = published
    };
    var output = new StringWriter();
    var request = new RunRequest(
        Path.GetFullPath("developer.yaml"),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        output,
        new StringWriter(),
        new LogRedactor(),
        sink);

    var exitCode = await handler.InspectAsync(request, json: true, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.Same(request, service.InspectRequest);
    var lines = output.ToString().Split(
        Environment.NewLine,
        StringSplitOptions.RemoveEmptyEntries);
    using var firstJson = JsonDocument.Parse(lines[0]);
    Assert.True(firstJson.RootElement.TryGetProperty("runId", out _));
    Assert.True(firstJson.RootElement.TryGetProperty("timestampUtc", out _));
    Assert.Equal("stepProgress", firstJson.RootElement.GetProperty("kind").GetString());
    Assert.False(firstJson.RootElement.TryGetProperty("RunId", out _));
    var events = lines
        .Select(line => JsonSerializer.Deserialize<RunEvent>(line, JsonOptions))
        .ToArray();
    Assert.Collection(
        events,
        runEvent =>
        {
          Assert.NotNull(runEvent);
          Assert.Equal(41, runEvent.Sequence);
          Assert.Equal(RunEventKind.StepProgress, runEvent.Kind);
          Assert.Equal("git", runEvent.ResourceId);
        },
        runEvent =>
        {
          Assert.NotNull(runEvent);
          Assert.Equal(42, runEvent.Sequence);
          Assert.Equal(RunEventKind.Completed, runEvent.Kind);
        });
  }

  [Fact]
  public async Task CommandHandler_WritesTargetEventBeforeRunOperationCompletes()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var sink = new RunEventHub();
    var published = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new LiveOutputEnvironmentRunService(run, sink, published, release.Task);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        output,
        new StringWriter(),
        new LogRedactor(),
        sink);

    var execution = handler.ApplyAsync(Request(), json: true, CancellationToken.None);
    await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

    try
    {
      var observed = Assert.Single(DeserializeEvents(output));
      Assert.Equal(run.RunId, observed.RunId);
      Assert.False(execution.IsCompleted);
    }
    finally
    {
      release.SetResult();
      Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
    }
  }

  [Fact]
  public async Task CommandHandler_ApplyWritesRequestedReport()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    using var sink = new RunEventHub();
    var service = new StubEnvironmentRunService { Result = run };
    var redactor = new LogRedactor();
    string directory = Path.Combine(Path.GetTempPath(), $"wdem-cli-report-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string reportPath = Path.Combine(directory, "result.json");
    try
    {
      var handler = new WdemCommandHandler(
          service,
          new StubExecutionRunStore(),
          new StringWriter(),
          new StringWriter(),
          redactor,
          sink,
          reportExporter: new RunReportExporter(redactor));

      int exitCode = await handler.ApplyAsync(
          Request(),
          json: false,
          reportPath,
          CancellationToken.None);

      Assert.Equal(0, exitCode);
      using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
      Assert.Equal(run.RunId, report.RootElement.GetProperty("runId").GetGuid());
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData("retry")]
  [InlineData("resume")]
  public async Task CommandHandler_RetryAndResumeWriteOnlyTargetRunEvents(string command)
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var unrelated = run with { RunId = Guid.NewGuid() };
    var sink = new RunEventHub();
    var service = new StubEnvironmentRunService
    {
      Result = run,
      EventSink = sink,
      Events = [Event(unrelated, "unrelated"), Event(run, "target")]
    };
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        output,
        new StringWriter(),
        new LogRedactor(),
        sink);

    var exitCode = command == "retry"
        ? await handler.RetryAsync(
            Guid.NewGuid(),
            new HashSet<string>(["git"], StringComparer.OrdinalIgnoreCase),
            json: true,
            CancellationToken.None)
        : await handler.ResumeAsync(Guid.NewGuid(), json: true, CancellationToken.None);

    Assert.Equal(0, exitCode);
    var observed = Assert.Single(DeserializeEvents(output));
    Assert.Equal(run.RunId, observed.RunId);
    Assert.Equal("target", observed.Message);
  }

  [Fact]
  public async Task CommandHandler_RequiredOutputFailureReturnsUnexpectedHostExit()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var sink = new RunEventHub();
    var service = new StubEnvironmentRunService
    {
      Result = run,
      EventSink = sink,
      Events =
      [
        new RunEvent(
            run.RunId,
            1,
            run.EndedAtUtc!.Value,
            RunEventKind.Completed,
            null,
            null,
            1,
            "Succeeded",
            null)
      ]
    };
    var error = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        new ThrowingTextWriter(),
        error,
        new LogRedactor(),
        sink);

    var exitCode = await handler.ApplyAsync(Request(), json: true, CancellationToken.None);

    Assert.Equal(1, exitCode);
    var failure = Assert.Single(DeserializeEvents(error));
    Assert.Equal(WdemErrorCode.ProviderError, failure.Error?.Code);
  }

  [Fact]
  public async Task CommandHandler_HangingOutputReturnsUnexpectedHostExitWithinWriteDeadline()
  {
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var sink = new RunEventHub();
    var service = new StubEnvironmentRunService
    {
      Result = run,
      EventSink = sink,
      Events = [Event(run, "Succeeded")]
    };
    var output = new BlockingTextWriter();
    var error = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        new StubExecutionRunStore(),
        output,
        error,
        new LogRedactor(),
        sink,
        TimeSpan.FromMilliseconds(50));

    var exitCode = await handler.ApplyAsync(Request(), json: true, CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(2));
    output.FailPendingWrite();

    Assert.Equal(1, exitCode);
    var failure = Assert.Single(DeserializeEvents(error));
    Assert.Equal(WdemErrorCode.ProviderError, failure.Error?.Code);
  }

  [Fact]
  public async Task CommandHandler_ConcurrentOperationsDoNotShareRequiredOutputSubscriptions()
  {
    var firstRun = CompletedRun(ExecutionOutcome.Succeeded);
    var secondRun = firstRun with
    {
      RunId = Guid.Parse("0d294b18-8fc7-4a02-b47c-411b8aaec27b")
    };
    var sink = new RunEventHub();
    using var ready = new CountdownEvent(2);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstService = new CoordinatedEnvironmentRunService(
        firstRun,
        sink,
        Event(firstRun, "first"),
        ready,
        start.Task);
    var secondService = new CoordinatedEnvironmentRunService(
        secondRun,
        sink,
        Event(secondRun, "second"),
        ready,
        start.Task);
    var firstError = new StringWriter();
    var firstHandler = new WdemCommandHandler(
        firstService,
        new StubExecutionRunStore(),
        new ThrowingTextWriter(),
        firstError,
        new LogRedactor(),
        sink);
    var secondOutput = new StringWriter();
    var secondHandler = new WdemCommandHandler(
        secondService,
        new StubExecutionRunStore(),
        secondOutput,
        new StringWriter(),
        new LogRedactor(),
        sink);
    var first = Task.Run(() => firstHandler.ApplyAsync(
        Request(),
        json: true,
        CancellationToken.None));
    var second = Task.Run(() => secondHandler.ApplyAsync(
        Request(),
        json: true,
        CancellationToken.None));
    Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

    start.SetResult();

    Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.Equal(0, await second.WaitAsync(TimeSpan.FromSeconds(5)));
    var observed = Assert.Single(DeserializeEvents(secondOutput));
    Assert.Equal(secondRun.RunId, observed.RunId);
    Assert.Equal("second", observed.Message);
    Assert.Single(DeserializeEvents(firstError));
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
        new StringWriter(),
        new LogRedactor(),
        new RunEventHub());
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
    var sink = new RunEventHub();
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService
        {
          Result = run,
          EventSink = sink,
          Events =
          [
            new RunEvent(
                run.RunId,
                1,
                run.EndedAtUtc!.Value,
                RunEventKind.Log,
                error.ResourceId,
                error.StepId,
                null,
                error.Summary,
                error)
          ]
        },
        new StubExecutionRunStore(),
        output,
        new StringWriter(),
        new LogRedactor(),
        sink);

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
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = CompletedRun(ExecutionOutcome.Cancelled);
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new OperationCanceledException()
    };
    var handler = Handler(service);

    Assert.Equal(130, await InvokeApplyAsync(cancelled));
    Assert.Equal(130, await handler.ApplyAsync(Request(), false, cancellation.Token));
  }

  [Fact]
  public async Task CommandHandler_UnrelatedOperationCanceledExceptionReturnsOne()
  {
    var service = new StubEnvironmentRunService
    {
      Result = CompletedRun(ExecutionOutcome.Succeeded),
      Failure = new OperationCanceledException("provider aborted without caller cancellation")
    };

    var exitCode = await Handler(service).ApplyAsync(
        Request(),
        false,
        CancellationToken.None);

    Assert.Equal(1, exitCode);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommandHandler_CancelledOperationWritesRedactedEvent(bool json)
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
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
        error,
        new LogRedactor(),
        new RunEventHub());

    var exitCode = await handler.ApplyAsync(Request(), json, cancellation.Token);

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
        error,
        new LogRedactor(),
        new RunEventHub());

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
  [InlineData(WdemErrorCode.DetectionError)]
  [InlineData(WdemErrorCode.ProviderError)]
  public async Task CommandHandler_NonExecutableExecutionDiagnosticReturnsThree(
      WdemErrorCode errorCode)
  {
    var run = CompletedRun(ExecutionOutcome.Failed) with
    {
      Plan = new ExecutionPlan
      {
        PlanId = Guid.Parse("9b32c69b-e693-4fb0-b895-9ab0528f1c33"),
        Fingerprint = "non-executable-provider-plan",
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        Layers = [],
        Resources = [],
        IsExecutable = false,
        Errors =
        [
          new StructuredError(
              errorCode,
              "Execution preparation failed.",
              "The provider could not prepare the resource.")
        ]
      }
    };

    Assert.Equal(3, await InvokeApplyAsync(run));
  }

  [Fact]
  public async Task CommandHandler_NonExecutablePlanWithoutDiagnosticsReturnsThree()
  {
    var run = CompletedRun(ExecutionOutcome.Failed) with
    {
      Plan = new ExecutionPlan
      {
        PlanId = Guid.Parse("e157547e-e81c-4fa3-9fd4-eb217c75a2a8"),
        Fingerprint = "non-executable-plan",
        ProfileId = "developer",
        ProfileVersion = "1.0.0",
        Layers = [],
        Resources = [],
        IsExecutable = false
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
    var result = Assert.Single(run.ResourceResults.Values);
    var sink = new RunEventHub();
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService
        {
          Result = run,
          EventSink = sink,
          Events =
          [
            new RunEvent(
                run.RunId,
                1,
                run.EndedAtUtc!.Value,
                RunEventKind.ResourceStateChanged,
                result.ResourceId,
                null,
                result.Progress,
                result.Message!,
                result.Error)
          ]
        },
        new StubExecutionRunStore(),
        output,
        new StringWriter(),
        new LogRedactor(),
        sink);

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
        new StringWriter(),
        new LogRedactor(),
        new RunEventHub());

    var exitCode = await handler.ListRunsAsync(true, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.DoesNotContain("list-profile-secret", output.ToString());
    var runEvent = Assert.Single(DeserializeEvents(output));
    Assert.Equal(RunEventKind.RunStateChanged, runEvent.Kind);
  }

  [Theory]
  [InlineData(WdemErrorCode.ProfileError, 2)]
  [InlineData(WdemErrorCode.DependencyError, 2)]
  [InlineData(WdemErrorCode.ProviderError, 3)]
  public async Task CommandHandler_RunsListEmitsRedactedStoreDiagnosticsAndClassifiesExit(
      WdemErrorCode errorCode,
      int expectedExitCode)
  {
    var output = new StringWriter();
    var store = new StubExecutionRunStore
    {
      StoreDiagnostics =
      [
        new StructuredError(
            errorCode,
            "password=list-diagnostic-secret",
            "token=list-diagnostic-detail")
      ]
    };
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService
        {
          Result = CompletedRun(ExecutionOutcome.Succeeded)
        },
        store,
        output,
        new StringWriter(),
        new LogRedactor(),
        new RunEventHub());

    var exitCode = await handler.ListRunsAsync(true, CancellationToken.None);

    Assert.Equal(expectedExitCode, exitCode);
    Assert.DoesNotContain("list-diagnostic-secret", output.ToString());
    Assert.DoesNotContain("list-diagnostic-detail", output.ToString());
    var diagnosticEvent = Assert.Single(DeserializeEvents(output));
    Assert.Equal(RunEventKind.Log, diagnosticEvent.Kind);
    Assert.Equal(errorCode, diagnosticEvent.Error?.Code);
  }

  [Fact]
  public async Task CommandHandler_HumanRunOutputIncludesCopyableRunId()
  {
    var output = new StringWriter();
    var run = CompletedRun(ExecutionOutcome.Succeeded);
    var handler = new WdemCommandHandler(
        new StubEnvironmentRunService { Result = run },
        new StubExecutionRunStore { Runs = [run] },
        output,
        new StringWriter(),
        new LogRedactor(),
        new RunEventHub());

    var exitCode = await handler.ListRunsAsync(false, CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.Contains(run.RunId.ToString("D"), output.ToString(), StringComparison.Ordinal);
  }

  private static RunEvent[] DeserializeEvents(StringWriter writer) =>
      writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
          .Select(line => JsonSerializer.Deserialize<RunEvent>(line, JsonOptions))
          .OfType<RunEvent>()
          .ToArray();

  private static RunEvent Event(ExecutionRun run, string message) => new(
      run.RunId,
      1,
      run.EndedAtUtc!.Value,
      RunEventKind.Completed,
      null,
      null,
      1,
      message,
      null);

  private static RunRequest Request() => new(
      Path.GetFullPath("developer.yaml"),
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static WdemCommandHandler Handler(IEnvironmentRunService service) => new(
      service,
      new StubExecutionRunStore(),
      new StringWriter(),
      new StringWriter(),
      new LogRedactor(),
      new RunEventHub());

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
    public string? ReportFile { get; private set; }

    public Task<int> InspectAsync(
        RunRequest request,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken)
    {
      ReportFile = reportFile;
      return InspectAsync(request, json, cancellationToken);
    }

    public Task<int> ApplyAsync(
        RunRequest request,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken)
    {
      ReportFile = reportFile;
      return ApplyAsync(request, json, cancellationToken);
    }

    public Task<int> RetryAsync(
        Guid runId,
        IReadOnlySet<string> resourceIds,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken)
    {
      ReportFile = reportFile;
      return RetryAsync(runId, resourceIds, json, cancellationToken);
    }

    public Task<int> ResumeAsync(
        Guid runId,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken)
    {
      ReportFile = reportFile;
      return ResumeAsync(runId, json, cancellationToken);
    }

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
    public IRunEventSink? EventSink { get; init; }
    public IReadOnlyList<RunEvent> Events { get; init; } = [];
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

    private async Task<ExecutionRun> GetResult()
    {
      if (Failure is not null)
      {
        throw Failure;
      }

      EventSink?.BindCurrentScopeToRun(Result.RunId);
      foreach (var runEvent in Events)
      {
        await EventSink!.PublishAsync(runEvent, CancellationToken.None);
      }

      return Result;
    }
  }

  private sealed class CoordinatedEnvironmentRunService(
      ExecutionRun result,
      IRunEventSink eventSink,
      RunEvent runEvent,
      CountdownEvent ready,
      Task start) : IEnvironmentRunService
  {
    public Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken) => ApplyAsync(request, cancellationToken);

    public async Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      ready.Signal();
      await start.WaitAsync(cancellationToken);
      eventSink.BindCurrentScopeToRun(result.RunId);
      await eventSink.PublishAsync(runEvent, cancellationToken);
      return result;
    }

    public Task<ExecutionRun> RetryAsync(
        Guid priorRunId,
        IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);

    public Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AbandonAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class LiveOutputEnvironmentRunService(
      ExecutionRun result,
      IRunEventSink eventSink,
      TaskCompletionSource published,
      Task release) : IEnvironmentRunService
  {
    public Task<ExecutionRun> InspectAsync(
        RunRequest request,
        CancellationToken cancellationToken) => ApplyAsync(request, cancellationToken);

    public async Task<ExecutionRun> ApplyAsync(
        RunRequest request,
        CancellationToken cancellationToken)
    {
      eventSink.BindCurrentScopeToRun(result.RunId);
      await eventSink.PublishAsync(Event(result, "live"), cancellationToken);
      published.SetResult();
      await release.WaitAsync(cancellationToken);
      return result;
    }

    public Task<ExecutionRun> RetryAsync(
        Guid priorRunId,
        IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);

    public Task<ExecutionRun> RecoverAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task AbandonAsync(
        Guid priorRunId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class StubExecutionRunStore : IExecutionRunStore
  {
    public IReadOnlyList<StructuredError> Diagnostics => StoreDiagnostics;
    public IReadOnlyList<StructuredError> StoreDiagnostics { get; init; } = [];
    public IReadOnlyList<ExecutionRun> Runs { get; init; } = [];
    public bool ListCalled { get; private set; }

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CreateAsync(
        ExecutionRun run,
        IReadOnlyList<ApprovedResourceSeal> approvedResources,
        CancellationToken cancellationToken) => Task.CompletedTask;

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

  private sealed class ThrowingTextWriter : TextWriter
  {
    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(string? value) =>
        Task.FromException(new IOException("output failed"));

    public override Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new IOException("output failed"));
  }

  private sealed class BlockingTextWriter : TextWriter
  {
    private readonly TaskCompletionSource _pending = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default) => _pending.Task;

    public void FailPendingWrite() =>
        _pending.TrySetException(new IOException("late output failure"));
  }
}
