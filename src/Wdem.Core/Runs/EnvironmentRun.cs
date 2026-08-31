namespace Wdem.Core.Runs;

public sealed class EnvironmentRun
{
  private readonly Task<RunReport> _completion;
  private readonly CancellationTokenSource _allCancellationTokenSource;
  private readonly Dictionary<string, CancellationTokenSource> _taskCancellationTokenSources;
  private readonly WorkflowStateStore _state;

  internal EnvironmentRun(
      Task<RunReport> completion,
      CancellationTokenSource allCancellationTokenSource,
      Dictionary<string, CancellationTokenSource> taskCancellationTokenSources,
      WorkflowStateStore state)
  {
    _completion = completion;
    _allCancellationTokenSource = allCancellationTokenSource;
    _taskCancellationTokenSources = taskCancellationTokenSources;
    _state = state;
  }

  public Task<RunReport> Completion => _completion;

  public WorkflowSnapshot Snapshot => _state.Snapshot;

  public void CancelAll()
  {
    if (!_state.RequestCancelAll())
    {
      return;
    }

    _allCancellationTokenSource.Cancel();
    foreach (var taskSource in _taskCancellationTokenSources.Values)
    {
      taskSource.Cancel();
    }
  }

  public void CancelTask(string taskId)
  {
    if (_taskCancellationTokenSources.TryGetValue(taskId, out var source) &&
        _state.RequestCancelTask(taskId))
    {
      source.Cancel();
    }
  }

  internal CancellationToken AllCancellationToken => _allCancellationTokenSource.Token;
}
