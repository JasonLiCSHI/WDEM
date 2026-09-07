using Wdem.Domain.Workflows;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class TaskWorkflowTransitionTests
{
  public static TheoryData<TaskWorkflowTransition, bool, bool, bool> BuiltInConditions => new()
  {
    { TaskWorkflowTransition.Always("next"), false, false, true },
    { TaskWorkflowTransition.WhenActivitiesSucceeded("next"), true, false, true },
    { TaskWorkflowTransition.WhenActivitiesSucceeded("next"), false, true, false },
    { TaskWorkflowTransition.WhenActivitiesFailed("next"), false, true, true },
    { TaskWorkflowTransition.WhenActivitiesFailed("next"), true, false, false },
    { TaskWorkflowTransition.WhenTaskSatisfied("next"), false, true, true },
    { TaskWorkflowTransition.WhenTaskSatisfied("next"), true, false, false },
    { TaskWorkflowTransition.WhenTaskNotSatisfied("next"), true, false, true },
    { TaskWorkflowTransition.WhenTaskNotSatisfied("next"), false, true, false }
  };

  [Theory]
  [MemberData(nameof(BuiltInConditions))]
  public void BuiltInTransition_WhenEvaluated_ThenMatchesWorkflowFacts(
      TaskWorkflowTransition transition,
      bool activitiesSucceeded,
      bool taskSatisfied,
      bool expected)
  {
    var context = new TaskWorkflowTransitionContext(
        activitiesSucceeded,
        taskSatisfied);

    Assert.Equal(expected, transition.IsMatch(context));
    Assert.Equal("next", transition.TargetStateId);
  }

  [Fact]
  public void CustomTransition_WhenCreated_ThenCanComposeWorkflowFacts()
  {
    var transition = new TaskWorkflowTransition(
        "recover",
        context => !context.ActivitiesSucceeded && context.IsTaskSatisfied,
        "recover-satisfied-task");

    Assert.True(transition.IsMatch(new TaskWorkflowTransitionContext(false, true)));
    Assert.False(transition.IsMatch(new TaskWorkflowTransitionContext(false, false)));
    Assert.Equal("recover-satisfied-task", transition.Name);
  }
}
