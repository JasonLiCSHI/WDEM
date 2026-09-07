using Wdem.Domain.Execution;
using Wdem.Domain.Workflows;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class TaskWorkflowDefinitionTests
{
  [Fact]
  public void Definition_WhenInitialStateIsUnknown_ThenRejectsIt()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new TaskWorkflowDefinition("missing", [Terminal("done")]));

    Assert.Contains("Initial workflow state 'missing'", exception.Message);
  }

  [Fact]
  public void Definition_WhenStateIdsAreDuplicated_ThenRejectsThem()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new TaskWorkflowDefinition("done", [Terminal("done"), Terminal("done")]));

    Assert.Contains("declared more than once", exception.Message);
  }

  [Fact]
  public void Definition_WhenTransitionTargetIsUndeclared_ThenRejectsIt()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new TaskWorkflowDefinition(
            "start",
            [
              new TaskWorkflowState(
                  "start",
                  TaskExecutionState.Running,
                  transitions: [TaskWorkflowTransition.Always("missing")])
            ]));

    Assert.Contains("undeclared state 'missing'", exception.Message);
  }

  [Fact]
  public void Definition_WhenNonTerminalStateHasNoTransition_ThenRejectsIt()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new TaskWorkflowDefinition(
            "start",
            [new TaskWorkflowState("start", TaskExecutionState.Running)]));

    Assert.Contains("must declare a transition", exception.Message);
  }

  [Fact]
  public void Definition_WhenTerminalStateHasTransitions_ThenRejectsIt()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new TaskWorkflowDefinition(
            "done",
            [
              new TaskWorkflowState(
                  "done",
                  TaskExecutionState.Succeeded,
                  transitions: [TaskWorkflowTransition.Always("done")],
                  terminalOutcome: TaskOutcome.Succeeded)
            ]));

    Assert.Contains("Terminal workflow state 'done'", exception.Message);
  }

  [Fact]
  public void Definition_WhenActivitiesAreDeclared_ThenCountsEveryLifecycleActivity()
  {
    var workflow = new TaskWorkflowDefinition(
        "start",
        [
          new TaskWorkflowState(
              "start",
              TaskExecutionState.Running,
              entryActivities: [new TestActivity("entry")],
              residenceActivities: [new TestActivity("residence")],
              exitActivities: [new TestActivity("exit")],
              transitions: [TaskWorkflowTransition.Always("done")]),
          Terminal("done")
        ]);

    Assert.Equal(3, workflow.ActivityCount);
  }

  [Fact]
  public void State_WhenCreated_ThenCopiesLifecycleCollectionsAtBoundary()
  {
    var activities = new List<WorkflowActivity> { new TestActivity("configure") };
    var transitions = new List<TaskWorkflowTransition>
    {
      TaskWorkflowTransition.Always("done")
    };
    var state = new TaskWorkflowState(
        "configure",
        TaskExecutionState.Running,
        residenceActivities: activities,
        transitions: transitions);

    activities.Clear();
    transitions.Clear();

    Assert.Single(state.ResidenceActivities);
    Assert.Single(state.Transitions);
    Assert.Throws<NotSupportedException>(() =>
        ((IList<WorkflowActivity>)state.ResidenceActivities).Clear());
    Assert.Throws<NotSupportedException>(() =>
        ((IList<TaskWorkflowTransition>)state.Transitions).Clear());
  }

  private static TaskWorkflowState Terminal(string id) =>
      new(id, TaskExecutionState.Succeeded, terminalOutcome: TaskOutcome.Succeeded);

  private sealed class TestActivity(string id) : WorkflowActivity(id);
}
