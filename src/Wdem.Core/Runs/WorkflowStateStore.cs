using System.Collections.ObjectModel;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;

namespace Wdem.Core.Runs;

/// <summary>
/// Owns the authoritative workflow/task state and publishes immutable projections.
/// Activity code can only execute after a successful state transition through this module.
/// </summary>
internal sealed class WorkflowStateStore
{
  private readonly object _gate = new();
  private readonly Dictionary<string, TaskState> _tasks;
  private readonly IProgress<WorkflowProgress>? _progress;
  private readonly IProgress<WorkflowUpdate>? _updates;
  private WorkflowRunState _runState;
  private WorkflowSnapshot _snapshot;
  private long _revision;

  public WorkflowStateStore(
      EnvironmentProfile profile,
      TaskGraph graph,
      IProgress<WorkflowProgress>? progress,
      IProgress<WorkflowUpdate>? updates)
  {
    _progress = progress;
    _updates = updates;
    var planned = graph.OrderedTaskIds.ToHashSet(StringComparer.Ordinal);
    _tasks = profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => new TaskState(
            task.Required,
            planned.Contains(task.Id),
            planned.Contains(task.Id) ? TaskExecutionState.Pending : TaskExecutionState.NotSelected,
            Stage: null,
            Percent: 0,
            Outcome: null,
            ActivityIndex: 0,
            ActivityCount: 2 + task.Pre.Count + task.Post.Count + (task.Apply is null ? 0 : 1)),
        StringComparer.Ordinal);
    _runState = planned.Count == 0 ? WorkflowRunState.Completed : WorkflowRunState.Running;
    _snapshot = CreateSnapshotLocked();
  }

  public WorkflowSnapshot Snapshot
  {
    get
    {
      lock (_gate)
      {
        return _snapshot;
      }
    }
  }

  public static WorkflowSnapshot CreateReadySnapshot(EnvironmentProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    var tasks = profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => new WorkflowTaskSnapshot(
            task.Id,
            TaskExecutionState.Ready,
            Stage: null,
            Percent: 0,
            Outcome: null,
            IsPlanned: false,
            ActivityIndex: 0,
            ActivityCount: 2 + task.Pre.Count + task.Post.Count + (task.Apply is null ? 0 : 1),
            new TaskCapabilities(
                CanStart: true,
                CanCancel: false,
                CanSelect: !task.Required)),
        StringComparer.Ordinal);
    return new WorkflowSnapshot(
        Revision: 0,
        WorkflowRunState.Ready,
        new ReadOnlyDictionary<string, WorkflowTaskSnapshot>(tasks));
  }

  public bool MakeReady(string taskId)
  {
    WorkflowUpdate update;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.State == TaskExecutionState.Cancelling)
      {
        return false;
      }

      EnsureTransition(current.State, TaskExecutionState.Ready);
      _tasks[taskId] = current with { State = TaskExecutionState.Ready };
      update = AdvanceLocked(CreateProgressLocked(taskId));
    }

    Publish(update);
    return true;
  }

  public bool EnterActivity(
      string taskId,
      TaskExecutionState state,
      string stage,
      int activityIndex)
  {
    WorkflowUpdate update;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.State == TaskExecutionState.Cancelling)
      {
        return false;
      }

      EnsureTransition(current.State, state);
      var percent = current.ActivityCount == 0
          ? 0
          : Math.Clamp((activityIndex - 1) * 100 / current.ActivityCount, 0, 99);
      _tasks[taskId] = current with
      {
        State = state,
        Stage = stage,
        Percent = percent,
        ActivityIndex = activityIndex
      };
      update = AdvanceLocked(CreateProgressLocked(taskId));
    }

    Publish(update);
    return true;
  }

  public TaskOutcome CompleteTask(string taskId, TaskOutcome outcome)
  {
    WorkflowUpdate update;
    TaskOutcome effectiveOutcome;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.IsTerminal)
      {
        return current.Outcome ?? outcome;
      }

      effectiveOutcome = current.State == TaskExecutionState.Cancelling
          ? TaskOutcome.Cancelled
          : outcome;
      var terminalState = effectiveOutcome switch
      {
        TaskOutcome.Succeeded => TaskExecutionState.Succeeded,
        TaskOutcome.NotRequired => TaskExecutionState.Satisfied,
        TaskOutcome.Failed => TaskExecutionState.Failed,
        TaskOutcome.Cancelled => TaskExecutionState.Cancelled,
        TaskOutcome.Blocked => TaskExecutionState.Blocked,
        _ => TaskExecutionState.NotSelected
      };
      EnsureTransition(current.State, terminalState);
      _tasks[taskId] = current with
      {
        State = terminalState,
        Stage = null,
        Percent = 100,
        Outcome = effectiveOutcome,
        ActivityIndex = current.ActivityCount
      };
      CompleteWorkflowIfTerminalLocked();
      update = AdvanceLocked(CreateProgressLocked(taskId));
    }

    Publish(update);
    return effectiveOutcome;
  }

  public void PublishOutput(
      string taskId,
      string message,
      WorkflowOutputStream stream)
  {
    WorkflowUpdate update;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      var change = new WorkflowProgress(
          taskId,
          current.State,
          current.Stage,
          current.Percent,
          current.Outcome,
          message,
          stream);
      update = AdvanceLocked(change);
    }

    Publish(update);
  }

  public bool RequestCancelTask(string taskId)
  {
    WorkflowUpdate update;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (!CreateTaskSnapshotLocked(taskId, current).CanCancel)
      {
        return false;
      }

      EnsureTransition(current.State, TaskExecutionState.Cancelling);
      _tasks[taskId] = current with { State = TaskExecutionState.Cancelling };
      update = AdvanceLocked(CreateProgressLocked(taskId));
    }

    Publish(update);
    return true;
  }

  public bool RequestCancelAll()
  {
    WorkflowUpdate update;
    lock (_gate)
    {
      if (_runState != WorkflowRunState.Running)
      {
        return false;
      }

      _runState = WorkflowRunState.Cancelling;
      foreach (var (taskId, current) in _tasks.ToArray())
      {
        if (current.IsPlanned && IsCancellable(current.State))
        {
          EnsureTransition(current.State, TaskExecutionState.Cancelling);
          _tasks[taskId] = current with { State = TaskExecutionState.Cancelling };
        }
      }

      update = AdvanceLocked(Change: null);
    }

    Publish(update);
    return true;
  }

  private WorkflowUpdate AdvanceLocked(WorkflowProgress? Change)
  {
    _revision++;
    _snapshot = CreateSnapshotLocked();
    return new WorkflowUpdate(_snapshot, Change);
  }

  private WorkflowSnapshot CreateSnapshotLocked()
  {
    var tasks = _tasks.ToDictionary(
        pair => pair.Key,
        pair => CreateTaskSnapshotLocked(pair.Key, pair.Value),
        StringComparer.Ordinal);
    return new WorkflowSnapshot(
        _revision,
        _runState,
        new ReadOnlyDictionary<string, WorkflowTaskSnapshot>(tasks));
  }

  private WorkflowTaskSnapshot CreateTaskSnapshotLocked(string taskId, TaskState task)
  {
    var idle = _runState is WorkflowRunState.Ready or WorkflowRunState.Completed;
    return new WorkflowTaskSnapshot(
        taskId,
        task.State,
        task.Stage,
        task.Percent,
        task.Outcome,
        task.IsPlanned,
        task.ActivityIndex,
        task.ActivityCount,
        new TaskCapabilities(
            CanStart: idle,
            CanCancel: _runState == WorkflowRunState.Running &&
                task.IsPlanned &&
                IsCancellable(task.State),
            CanSelect: idle && !task.Required));
  }

  private WorkflowProgress CreateProgressLocked(string taskId)
  {
    var task = GetTaskLocked(taskId);
    return new WorkflowProgress(
        taskId,
        task.State,
        task.Stage,
        task.Percent,
        task.Outcome);
  }

  private void CompleteWorkflowIfTerminalLocked()
  {
    if (_tasks.Values.Where(task => task.IsPlanned).All(task => task.IsTerminal))
    {
      _runState = WorkflowRunState.Completed;
    }
  }

  private TaskState GetTaskLocked(string taskId) =>
      _tasks.TryGetValue(taskId, out var task)
          ? task
          : throw new ArgumentException($"Unknown task id '{taskId}'.", nameof(taskId));

  private void Publish(WorkflowUpdate update)
  {
    if (update.Change is { } change)
    {
      _progress?.Report(change);
    }
    _updates?.Report(update);
  }

  private static bool IsCancellable(TaskExecutionState state) => state is
      TaskExecutionState.Pending or
      TaskExecutionState.Ready or
      TaskExecutionState.Detecting or
      TaskExecutionState.RunningPre or
      TaskExecutionState.Applying or
      TaskExecutionState.RunningPost or
      TaskExecutionState.Verifying;

  private static void EnsureTransition(TaskExecutionState from, TaskExecutionState to)
  {
    var allowed = (from, to) switch
    {
      (TaskExecutionState.Pending, TaskExecutionState.Ready) => true,
      (TaskExecutionState.Pending, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.Pending, TaskExecutionState.Blocked) => true,
      (TaskExecutionState.Ready, TaskExecutionState.Detecting) => true,
      (TaskExecutionState.Ready, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.Ready, TaskExecutionState.Failed) => true,
      (TaskExecutionState.Detecting, TaskExecutionState.Satisfied) => true,
      (TaskExecutionState.Detecting, TaskExecutionState.RunningPre) => true,
      (TaskExecutionState.Detecting, TaskExecutionState.Applying) => true,
      (TaskExecutionState.Detecting, TaskExecutionState.Failed) => true,
      (TaskExecutionState.Detecting, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.RunningPre, TaskExecutionState.RunningPre) => true,
      (TaskExecutionState.RunningPre, TaskExecutionState.Applying) => true,
      (TaskExecutionState.RunningPre, TaskExecutionState.Failed) => true,
      (TaskExecutionState.RunningPre, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.Applying, TaskExecutionState.RunningPost) => true,
      (TaskExecutionState.Applying, TaskExecutionState.Verifying) => true,
      (TaskExecutionState.Applying, TaskExecutionState.Failed) => true,
      (TaskExecutionState.Applying, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.RunningPost, TaskExecutionState.RunningPost) => true,
      (TaskExecutionState.RunningPost, TaskExecutionState.Verifying) => true,
      (TaskExecutionState.RunningPost, TaskExecutionState.Failed) => true,
      (TaskExecutionState.RunningPost, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.Verifying, TaskExecutionState.Succeeded) => true,
      (TaskExecutionState.Verifying, TaskExecutionState.Failed) => true,
      (TaskExecutionState.Verifying, TaskExecutionState.Cancelling) => true,
      (TaskExecutionState.Cancelling, TaskExecutionState.Cancelled) => true,
      _ => false
    };

    if (!allowed)
    {
      throw new InvalidOperationException($"Invalid task state transition: {from} -> {to}.");
    }
  }

  private sealed record TaskState(
      bool Required,
      bool IsPlanned,
      TaskExecutionState State,
      string? Stage,
      int Percent,
      TaskOutcome? Outcome,
      int ActivityIndex,
      int ActivityCount)
  {
    public bool IsTerminal => State is
        TaskExecutionState.NotSelected or
        TaskExecutionState.Satisfied or
        TaskExecutionState.Succeeded or
        TaskExecutionState.Failed or
        TaskExecutionState.Cancelled or
        TaskExecutionState.Blocked;
  }
}
