using Wdem.Application.Events;
using Wdem.Application.Logging;
using Wdem.Domain.Events;

namespace Wdem.Infrastructure.Logging;

public sealed class DomainEventSessionLogHandler(ISessionLog sessionLog) : IDomainEventHandler
{
  public void Handle(IDomainEvent domainEvent)
  {
    ArgumentNullException.ThrowIfNull(domainEvent);

    sessionLog.Write(
        "domain_event",
        Describe(domainEvent),
        ToLogData(domainEvent));
  }

  private static string Describe(IDomainEvent domainEvent) => domainEvent switch
  {
    TaskWorkflowStarted item => $"Task '{item.TaskId}' workflow started.",
    TaskWorkflowStateEntered item =>
        $"Task '{item.TaskId}' entered workflow state '{item.StateId}'.",
    TaskWorkflowActivityStarted item =>
        $"Task '{item.TaskId}' started Activity '{item.ActivityId}'.",
    TaskWorkflowActivityCompleted item =>
        $"Task '{item.TaskId}' completed Activity '{item.ActivityId}' with success={item.Succeeded}.",
    TaskWorkflowTransitioned item =>
        $"Task '{item.TaskId}' transitioned from '{item.FromStateId}' to '{item.ToStateId}'.",
    TaskWorkflowFinished item =>
        $"Task '{item.TaskId}' finished with outcome '{item.Outcome}'.",
    _ => domainEvent.GetType().Name
  };

  private static object ToLogData(IDomainEvent domainEvent) => domainEvent switch
  {
    TaskWorkflowStarted item => new
    {
      Event = nameof(TaskWorkflowStarted),
      item.ProfileId,
      item.TaskId,
      item.InitialStateId
    },
    TaskWorkflowStateEntered item => new
    {
      Event = nameof(TaskWorkflowStateEntered),
      item.ProfileId,
      item.TaskId,
      item.StateId,
      TaskState = item.TaskState.ToString()
    },
    TaskWorkflowActivityStarted item => new
    {
      Event = nameof(TaskWorkflowActivityStarted),
      item.ProfileId,
      item.TaskId,
      item.StateId,
      item.ActivityId,
      Location = item.Location.ToString(),
      item.ActivityIndex
    },
    TaskWorkflowActivityCompleted item => new
    {
      Event = nameof(TaskWorkflowActivityCompleted),
      item.ProfileId,
      item.TaskId,
      item.StateId,
      item.ActivityId,
      Location = item.Location.ToString(),
      item.ActivityIndex,
      item.Succeeded,
      item.IsTaskSatisfied,
      item.Error
    },
    TaskWorkflowTransitioned item => new
    {
      Event = nameof(TaskWorkflowTransitioned),
      item.ProfileId,
      item.TaskId,
      item.FromStateId,
      item.ToStateId,
      item.TransitionName
    },
    TaskWorkflowFinished item => new
    {
      Event = nameof(TaskWorkflowFinished),
      item.ProfileId,
      item.TaskId,
      item.StateId,
      Outcome = item.Outcome.ToString(),
      item.Error
    },
    _ => new { Event = domainEvent.GetType().Name }
  };
}
