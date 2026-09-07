using Wdem.Application.Planning;
using Wdem.Application.Execution;
using Wdem.Application.Events;
using Wdem.Application.Profiles;
using Wdem.Application.Runtime;
using Wdem.Application.Tests.TestDoubles;
using Wdem.Application.Workflows;
using Wdem.Domain.Execution;
using Wdem.Domain.Events;
using Wdem.Domain.Workflows;
using Wdem.Infrastructure.Profiles;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class TaskWorkflowTests
{
  [Fact]
  public async Task CustomWorkflow_ExecutesEntryResidenceAndExitActivitiesInOrder()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var executed = new List<string>();
    var updates = new List<WorkflowUpdate>();
    var workflow = new TaskWorkflowDefinition(
        "prepare",
        [
          new TaskWorkflowState(
              "prepare",
              TaskExecutionState.Running,
              entryActivities: [new RecordingActivity("enter", executed)],
              residenceActivities: [new RecordingActivity("reside", executed)],
              exitActivities: [new RecordingActivity("exit", executed)],
              transitions: [TaskWorkflowTransition.Always("finished")]),
          new TaskWorkflowState(
              "finished",
              TaskExecutionState.Succeeded,
              terminalOutcome: TaskOutcome.Succeeded)
        ]);

    var run = CreateHandler(
        new FakeRuntime(),
        new SingleWorkflowProvider(workflow),
        new TestActivityExecutor()).Start(
        Loaded(profile),
        graph,
        updates: new InlineProgress<WorkflowUpdate>(updates.Add));
    var report = await run.Completion;

    Assert.Equal(["enter", "reside", "exit"], executed);
    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["custom"].Outcome);
    Assert.Equal("finished", run.Snapshot.Tasks["custom"].RuntimeStateId);
    Assert.Equal(TaskExecutionState.Succeeded, run.Snapshot.Tasks["custom"].State);

    var activitySnapshots = updates
        .Select(update => update.Snapshot.Tasks["custom"])
        .Where(task => task.ActivityId is not null)
        .ToArray();
    Assert.Equal(
        [
          WorkflowActivityLocation.Entry,
          WorkflowActivityLocation.Residence,
          WorkflowActivityLocation.Exit
        ],
        activitySnapshots.Select(task => task.ActivityLocation));
    Assert.All(activitySnapshots, task =>
    {
      Assert.Equal("prepare", task.RuntimeStateId);
      Assert.Equal(TaskExecutionState.Running, task.State);
    });
  }

  [Fact]
  public async Task CustomWorkflow_WhenExecuted_ThenPublishesDomainEventsInLifecycleOrder()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var executed = new List<string>();
    var domainEvents = new RecordingDomainEventPublisher();
    var workflow = new TaskWorkflowDefinition(
        "prepare",
        [
          new TaskWorkflowState(
              "prepare",
              TaskExecutionState.Running,
              residenceActivities: [new RecordingActivity("configure", executed)],
              transitions: [TaskWorkflowTransition.Always("finished")]),
          new TaskWorkflowState(
              "finished",
              TaskExecutionState.Succeeded,
              terminalOutcome: TaskOutcome.Succeeded)
        ]);

    var report = await CreateHandler(
        new FakeRuntime(),
        new SingleWorkflowProvider(workflow),
        new TestActivityExecutor(),
        domainEvents).Start(
        Loaded(profile),
        graph).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["custom"].Outcome);
    Assert.Collection(
        domainEvents.Events,
        item => Assert.IsType<TaskWorkflowStarted>(item),
        item => Assert.IsType<TaskWorkflowStateEntered>(item),
        item => Assert.IsType<TaskWorkflowActivityStarted>(item),
        item => Assert.IsType<TaskWorkflowActivityCompleted>(item),
        item => Assert.IsType<TaskWorkflowTransitioned>(item),
        item => Assert.IsType<TaskWorkflowFinished>(item));

    var transition = Assert.IsType<TaskWorkflowTransitioned>(domainEvents.Events[4]);
    Assert.Equal("prepare", transition.FromStateId);
    Assert.Equal("finished", transition.ToStateId);
    Assert.Equal("always", transition.TransitionName);

    var finished = Assert.IsType<TaskWorkflowFinished>(domainEvents.Events[5]);
    Assert.Equal("workflow-test", finished.ProfileId);
    Assert.Equal("custom", finished.TaskId);
    Assert.Equal(TaskOutcome.Succeeded, finished.Outcome);
  }

  [Fact]
  public async Task CustomWorkflow_CanRouteActivityFailureToRecoveryState()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var workflow = new TaskWorkflowDefinition(
        "attempt",
        [
          new TaskWorkflowState(
              "attempt",
              TaskExecutionState.Running,
              residenceActivities: [new FailingActivity("try")],
              transitions:
              [
                TaskWorkflowTransition.WhenActivitiesSucceeded("finished"),
                TaskWorkflowTransition.WhenActivitiesFailed("recovered")
              ]),
          new TaskWorkflowState(
              "recovered",
              TaskExecutionState.Running,
              residenceActivities: [new RecordingActivity("recover", [])],
              transitions: [TaskWorkflowTransition.Always("finished")]),
          new TaskWorkflowState(
              "finished",
              TaskExecutionState.Succeeded,
              terminalOutcome: TaskOutcome.Succeeded)
        ]);

    var report = await CreateHandler(
        new FakeRuntime(),
        new SingleWorkflowProvider(workflow),
        new TestActivityExecutor()).Start(
        Loaded(profile),
        graph).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["custom"].Outcome);
  }

  [Fact]
  public async Task CustomWorkflow_CancellationStopsBeforeExitAndDownstreamStates()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var executed = new List<string>();
    var blockingActivity = new BlockingActivity("wait");
    var workflow = new TaskWorkflowDefinition(
        "active",
        [
          new TaskWorkflowState(
              "active",
              TaskExecutionState.Running,
              residenceActivities: [blockingActivity],
              exitActivities: [new RecordingActivity("unsafe-exit", executed)],
              transitions: [TaskWorkflowTransition.Always("finished")]),
          new TaskWorkflowState(
              "finished",
              TaskExecutionState.Succeeded,
              entryActivities: [new RecordingActivity("downstream", executed)],
              terminalOutcome: TaskOutcome.Succeeded)
        ]);

    var run = CreateHandler(
        new FakeRuntime(),
        new SingleWorkflowProvider(workflow),
        new TestActivityExecutor()).Start(
        Loaded(profile),
        graph);
    await blockingActivity.Started;

    run.CancelTask("custom");

    Assert.Equal(TaskExecutionState.Cancelling, run.Snapshot.Tasks["custom"].State);
    var report = await run.Completion;
    Assert.Equal(TaskOutcome.Cancelled, report.Tasks["custom"].Outcome);
    Assert.Empty(executed);
  }

  [Fact]
  public void Definition_RejectsTransitionToUndeclaredState()
  {
    var exception = Assert.Throws<ArgumentException>(() => new TaskWorkflowDefinition(
        "start",
        [
          new TaskWorkflowState(
              "start",
              TaskExecutionState.Running,
              transitions: [TaskWorkflowTransition.Always("missing")])
        ]));

    Assert.Contains("missing", exception.Message);
  }

  [Fact]
  public async Task CustomWorkflow_FailsWhenTransitionLimitIsExceeded()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var workflow = new TaskWorkflowDefinition(
        "loop",
        [
          new TaskWorkflowState(
              "loop",
              TaskExecutionState.Running,
              transitions: [TaskWorkflowTransition.Always("loop")])
        ],
        maxTransitions: 2);

    var report = await CreateHandler(
        new FakeRuntime(),
        new SingleWorkflowProvider(workflow)).Start(
        Loaded(profile),
        graph).Completion;

    Assert.Equal(TaskOutcome.Failed, report.Tasks["custom"].Outcome);
    Assert.Contains("transition limit", report.Tasks["custom"].Error);
  }

  [Fact]
  public async Task DefaultWorkflow_WhenDetectFailureIsUndeclared_DoesNotStartApply()
  {
    var profile = ProfileParser.Parse(ProfileJson.Replace(
        ", \"missingExitCodes\": [1]",
        string.Empty));
    var plan = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var runtime = new FakeRuntime().WithDetect("custom", exitCode: 1, stderr: "access denied");

    var report = await CreateHandler(runtime).Start(Loaded(profile), plan).Completion;

    Assert.Equal(TaskOutcome.Failed, report.Tasks["custom"].Outcome);
    Assert.Collection(
        runtime.Invocations,
        invocation => Assert.Equal(("custom", "detect"), invocation));
  }

  [Fact]
  public async Task SchemaVersionTwoWorkflow_DrivesDeclaredLifecycleCommands()
  {
    var profile = ProfileParser.Parse(DeclarativeWorkflowProfileJson);
    var graph = new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["custom"]);
    var runtime = new FakeRuntime()
        .WithDetect("custom", exitCode: 0, stdout: "custom version 2.5");

    var report = await CreateHandler(runtime).Start(Loaded(profile), graph).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["custom"].Outcome);
    Assert.Equal(
        [("custom", "setup"), ("custom", "detect"), ("custom", "cleanup")],
        runtime.Invocations);
    Assert.Equal(
        [
          WorkflowActivityLocation.Entry,
          WorkflowActivityLocation.Residence,
          WorkflowActivityLocation.Exit
        ],
        report.Tasks["custom"].Steps.Select(step => step.ActivityLocation));
  }

  private static ApplyPlanHandler CreateHandler(
      ITaskRuntime runtime,
      ITaskWorkflowProvider? workflowProvider = null,
      IWorkflowActivityExecutor? activityExecutor = null,
      IDomainEventPublisher? domainEvents = null) =>
      new(
          runtime,
          activityExecutor ?? DefaultWorkflowActivityExecutor.Instance,
          workflowProvider ?? DefaultTaskWorkflowProvider.Instance,
          new ProfileExecutionAuthorizer(new FakeProfileTrustStore()),
          domainEvents ?? NullDomainEventPublisher.Instance);

  private static LoadedProfile Loaded(Wdem.Domain.Profiles.EnvironmentProfile profile) =>
      new(profile, ProfileOrigin.Local, "test-profile.json", "TEST");

  private sealed class SingleWorkflowProvider(TaskWorkflowDefinition workflow)
      : ITaskWorkflowProvider
  {
    public TaskWorkflowDefinition Create(Wdem.Domain.Tasks.TaskDefinition task) => workflow;
  }

  private sealed class RecordingActivity(string id, ICollection<string> executed)
      : WorkflowActivity(id)
  {
    public ICollection<string> Executed { get; } = executed;
  }

  private sealed class FailingActivity(string id) : WorkflowActivity(id);

  private sealed class BlockingActivity(string id) : WorkflowActivity(id)
  {
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
      _started.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken);
    }
  }

  private sealed class RecordingDomainEventPublisher : IDomainEventPublisher
  {
    public List<IDomainEvent> Events { get; } = [];

    public void Publish(IDomainEvent domainEvent) => Events.Add(domainEvent);
  }

  private sealed class TestActivityExecutor : IWorkflowActivityExecutor
  {
    public async Task<WorkflowActivityResult> ExecuteAsync(
        WorkflowActivity activity,
        WorkflowActivityContext context,
        CancellationToken cancellationToken)
    {
      switch (activity)
      {
        case RecordingActivity recording:
          recording.Executed.Add(recording.Id);
          return WorkflowActivityResult.Success();
        case FailingActivity:
          return WorkflowActivityResult.Failure("Expected failure.");
        case BlockingActivity blocking:
          await blocking.WaitAsync(cancellationToken);
          return WorkflowActivityResult.Success();
        default:
          return await DefaultWorkflowActivityExecutor.Instance.ExecuteAsync(
              activity,
              context,
              cancellationToken);
      }
    }
  }

  private const string ProfileJson = """
    {
      "id": "workflow-test",
      "version": "1.0.0",
      "displayName": "Workflow test",
      "tasks": {
        "custom": {
          "displayName": "Custom",
          "required": true,
          "version": ">= 2.0",
          "detect": { "executable": "custom", "arguments": ["detect"], "missingExitCodes": [1] },
          "apply": { "executable": "custom", "arguments": ["apply"] }
        }
      }
    }
    """;

  private const string DeclarativeWorkflowProfileJson = """
    {
      "schemaVersion": 2,
      "id": "declarative-workflow-test",
      "version": "1.0.0",
      "displayName": "Declarative workflow test",
      "tasks": {
        "custom": {
          "displayName": "Custom",
          "required": true,
          "detect": { "executable": "custom", "arguments": ["detect"] },
          "apply": { "executable": "custom", "arguments": ["apply"] },
          "workflow": {
            "initialState": "configure",
            "states": [
              {
                "id": "configure",
                "taskState": "Running",
                "entry": [
                  {
                    "id": "setup",
                    "phase": "setup",
                    "executable": "custom",
                    "arguments": ["setup"]
                  }
                ],
                "residence": [
                  {
                    "id": "configure",
                    "phase": "detect",
                    "executable": "custom",
                    "arguments": ["configure"],
                    "versionPattern": "custom version (?<version>\\d+(?:\\.\\d+)+)"
                  }
                ],
                "exit": [
                  {
                    "id": "cleanup",
                    "phase": "cleanup",
                    "executable": "custom",
                    "arguments": ["cleanup"]
                  }
                ],
                "transitions": [
                  { "target": "done", "condition": "taskSatisfied" },
                  { "target": "failed", "condition": "taskNotSatisfied" }
                ]
              },
              {
                "id": "done",
                "taskState": "Succeeded",
                "outcome": "Succeeded"
              },
              {
                "id": "failed",
                "taskState": "Failed",
                "outcome": "Failed"
              }
            ]
          }
        }
      }
    }
    """;
}
