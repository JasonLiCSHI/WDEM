using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runs;
using Wdem.Core.Tests.TestDoubles;
using Wdem.Core.Workflows;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class TaskWorkflowTests
{
  [Fact]
  public async Task CustomWorkflow_ExecutesEntryResidenceAndExitActivitiesInOrder()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = TaskGraph.Build(profile, rootTaskIds: ["custom"]);
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

    var run = EnvironmentManager.StartApply(
        profile,
        graph,
        new FakeRuntime(),
        updates: new InlineProgress<WorkflowUpdate>(updates.Add),
        workflowProvider: new SingleWorkflowProvider(workflow));
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
  public async Task CustomWorkflow_CanRouteActivityFailureToRecoveryState()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = TaskGraph.Build(profile, rootTaskIds: ["custom"]);
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

    var report = await EnvironmentManager.StartApply(
        profile,
        graph,
        new FakeRuntime(),
        workflowProvider: new SingleWorkflowProvider(workflow)).Completion;

    Assert.Equal(TaskOutcome.Succeeded, report.Tasks["custom"].Outcome);
  }

  [Fact]
  public async Task CustomWorkflow_CancellationStopsBeforeExitAndDownstreamStates()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var graph = TaskGraph.Build(profile, rootTaskIds: ["custom"]);
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

    var run = EnvironmentManager.StartApply(
        profile,
        graph,
        new FakeRuntime(),
        workflowProvider: new SingleWorkflowProvider(workflow));
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
    var graph = TaskGraph.Build(profile, rootTaskIds: ["custom"]);
    var workflow = new TaskWorkflowDefinition(
        "loop",
        [
          new TaskWorkflowState(
              "loop",
              TaskExecutionState.Running,
              transitions: [TaskWorkflowTransition.Always("loop")])
        ],
        maxTransitions: 2);

    var report = await EnvironmentManager.StartApply(
        profile,
        graph,
        new FakeRuntime(),
        workflowProvider: new SingleWorkflowProvider(workflow)).Completion;

    Assert.Equal(TaskOutcome.Failed, report.Tasks["custom"].Outcome);
    Assert.Contains("transition limit", report.Tasks["custom"].Error);
  }

  [Fact]
  public async Task SchemaVersionTwoWorkflow_DrivesDeclaredLifecycleCommands()
  {
    var profile = ProfileParser.Parse(DeclarativeWorkflowProfileJson);
    var graph = TaskGraph.Build(profile, rootTaskIds: ["custom"]);
    var runtime = new FakeRuntime()
        .WithDetect("custom", exitCode: 0, stdout: "custom version 2.5");

    var report = await EnvironmentManager.StartApply(profile, graph, runtime).Completion;

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

  private sealed class SingleWorkflowProvider(TaskWorkflowDefinition workflow)
      : ITaskWorkflowProvider
  {
    public TaskWorkflowDefinition Create(Wdem.Core.Tasks.TaskDefinition task) => workflow;
  }

  private sealed class RecordingActivity(string id, ICollection<string> executed)
      : WorkflowActivity(id)
  {
    public override Task<WorkflowActivityResult> ExecuteAsync(
        WorkflowActivityContext context,
        CancellationToken cancellationToken)
    {
      executed.Add(Id);
      return Task.FromResult(WorkflowActivityResult.Success());
    }
  }

  private sealed class FailingActivity(string id) : WorkflowActivity(id)
  {
    public override Task<WorkflowActivityResult> ExecuteAsync(
        WorkflowActivityContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(WorkflowActivityResult.Failure("Expected failure."));
  }

  private sealed class BlockingActivity(string id) : WorkflowActivity(id)
  {
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public override async Task<WorkflowActivityResult> ExecuteAsync(
        WorkflowActivityContext context,
        CancellationToken cancellationToken)
    {
      _started.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken);
      return WorkflowActivityResult.Success();
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
          "detect": { "executable": "custom", "arguments": ["detect"] },
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
