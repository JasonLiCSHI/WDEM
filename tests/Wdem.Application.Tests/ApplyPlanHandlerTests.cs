using Wdem.Application.Execution;
using Wdem.Application.Events;
using Wdem.Application.Inspection;
using Wdem.Application.Planning;
using Wdem.Application.Profiles;
using Wdem.Application.Runtime;
using Wdem.Application.Tests.TestDoubles;
using Wdem.Application.Workflows;
using Wdem.Domain.Execution;
using Wdem.Domain.Versions;
using Wdem.Infrastructure.Profiles;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class ApplyPlanHandlerTests
{
  [Fact]
  public void Apply_RejectsUntrustedRemoteProfileBeforeStartingWorkflow()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var loaded = new LoadedProfile(
        profile,
        ProfileOrigin.Cache,
        "profile-cache.json",
        "UNTRUSTED",
        "test-source");
    var plan = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a"]);
    var runtime = new FakeRuntime();
    var handler = CreateHandler(runtime, trusted: false);

    Assert.Throws<UntrustedProfileException>(() => handler.Start(loaded, plan));
    Assert.Empty(runtime.Invocations);
  }

  [Fact]
  public async Task Apply_DoesNotExecuteATaskWhoseDetectionFailedDuringPlanning()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var inspection = new InspectReport(profile.Tasks.Keys.ToDictionary(
        taskId => taskId,
        taskId => new TaskInspection(
            taskId,
            DetectSucceeded: false,
            DetectedVersion: null,
            Compliance: ComplianceStatus.DetectionFailed,
            VersionRequirement: null,
            new StepReport("detect", 5, "", "access denied")),
        StringComparer.Ordinal));
    var plan = new CreatePlanHandler().CreateForTasks(
        profile,
        rootTaskIds: ["a"],
        inspection);
    var runtime = new FakeRuntime();

    var report = await CreateHandler(runtime).Start(Loaded(profile), plan).Completion;

    Assert.Equal(TaskOutcome.Blocked, report.Tasks["a"].Outcome);
    Assert.Empty(runtime.Invocations);
  }

  [Fact]
  public async Task Apply_IndependentTasksStartConcurrently()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a", "b"]);
    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("b", exitCode: 1)
        .WithApplyThatWaitsForCancellation("a")
        .WithApplyThatWaitsForCancellation("b");
    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    try
    {
      await Task.WhenAll(
          runtime.WaitForCommandStartAsync("a", "apply"),
          runtime.WaitForCommandStartAsync("b", "apply"))
          .WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally
    {
      run.CancelAll();
      await run.Completion;
    }

    Assert.Equal(TaskOutcome.Cancelled, run.Snapshot.Tasks["a"].Outcome);
    Assert.Equal(TaskOutcome.Cancelled, run.Snapshot.Tasks["b"].Outcome);
  }

  [Fact]
  public async Task Apply_DependentTaskWaitsForSuccessfulDependency()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["c"]);
    var finishDependency = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("c", exitCode: 1)
        .WithApplyThatWaitsFor("a", finishDependency.Task)
        .WithApplyThatWaitsForCancellation("c");
    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    await runtime.WaitForCommandStartAsync("a", "apply");
    Assert.DoesNotContain(runtime.Invocations, invocation => invocation.taskId == "c");

    finishDependency.SetResult();
    await runtime.WaitForCommandStartAsync("c", "apply")
        .WaitAsync(TimeSpan.FromSeconds(5));
    run.CancelAll();
    var report = await run.Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["a"].Outcome);
    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["c"].Outcome);
  }

  [Fact]
  public async Task Apply_CancelTask_BlocksDependentsButContinuesIndependentTasks()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a", "b", "c"]);
    var finishIndependentTask = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("b", exitCode: 1)
        .WithDetect("c", exitCode: 1)
        .WithApplyThatWaitsForCancellation("a")
        .WithApplyThatWaitsFor("b", finishIndependentTask.Task)
        .WithApply("c", exitCode: 0);

    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    await Task.WhenAll(
        runtime.WaitForCommandStartAsync("a", "apply"),
        runtime.WaitForCommandStartAsync("b", "apply"));
    run.CancelTask("a");

    Assert.Equal(WorkflowRunState.Running, run.Snapshot.State);
    Assert.Contains(
        run.Snapshot.Tasks["a"].State,
        new[] { TaskExecutionState.Cancelling, TaskExecutionState.Cancelled });
    Assert.False(run.Snapshot.Tasks["a"].CanCancel);
    Assert.True(run.Snapshot.Tasks["b"].CanCancel);
    finishIndependentTask.SetResult();

    var report = await run.Completion;

    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["a"].Outcome);
    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["b"].Outcome);
    Assert.Equal(TaskOutcome.Blocked, report.Tasks["c"].Outcome);
    Assert.Equal(WorkflowRunState.Completed, run.Snapshot.State);
    Assert.Equal(TaskOutcome.Cancelled, run.Snapshot.Tasks["a"].Outcome);
    Assert.Equal(TaskOutcome.Succeeded, run.Snapshot.Tasks["b"].Outcome);
    Assert.Equal(TaskOutcome.Blocked, run.Snapshot.Tasks["c"].Outcome);
  }

  [Fact]
  public async Task Apply_FailedDependency_BlocksDownstreamTasks()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a", "c"]);

    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("c", exitCode: 1)
        .WithApply("a", exitCode: 2);

    var report = await CreateHandler(runtime).Start(Loaded(profile), graph).Completion;

    Assert.Equal(TaskExecutionState.Failed, report.Tasks["a"].State);
    Assert.Equal(TaskOutcome.Failed, report.Tasks["a"].Outcome);
    Assert.Equal(TaskExecutionState.Blocked, report.Tasks["c"].State);
    Assert.Equal(TaskOutcome.Blocked, report.Tasks["c"].Outcome);
  }

  [Fact]
  public async Task Apply_CancelAll_DoesNotStartNewTasks()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["c"]);

    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("c", exitCode: 1)
        .WithApplyThatWaitsForCancellation("a")
        .WithApply("c", exitCode: 0);

    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    await runtime.WaitForCommandStartAsync("a", "apply");
    run.CancelAll();

    Assert.Equal(WorkflowRunState.Cancelling, run.Snapshot.State);
    Assert.All(run.Snapshot.Tasks.Values.Where(task => task.IsPlanned), task =>
    {
      Assert.Equal(TaskExecutionState.Cancelling, task.State);
      Assert.False(task.CanCancel);
    });

    var report = await run.Completion;

    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["a"].Outcome);
    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["c"].Outcome);
    Assert.DoesNotContain(runtime.Invocations, invocation => invocation.taskId == "c");
    Assert.Equal(WorkflowRunState.Completed, run.Snapshot.State);
    Assert.All(run.Snapshot.Tasks.Values, task => Assert.True(task.IsTerminal));
  }

  [Fact]
  public async Task Apply_ReportsDetailedTaskStateAndStageProgress()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["b"]);
    var runtime = new FakeRuntime()
        .WithDetect("b", exitCode: 1)
        .WithApply("b", exitCode: 0);
    var updates = new List<WorkflowProgress>();
    var progress = new InlineProgress<WorkflowProgress>(updates.Add);

    var report = await CreateHandler(runtime).Start(Loaded(profile), graph, progress).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["b"].Outcome);
    Assert.Collection(
        updates.Where(update => update.TaskId == "b" && update.ActivityId is null),
        update => Assert.Equal(TaskExecutionState.Ready, update.State),
        update =>
        {
          Assert.Equal(TaskExecutionState.Detecting, update.State);
          Assert.Equal("detect", update.Stage);
        },
        update =>
        {
          Assert.Equal(TaskExecutionState.Applying, update.State);
          Assert.Equal("apply", update.Stage);
        },
        update =>
        {
          Assert.Equal(TaskExecutionState.Verifying, update.State);
          Assert.Equal("verify", update.Stage);
        },
        update =>
        {
          Assert.Equal(TaskExecutionState.Succeeded, update.State);
          Assert.Equal(TaskOutcome.Succeeded, update.Outcome);
          Assert.Equal(100, update.Percent);
        });
  }

  [Fact]
  public async Task Apply_StreamsCommandOutputWithTaskAndStageContext()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["b"]);
    var runtime = new FakeRuntime()
        .WithDetect("b", exitCode: 1)
        .WithApplyOutput("b", "downloading", WorkflowOutputStream.StandardOutput);
    var updates = new List<WorkflowProgress>();
    var progress = new InlineProgress<WorkflowProgress>(updates.Add);

    await CreateHandler(runtime).Start(Loaded(profile), graph, progress).Completion;

    var output = Assert.Single(updates, update => update.Message == "downloading");
    Assert.Equal("b", output.TaskId);
    Assert.Equal("apply", output.Stage);
    Assert.Equal(TaskExecutionState.Applying, output.State);
    Assert.Equal(WorkflowOutputStream.StandardOutput, output.OutputStream);
  }

  [Fact]
  public async Task Apply_TaskEventsReduceToImmutableWorkflowSnapshots()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["b"]);
    var runtime = new FakeRuntime()
        .WithDetect("b", exitCode: 1)
        .WithApply("b", exitCode: 0);
    var updates = new List<WorkflowUpdate>();

    var run = CreateHandler(runtime).Start(
        Loaded(profile),
        graph,
        updates: new InlineProgress<WorkflowUpdate>(updates.Add));
    await run.Completion;

    Assert.NotEmpty(updates);
    Assert.True(updates.Select(update => update.Snapshot.Revision).SequenceEqual(
        updates.Select(update => update.Snapshot.Revision).Order()));
    Assert.Contains(updates, update =>
        update.Snapshot.Tasks["b"].State == TaskExecutionState.Applying);
    Assert.Equal(WorkflowRunState.Completed, run.Snapshot.State);
    Assert.Equal(TaskExecutionState.Succeeded, run.Snapshot.Tasks["b"].State);
    Assert.Equal(TaskOutcome.Succeeded, run.Snapshot.Tasks["b"].Outcome);
  }

  [Fact]
  public async Task Apply_ParallelTaskUpdatesArePublishedInRevisionOrder()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a", "b"]);
    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithDetect("b", exitCode: 1)
        .WithApplyThatWaitsForCancellation("a")
        .WithApplyThatWaitsForCancellation("b");
    var updates = new List<WorkflowUpdate>();
    var run = CreateHandler(runtime).Start(
        Loaded(profile),
        graph,
        updates: new InlineProgress<WorkflowUpdate>(updates.Add));

    await Task.WhenAll(
        runtime.WaitForCommandStartAsync("a", "apply"),
        runtime.WaitForCommandStartAsync("b", "apply"));
    run.CancelAll();
    await run.Completion;

    Assert.Equal(
        Enumerable.Range(1, updates.Count).Select(revision => (long)revision),
        updates.Select(update => update.Snapshot.Revision));
  }

  [Fact]
  public async Task Apply_EnteringEachActivityStatePrecedesItsRuntimeInvocation()
  {
    var profile = ProfileParser.Parse(PipelineProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["pipeline"]);
    var updates = new List<WorkflowUpdate>();
    var statesSeenByRuntime = new List<TaskExecutionState>();
    var runtime = new FakeRuntime()
        .WithDetect("pipeline", exitCode: 1)
        .WithPre("pipeline", exitCode: 0)
        .WithApply("pipeline", exitCode: 0)
        .WithPost("pipeline", exitCode: 0)
        .OnInvocation(_ => statesSeenByRuntime.Add(updates[^1].Snapshot.Tasks["pipeline"].State));

    var report = await CreateHandler(runtime).Start(
        Loaded(profile),
        graph,
        updates: new InlineProgress<WorkflowUpdate>(updates.Add)).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["pipeline"].Outcome);
    Assert.Equal(
        [
          TaskExecutionState.Detecting,
          TaskExecutionState.RunningPre,
          TaskExecutionState.Applying,
          TaskExecutionState.RunningPost,
          TaskExecutionState.Verifying
        ],
        statesSeenByRuntime);
  }

  [Fact]
  public void ReadySnapshot_ProjectsTaskCapabilitiesWithoutUiRules()
  {
    var profile = ProfileParser.Parse(ProfileJson);

    var snapshot = CreateHandler(new FakeRuntime()).CreateReadySnapshot(profile);

    Assert.Equal(WorkflowRunState.Ready, snapshot.State);
    Assert.All(snapshot.Tasks.Values, task =>
    {
      Assert.True(task.CanStart);
      Assert.False(task.CanCancel);
    });
    Assert.False(snapshot.Tasks["a"].CanSelect);
  }

  [Fact]
  public async Task Apply_CapabilitiesFollowTaskAndWorkflowState()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a"]);
    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithApplyThatWaitsForCancellation("a");
    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    await runtime.WaitForCommandStartAsync("a", "apply");

    Assert.False(run.Snapshot.Tasks["a"].CanStart);
    Assert.True(run.Snapshot.Tasks["a"].CanCancel);
    Assert.False(run.Snapshot.Tasks["b"].CanStart);
    Assert.False(run.Snapshot.Tasks["b"].CanCancel);

    run.CancelAll();
    Assert.False(run.Snapshot.CanCancelAny);
    await run.Completion;

    Assert.True(run.Snapshot.Tasks["a"].CanStart);
    Assert.False(run.Snapshot.Tasks["a"].CanCancel);
  }

  [Fact]
  public async Task Apply_CancellationWinsWhenRuntimeReturnsSuccessAfterCancellation()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a"]);
    var runtime = new FakeRuntime()
        .WithDetect("a", exitCode: 1)
        .WithApplyThatReturnsAfterCancellation("a");
    var run = CreateHandler(runtime).Start(Loaded(profile), graph);

    await runtime.WaitForCommandStartAsync("a", "apply");
    run.CancelTask("a");
    var report = await run.Completion;

    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["a"].Outcome);
    Assert.Equal(TaskExecutionState.Cancelled, run.Snapshot.Tasks["a"].State);
    Assert.DoesNotContain(runtime.Invocations, invocation => invocation.phase == "verify");
  }

  private static ApplyPlanHandler CreateHandler(ITaskRuntime runtime, bool trusted = true) =>
      new(
          runtime,
          DefaultWorkflowActivityExecutor.Instance,
          DefaultTaskWorkflowProvider.Instance,
          new ProfileExecutionAuthorizer(new FakeProfileTrustStore(trusted)),
          NullDomainEventPublisher.Instance);

  private static LoadedProfile Loaded(Wdem.Domain.Profiles.EnvironmentProfile profile) =>
      new(profile, ProfileOrigin.Local, "test-profile.json", "TEST");

  private const string ProfileJson = """
    {
      "id": "test",
      "version": "1.0.0",
      "displayName": "Test",
      "tasks": {
        "a": {
          "displayName": "A",
          "required": true,
          "detect": { "executable": "a", "arguments": [], "missingExitCodes": [1] },
          "apply": { "executable": "a", "arguments": ["apply"] }
        },
        "b": {
          "displayName": "B",
          "required": true,
          "detect": { "executable": "b", "arguments": [], "missingExitCodes": [1] },
          "apply": { "executable": "b", "arguments": ["apply"] }
        },
        "c": {
          "displayName": "C",
          "required": true,
          "dependsOn": ["a"],
          "detect": { "executable": "c", "arguments": [], "missingExitCodes": [1] },
          "apply": { "executable": "c", "arguments": ["apply"] }
        }
      }
    }
    """;

  private const string PipelineProfileJson = """
    {
      "id": "pipeline-test",
      "version": "1.0.0",
      "displayName": "Pipeline test",
      "tasks": {
        "pipeline": {
          "displayName": "Pipeline",
          "required": true,
          "detect": { "executable": "pipeline", "arguments": ["detect"], "missingExitCodes": [1] },
          "pre": [
            { "executable": "pipeline", "arguments": ["pre"] }
          ],
          "apply": { "executable": "pipeline", "arguments": ["apply"] },
          "post": [
            { "executable": "pipeline", "arguments": ["post"] }
          ]
        }
      }
    }
    """;
}
