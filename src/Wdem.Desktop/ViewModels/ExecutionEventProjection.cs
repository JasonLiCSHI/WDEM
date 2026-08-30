using Wdem.Core.Execution;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

internal sealed class ExecutionEventProjection
{
  private readonly Dictionary<string, ExecutionState> _resourceStates =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, RestartPolicy> _restartRequirements =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly List<string> _activeResourceIds = [];

  public string? CurrentResource { get; private set; }

  public RestartPolicy RestartRequirement { get; private set; }

  public void Reset()
  {
    _resourceStates.Clear();
    _restartRequirements.Clear();
    _activeResourceIds.Clear();
    CurrentResource = null;
    RestartRequirement = RestartPolicy.NoRestart;
  }

  public void Apply(RunEvent runEvent)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    if (runEvent.ResourceId is { } resourceId)
    {
      if (runEvent.RestartRequirement is { } restartRequirement)
      {
        _restartRequirements[resourceId] = restartRequirement;
        RestartRequirement = _restartRequirements.Values
            .DefaultIfEmpty(RestartPolicy.NoRestart)
            .Max();
      }

      if (runEvent.Kind == RunEventKind.ResourceStateChanged &&
          runEvent.State is { } state)
      {
        _resourceStates[resourceId] = state;
        if (state == ExecutionState.Running)
        {
          MarkActive(resourceId);
        }
        else
        {
          RemoveActive(resourceId);
        }
      }
      else if (runEvent.Kind == RunEventKind.StepProgress &&
               _resourceStates.GetValueOrDefault(resourceId) == ExecutionState.Running)
      {
        MarkActive(resourceId);
      }

      RefreshCurrentResource();
    }

    if (runEvent.Kind == RunEventKind.Completed ||
        (runEvent.Kind == RunEventKind.RunStateChanged &&
         runEvent.State == ExecutionState.Completed))
    {
      _activeResourceIds.Clear();
      CurrentResource = null;
    }
  }

  public void ApplySnapshot(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    _resourceStates.Clear();
    _activeResourceIds.Clear();
    CurrentResource = null;
    _restartRequirements.Clear();
    RestartRequirement = run.RestartRequirements
        .DefaultIfEmpty(RestartPolicy.NoRestart)
        .Max();
  }

  public void Complete()
  {
    _activeResourceIds.Clear();
    CurrentResource = null;
  }

  private void MarkActive(string resourceId)
  {
    RemoveActive(resourceId);
    _activeResourceIds.Add(resourceId);
  }

  private void RemoveActive(string resourceId) =>
      _activeResourceIds.RemoveAll(id =>
          string.Equals(id, resourceId, StringComparison.OrdinalIgnoreCase));

  private void RefreshCurrentResource()
  {
    _activeResourceIds.RemoveAll(id =>
        _resourceStates.GetValueOrDefault(id) != ExecutionState.Running);
    CurrentResource = _activeResourceIds.Count == 0 ? null : _activeResourceIds[^1];
  }
}
