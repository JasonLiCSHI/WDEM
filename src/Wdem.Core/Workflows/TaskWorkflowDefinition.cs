using System.Collections.ObjectModel;

namespace Wdem.Core.Workflows;

public sealed class TaskWorkflowDefinition
{
  public TaskWorkflowDefinition(
      string initialStateId,
      IEnumerable<TaskWorkflowState> states,
      int maxTransitions = 1024)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(initialStateId);
    ArgumentNullException.ThrowIfNull(states);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTransitions);

    var byId = new Dictionary<string, TaskWorkflowState>(StringComparer.Ordinal);
    foreach (var state in states)
    {
      ArgumentNullException.ThrowIfNull(state);
      if (!byId.TryAdd(state.Id, state))
      {
        throw new ArgumentException($"Workflow state '{state.Id}' is declared more than once.", nameof(states));
      }
    }

    if (!byId.ContainsKey(initialStateId))
    {
      throw new ArgumentException($"Initial workflow state '{initialStateId}' is not declared.", nameof(initialStateId));
    }

    foreach (var state in byId.Values)
    {
      if (state.IsTerminal && state.Transitions.Count > 0)
      {
        throw new ArgumentException($"Terminal workflow state '{state.Id}' cannot declare transitions.", nameof(states));
      }
      if (!state.IsTerminal && state.Transitions.Count == 0)
      {
        throw new ArgumentException($"Workflow state '{state.Id}' must declare a transition or terminal outcome.", nameof(states));
      }
      foreach (var transition in state.Transitions)
      {
        if (!byId.ContainsKey(transition.TargetStateId))
        {
          throw new ArgumentException(
              $"Workflow state '{state.Id}' targets undeclared state '{transition.TargetStateId}'.",
              nameof(states));
        }
      }
    }

    InitialStateId = initialStateId;
    States = new ReadOnlyDictionary<string, TaskWorkflowState>(byId);
    MaxTransitions = maxTransitions;
    ActivityCount = byId.Values.Sum(state => state.ActivityCount);
  }

  public string InitialStateId { get; }

  public IReadOnlyDictionary<string, TaskWorkflowState> States { get; }

  public int MaxTransitions { get; }

  public int ActivityCount { get; }
}
